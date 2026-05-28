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

    Task RecordRestoredAsync(Guid itemId, string restoredServerRelativeUrl, CancellationToken cancellationToken = default);
}
