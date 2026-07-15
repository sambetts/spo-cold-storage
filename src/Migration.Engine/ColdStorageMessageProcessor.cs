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
/// Decouples the cold-storage dispatch logic from the transport so the same
/// code drives both the continuous-WebJob <see cref="ColdStorageBusListener"/>
/// (push <c>ServiceBusProcessor</c>) and the queue-triggered Azure Function
/// (which wakes on messages and needs no Always On).
/// </summary>
public enum MessageOutcome
{
    Complete,
    Abandon,
    DeadLetter,
}

/// <summary>
/// Transport-agnostic core of the cold-storage listener: parses a raw bus
/// message body, routes it to the migrate or restore pipeline (with the legacy
/// <see cref="BaseSharePointFileInfo"/> fallback the indexer still emits), and
/// returns the settlement <see cref="MessageOutcome"/> for the host to apply.
///
/// Holds per-process in-flight guards so a single host doesn't process the same
/// item / placeholder twice concurrently; cross-process duplicates are still
/// coalesced by the DB status guards inside the pipelines.
/// </summary>
public sealed class ColdStorageMessageProcessor(Config config, ILogger logger) : BaseComponent(config, logger)
{
    private readonly ConcurrentDictionary<Guid, byte> _inFlightItems = new();
    private readonly ConcurrentDictionary<string, byte> _legacyInFlight = new(StringComparer.OrdinalIgnoreCase);
    // Serialises concurrent restores of the SAME placeholder on this host so a
    // second in-flight restore can't double-upload (issue #10). Cross-host
    // restores are additionally coalesced by the pipeline's DB status guard.
    private readonly ConcurrentDictionary<string, byte> _inFlightRestorePlaceholders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Processes one raw message body and returns how the host should settle it.
    /// Tries the new <see cref="ColdStorageBusEnvelope"/> first, falls back to the
    /// legacy indexer payload, and dead-letters anything unrecognised.
    /// </summary>
    public async Task<MessageOutcome> ProcessMessageAsync(string body, CancellationToken cancellationToken = default)
    {
        var envelope = TryDeserialiseEnvelope(body);
        if (envelope is not null && envelope.IsValid)
        {
            return await ProcessEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
        }

        // Backward compatibility: indexer continues to publish raw file-info.
        var legacy = TryDeserialiseLegacy(body);
        if (legacy is not null && legacy.IsValidInfo)
        {
            return await ProcessLegacyAsync(legacy, cancellationToken).ConfigureAwait(false);
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

        return success ? MessageOutcome.Complete : MessageOutcome.Abandon;
    }

    private async Task<MessageOutcome> ProcessLegacyAsync(BaseSharePointFileInfo legacy, CancellationToken cancellationToken)
    {
        var key = legacy.FullSharePointUrl;
        if (!_legacyInFlight.TryAdd(key, 0))
        {
            _logger.LogWarning("Legacy file '{Url}' already in flight; deferring message.", key);
            return MessageOutcome.Abandon;
        }
        try
        {
            using var sharePointFileMigrator = new SharePointFileMigrator(_config, _logger);
            var app = await AuthUtils.GetNewClientApp(_config).ConfigureAwait(false);
            try
            {
                _ = await sharePointFileMigrator.MigrateFromSharePointToBlobStorage(legacy, app).ConfigureAwait(false);
                await sharePointFileMigrator.SaveSucessfulFileMigrationToSql(legacy).ConfigureAwait(false);
                return MessageOutcome.Complete;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Legacy migration failed for '{Url}'.", key);
                await sharePointFileMigrator.SaveErrorForFileMigrationToSql(ex, legacy).ConfigureAwait(false);
                return MessageOutcome.Abandon;
            }
        }
        finally
        {
            _legacyInFlight.TryRemove(key, out _);
        }
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

    private static BaseSharePointFileInfo? TryDeserialiseLegacy(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<BaseSharePointFileInfo>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
