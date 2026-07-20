using Entities.DBEntities.ColdStorage;
using Microsoft.Extensions.Logging;
using Models.ColdStorage;

namespace Migration.Engine.Lifecycle;

/// <summary>
/// Persistence-facing contract used by the migrator and restore worker to
/// advance lifecycle status and emit audit log lines. Extracted into an
/// interface so workers can unit-test without standing up a SQL Server.
/// </summary>
public interface IJobStatusWriter
{
    Task<MigrationJobItem?> FindItemAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Most recently created item for a given SharePoint server-relative URL,
    /// regardless of job. Used by the idempotency guard to detect duplicate
    /// requests against the same file.
    /// </summary>
    Task<MigrationJobItem?> FindItemBySpUrlAsync(string serverRelativeUrl, CancellationToken cancellationToken = default);

    Task TransitionAsync(
        Guid itemId,
        MigrationLifecycleStatus newStatus,
        string message,
        Exception? exception = null,
        LogLevel level = LogLevel.Information,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Parks an item in <see cref="MigrationLifecycleStatus.RetryScheduled"/> with a concrete
    /// <paramref name="nextRetryUtc"/> due time (from the SharePoint <c>Retry-After</c> header
    /// when present, else the exponential backoff). Records the retry reason as the friendly
    /// last-error so the UI can show why it's waiting, and persists the optional
    /// <paramref name="retryAfterSeconds"/> for reporting. The retry itself is scheduled on the
    /// bus by the caller; this only advances the lifecycle row.
    /// </summary>
    Task ScheduleRetryAsync(
        Guid itemId,
        DateTime nextRetryUtc,
        int? retryAfterSeconds,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically increments and returns the item's processing-attempt counter. Used to
    /// bound retries (throttle backoff in the pipeline; poison-message bounding in the
    /// message processor) so an item can't be retried forever.
    /// </summary>
    Task<int> IncrementAttemptsAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task LogAsync(
        Guid jobId,
        Guid? itemId,
        MigrationLifecycleStatus status,
        LogLevel level,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default);

    Task RecordCopySuccessAsync(
        Guid itemId,
        string blobContainerName,
        string blobPath,
        string blobUrl,
        string contentMd5Base64,
        CancellationToken cancellationToken = default);

    Task RecordPlaceholderCreatedAsync(
        Guid itemId,
        string placeholderServerRelativeUrl,
        CancellationToken cancellationToken = default);

    Task RecordSourceDeletedAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the original author/editor/timestamps captured from the source
    /// SharePoint item at migration time, so they survive the source delete and
    /// can be surfaced on the placeholder and after a restore.
    /// </summary>
    Task RecordSourceMetadataAsync(
        Guid itemId,
        string? originalCreatedBy,
        string? originalModifiedBy,
        DateTime? originalCreated,
        DateTime? originalModified,
        CancellationToken cancellationToken = default);

    Task RecordRestoredAsync(Guid itemId, string restoredServerRelativeUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// True if a DIFFERENT item is already actively restoring the same placeholder
    /// (cross-process concurrency guard for issue #10). Excludes <paramref name="itemId"/>
    /// itself.
    /// </summary>
    Task<bool> IsRestoreInFlightForOtherItemAsync(
        Guid itemId,
        string placeholderServerRelativeUrl,
        CancellationToken cancellationToken = default);
}
