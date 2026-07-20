using Entities;
using Entities.Configuration;
using Microsoft.Extensions.Logging;
using Migration.Engine.Lifecycle;
using Migration.Engine.Migration;
using Migration.Engine.Restore;
using Migration.Engine.Utils;
using Models;
using Models.ColdStorage;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Migration.Engine;

/// <summary>
/// What the host should do with a Service Bus message after processing.
/// Decouples the cold-storage dispatch logic from the transport so the
/// queue-triggered Azure Function (which wakes on messages and needs no
/// Always On) can settle each message correctly.
/// </summary>
public enum MessageOutcome
{
    Complete,
    Abandon,
    DeadLetter,
}

/// <summary>
/// Transport-agnostic core of the cold-storage listener: parses a raw bus
/// message body, routes it to the migrate or restore pipeline, and returns the
/// settlement <see cref="MessageOutcome"/> for the host to apply.
///
/// Holds per-process in-flight guards so a single host doesn't process the same
/// item / placeholder twice concurrently; cross-process duplicates are still
/// coalesced by the DB status guards inside the pipelines.
/// </summary>
public sealed class ColdStorageMessageProcessor(Config config, ILogger logger, IColdStorageQueuePublisher? retryPublisher = null) : BaseComponent(config, logger)
{
    private readonly IColdStorageQueuePublisher? _retryPublisher = retryPublisher;
    private readonly ConcurrentDictionary<Guid, byte> _inFlightItems = new();
    // Serialises concurrent restores of the SAME placeholder on this host so a
    // second in-flight restore can't double-upload (issue #10). Cross-host
    // restores are additionally coalesced by the pipeline's DB status guard.
    private readonly ConcurrentDictionary<string, byte> _inFlightRestorePlaceholders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Processes one raw message body and returns how the host should settle it.
    /// Parses the <see cref="ColdStorageBusEnvelope"/> and dead-letters anything
    /// unrecognised.
    /// </summary>
    public async Task<MessageOutcome> ProcessMessageAsync(string body, CancellationToken cancellationToken = default)
    {
        var envelope = TryDeserialiseEnvelope(body);
        if (envelope is not null && envelope.IsValid)
        {
            return await ProcessEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("Unrecognised cold-storage bus message; sending to dead-letter queue. Body length={Length}", body.Length);
        return MessageOutcome.DeadLetter;
    }

    private async Task<MessageOutcome> ProcessEnvelopeAsync(ColdStorageBusEnvelope envelope, CancellationToken cancellationToken)
    {
        // Per-host placeholder lock for restores: defer a second concurrent
        // restore of the same placeholder so it can't run alongside the first.
        var placeholderKey = envelope.Operation == MigrationOperationKind.Restore
            ? envelope.RestoreTarget?.PlaceholderServerRelativeUrl
            : null;
        if (placeholderKey is not null && !_inFlightRestorePlaceholders.TryAdd(placeholderKey, 0))
        {
            _logger.LogWarning("Restore for placeholder '{Key}' already in flight on this host; deferring message.", placeholderKey);
            return MessageOutcome.Abandon;
        }

        if (!_inFlightItems.TryAdd(envelope.ItemId, 0))
        {
            _logger.LogWarning("Item {ItemId} already in flight on this host; deferring message.", envelope.ItemId);
            if (placeholderKey is not null)
            {
                _inFlightRestorePlaceholders.TryRemove(placeholderKey, out _);
            }
            return MessageOutcome.Abandon;
        }

        bool success;
        try
        {
            using var db = new SPOColdStorageDbContext(_config);
            var writer = new JobStatusWriter(db, _logger);

            // Honour admin queue control (issue #16): if the item was cancelled or
            // already finished after the message was enqueued, do no work and let
            // the message complete.
            var current = await writer.FindItemAsync(envelope.ItemId, cancellationToken).ConfigureAwait(false);
            if (current is not null && current.Status.IsTerminal())
            {
                _logger.LogInformation("Item {ItemId} is already {Status} (e.g. admin-cancelled); skipping.", envelope.ItemId, current.Status);
                success = true;
            }
            else if (envelope.Operation == MigrationOperationKind.Migrate)
            {
                var pipeline = new ColdStorageMigratorPipeline(_config, _logger, writer);
                var app = await AuthUtils.GetNewClientApp(_config).ConfigureAwait(false);
                success = await pipeline.ProcessAsync(envelope, app, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var pipeline = new SharePointRestorePipeline(_config, _logger, writer);
                success = await pipeline.ProcessAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled cold-storage pipeline error for item {ItemId}.", envelope.ItemId);
            success = false;
        }
        finally
        {
            _inFlightItems.TryRemove(envelope.ItemId, out _);
            if (placeholderKey is not null)
            {
                _inFlightRestorePlaceholders.TryRemove(placeholderKey, out _);
            }
        }

        if (success)
        {
            return MessageOutcome.Complete;
        }

        // Bound retries so a poison item can't loop forever (the redelivery storm we
        // saw when SQL was briefly unreachable made hundreds of attempts per item).
        // After the configured ceiling, mark the item terminally failed and
        // dead-letter the message so it lands on the DLQ (firing the depth alert)
        // instead of being abandoned and endlessly redelivered. The source is always
        // left intact — a failed migrate never deletes the SharePoint file.
        var maxAttempts = _config.ColdStorageMaxProcessAttempts > 0 ? _config.ColdStorageMaxProcessAttempts : 5;
        try
        {
            using var db = new SPOColdStorageDbContext(_config);
            var writer = new JobStatusWriter(db, _logger);
            var latest = await writer.FindItemAsync(envelope.ItemId, cancellationToken).ConfigureAwait(false);
            if (latest is not null && latest.Status.IsTerminal())
            {
                // The pipeline already recorded a terminal outcome for this item
                // (e.g. PlaceholderFailed after the source was deleted). Don't relabel
                // it or redeliver — the result is already persisted.
                return MessageOutcome.Complete;
            }
            if (latest is not null && latest.Status == MigrationLifecycleStatus.RetryScheduled)
            {
                // The pipeline parked this item with a concrete NextRetryAt. Schedule the retry
                // directly on the bus for that time so it resumes reliably even when the Function
                // idles between bursts (an in-process reconciler timer can't be relied on then).
                // The dispatch reconciler stays a late safety net if the scheduled message never
                // fires. Abandoning here would redeliver immediately (defeating the backoff);
                // dead-lettering would strand it.
                if (_retryPublisher is not null && latest.NextRetryAt is DateTime dueUtc)
                {
                    try
                    {
                        var enqueueAt = dueUtc <= DateTime.UtcNow
                            ? DateTimeOffset.UtcNow.AddSeconds(1)
                            : new DateTimeOffset(DateTime.SpecifyKind(dueUtc, DateTimeKind.Utc));
                        await _retryPublisher.ScheduleAsync(envelope, enqueueAt, cancellationToken).ConfigureAwait(false);
                        _logger.LogInformation("Item {ItemId} throttled; scheduled automatic retry for {Due:o}.", envelope.ItemId, enqueueAt);
                        return MessageOutcome.Complete;
                    }
                    catch (Exception ex)
                    {
                        // Couldn't schedule the retry — don't complete (that would strand the item
                        // until the reconciler happens to run). Abandon so Service Bus redelivers it
                        // and the next attempt reschedules.
                        _logger.LogError(ex, "Failed to schedule bus retry for item {ItemId}; abandoning for redelivery.", envelope.ItemId);
                        return MessageOutcome.Abandon;
                    }
                }
                // No retry publisher wired (legacy host) — complete and rely on the reconciler.
                return MessageOutcome.Complete;
            }
            var attempts = await writer.IncrementAttemptsAsync(envelope.ItemId, cancellationToken).ConfigureAwait(false);
            if (attempts >= maxAttempts)
            {
                await writer.TransitionAsync(
                    envelope.ItemId,
                    envelope.Operation == MigrationOperationKind.Migrate
                        ? MigrationLifecycleStatus.CopyToColdStorageFailed
                        : MigrationLifecycleStatus.RestoreFailed,
                    $"Failed {attempts} time(s); giving up and dead-lettering the message. The source was left intact.",
                    level: LogLevel.Error,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Item {ItemId} dead-lettered after {Attempts} failed attempts.", envelope.ItemId, attempts);
                return MessageOutcome.DeadLetter;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record poison/attempt state for item {ItemId}; abandoning for retry.", envelope.ItemId);
        }

        return MessageOutcome.Abandon;
    }

    private static ColdStorageBusEnvelope? TryDeserialiseEnvelope(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<ColdStorageBusEnvelope>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
