using Entities;
using Entities.Configuration;
using Entities.DBEntities.ColdStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Migration.Engine.Lifecycle;
using Migration.Engine.Migration;
using Migration.Engine.Restore;
using Migration.Engine.Settings;
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

    private readonly IColdStorageSettingsSource _settings = new DbColdStorageSettingsSource(config, logger);

    /// <summary>
    /// True when version-history preservation is switched on right now — the portal
    /// setting (cold_storage_settings) wins, falling back to this host's app setting.
    /// </summary>
    private async Task<bool> IsVersionHistoryEnabledAsync(CancellationToken cancellationToken)
        => await _settings.GetIntAsync(
            ColdStorageSettingKeys.CaptureVersionHistory,
            _config.ColdStorageCaptureVersionHistory,
            cancellationToken).ConfigureAwait(false) > 0;

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

    /// <summary>
    /// True when every SharePoint path in <paramref name="envelope"/> belongs to the site the job
    /// was authorized against. The job row is server-side data (written from the site the caller
    /// passed the contributor check for), so it is the authority — the envelope is not.
    /// <para>
    /// Fails closed: a missing job, a job with no recorded site, or any path outside that site
    /// returns false. That covers messages already on the bus at deploy time and rows written
    /// before the API-side scope checks existed.
    /// </para>
    /// </summary>
    public static bool IsEnvelopeInJobScope(ColdStorageBusEnvelope envelope, MigrationJob? job, out string reason)
    {
        if (job is null)
        {
            reason = "the job row no longer exists";
            return false;
        }
        if (string.IsNullOrWhiteSpace(job.SiteUrl))
        {
            reason = "the job has no recorded site to authorize against";
            return false;
        }
        var site = job.SiteUrl;

        if (envelope.Operation == MigrationOperationKind.Migrate)
        {
            var file = envelope.File;
            if (file is null)
            {
                reason = "the migrate envelope carries no file";
                return false;
            }
            if (!SharePointScope.IsSameSite(site, file.SiteUrl))
            {
                reason = $"site '{file.SiteUrl}' != '{site}'";
                return false;
            }
            if (!string.IsNullOrEmpty(file.WebUrl) && !SharePointScope.IsWebWithinSite(site, file.WebUrl))
            {
                reason = $"web '{file.WebUrl}' is outside '{site}'";
                return false;
            }
            if (!SharePointScope.IsWithinSite(site, file.ServerRelativeFilePath))
            {
                reason = $"path '{file.ServerRelativeFilePath}' is outside '{site}'";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        var target = envelope.RestoreTarget;
        if (target is null)
        {
            reason = "the restore envelope carries no target";
            return false;
        }
        if (!SharePointScope.IsSameSite(site, target.SiteUrl))
        {
            reason = $"site '{target.SiteUrl}' != '{site}'";
            return false;
        }
        if (!string.IsNullOrEmpty(target.WebUrl) && !SharePointScope.IsWebWithinSite(site, target.WebUrl))
        {
            reason = $"web '{target.WebUrl}' is outside '{site}'";
            return false;
        }
        if (!string.IsNullOrEmpty(target.OriginalServerRelativeUrl)
            && !SharePointScope.IsWithinSite(site, target.OriginalServerRelativeUrl))
        {
            reason = $"destination '{target.OriginalServerRelativeUrl}' is outside '{site}'";
            return false;
        }
        // The placeholder is *read* (and deleted) on SharePoint, so it must be in scope too.
        if (!string.IsNullOrEmpty(target.PlaceholderServerRelativeUrl)
            && !SharePointScope.IsWithinSite(site, target.PlaceholderServerRelativeUrl))
        {
            reason = $"placeholder '{target.PlaceholderServerRelativeUrl}' is outside '{site}'";
            return false;
        }
        // The blob key encodes "{host}/{server-relative path}", so a blob-driven restore can name
        // another site's — or another host's — archive. Hold it to the same scope, host included.
        if (!string.IsNullOrEmpty(target.BlobPath) && !SharePointScope.IsBlobKeyWithinSite(site, target.BlobPath))
        {
            reason = $"archive '{target.BlobPath}' originates outside '{site}'";
            return false;
        }
        reason = string.Empty;
        return true;
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

            // Authorization backstop. The API scope-checks every caller-supplied path before it
            // queues work, but that does not cover a message already sitting on the bus, a row
            // written before those checks existed, or any future producer that forgets. The job row
            // is server-side data, so re-assert here — the one point every envelope flows through —
            // that the envelope's SharePoint paths belong to the job's authorized site. The worker
            // acts app-only with Sites.FullControl.All, so anything out of scope is dead-lettered
            // rather than executed.
            var job = await db.MigrationJobs
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.JobId == envelope.JobId, cancellationToken)
                .ConfigureAwait(false);
            if (!IsEnvelopeInJobScope(envelope, job, out var scopeReason))
            {
                _logger.LogError(
                    "Item {ItemId}: envelope is outside its job's authorized site ({Reason}); dead-lettering instead of acting app-only.",
                    envelope.ItemId, scopeReason);
                await writer.TransitionAsync(envelope.ItemId, MigrationLifecycleStatus.ValidationFailed,
                    $"Refused: the queued request targets content outside the job's authorized site ({scopeReason}).",
                    level: LogLevel.Error, cancellationToken: cancellationToken).ConfigureAwait(false);
                return MessageOutcome.DeadLetter;
            }

            if (current is not null && current.Status.IsTerminal())
            {
                _logger.LogInformation("Item {ItemId} is already {Status} (e.g. admin-cancelled); skipping.", envelope.ItemId, current.Status);
                success = true;
            }
            else if (_config.ColdStorageUseProviderPipelines > 0
                     && !await IsVersionHistoryEnabledAsync(cancellationToken).ConfigureAwait(false))
            {
                // Provider-abstraction path (feature-flagged foundation): identical behaviour + guards
                // via ISourceStore/IColdStore, proven by the in-memory unit tests. Legacy inline
                // pipelines remain the default until this is validated in a non-prod environment.
                success = await ProcessViaProviderPipelinesAsync(envelope, writer, cancellationToken).ConfigureAwait(false);
            }
            else if (envelope.Operation == MigrationOperationKind.Migrate)
            {
                if (_config.ColdStorageUseProviderPipelines > 0)
                {
                    // Guard (issue #66): the provider pipelines have no version-history support, so
                    // running them with preservation enabled would archive only the current version
                    // and then delete the source — silently destroying history we promised to keep.
                    // Refuse the combination by falling back to the legacy pipelines, loudly.
                    _logger.LogError(
                        "ColdStorageUseProviderPipelines is enabled but so is version-history preservation, " +
                        "which the provider pipelines do not support. Falling back to the legacy pipelines for " +
                        "item {ItemId}. Turn one of them off to silence this.", envelope.ItemId);
                }
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

    /// <summary>
    /// Feature-flagged provider-abstraction dispatch: runs the same migrate/restore logic through the
    /// provider-neutral pipelines over the SharePoint + Azure Blob adaptors. Maps the bus envelope to
    /// the neutral request records; the pipelines + adaptors carry every guard the legacy path did.
    /// </summary>
    private async Task<bool> ProcessViaProviderPipelinesAsync(ColdStorageBusEnvelope envelope, JobStatusWriter writer, CancellationToken cancellationToken)
    {
        var options = Providers.TransferPipelineOptions.FromConfig(_config);
        var source = new Providers.SharePoint.SharePointSourceStore(_config, _logger);
        var cold = new Providers.AzureBlob.AzureBlobColdStore(_config, _logger);

        if (envelope.Operation == MigrationOperationKind.Migrate)
        {
            var file = envelope.File!;
            var eligibility = new ArchiveEligibilityEvaluator(
                _config,
                new DbArchiveExclusionSource(_config, _logger),
                new DbFileReadActivitySource(_config, _logger),
                new DbArchiveExtensionPolicySource(_config, _logger));
            var pipeline = new Providers.MigratePipeline(options, _logger, writer, source, cold, eligibility);
            var request = new Providers.MigrateRequest
            {
                JobId = envelope.JobId,
                ItemId = envelope.ItemId,
                Source = new Providers.SourceItemRef(file.SiteUrl, file.WebUrl, file.ServerRelativeFilePath),
                Cold = new Providers.ColdStorageKey(envelope.ContainerName, ColdStorageBlobKey.Build(file.SiteUrl, file.ServerRelativeFilePath)),
                SourceLastModifiedUtc = file.LastModified,
                SourceCreatedUtc = file.CreatedDate,
                SourceSizeHint = file.FileSize,
                RequestedByUpn = envelope.RequestedByUpn,
                CopyMetadataColumns = envelope.CopyMetadataColumns,
                DriveId = file.DriveId,
                GraphItemId = file.GraphItemId,
            };
            return await pipeline.ProcessAsync(request, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var target = envelope.RestoreTarget!;
            var pipeline = new Providers.RestorePipeline(options, _logger, writer, source, cold);
            var request = new Providers.RestoreRequest
            {
                JobId = envelope.JobId,
                ItemId = envelope.ItemId,
                Pointer = new Providers.SourceItemRef(target.SiteUrl, target.WebUrl, target.PlaceholderServerRelativeUrl),
                Destination = string.IsNullOrEmpty(target.OriginalServerRelativeUrl)
                    ? null
                    : new Providers.SourceItemRef(target.SiteUrl, target.WebUrl, target.OriginalServerRelativeUrl),
                ConflictBehavior = envelope.ConflictBehavior,
                DeleteColdAfterRestore = _config.ColdStorageDeleteBlobAfterRestore > 0,
            };
            return await pipeline.ProcessAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
