using Entities;
using Entities.DBEntities.ColdStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Models.ColdStorage;

namespace Migration.Engine.Lifecycle;

/// <summary>
/// Single point of writes for the cold-storage lifecycle tables. Wraps EF so
/// the migrator / restore worker can advance status + emit an audit log line
/// in one call, and so the same idempotency rules apply everywhere.
/// </summary>
public sealed class JobStatusWriter(SPOColdStorageDbContext db, ILogger logger) : IJobStatusWriter
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<MigrationJobItem?> FindItemAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        return await _db.MigrationJobItems
            .Include(i => i.Job)
            .FirstOrDefaultAsync(i => i.ItemId == itemId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MigrationJobItem?> FindItemBySpUrlAsync(string serverRelativeUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serverRelativeUrl))
        {
            return null;
        }
        return await _db.MigrationJobItems
            .Where(i => i.SpServerRelativeUrl == serverRelativeUrl)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task TransitionAsync(
        Guid itemId,
        MigrationLifecycleStatus newStatus,
        string message,
        Exception? exception = null,
        LogLevel level = LogLevel.Information,
        CancellationToken cancellationToken = default)
    {
        var item = await _db.MigrationJobItems.FirstOrDefaultAsync(i => i.ItemId == itemId, cancellationToken);
        if (item == null)
        {
            _logger.LogWarning("JobStatusWriter: item {ItemId} not found, status update '{Status}' dropped.", itemId, newStatus);
            return;
        }

        ApplyTransitionInternal(item, newStatus, message);

        if (exception != null)
        {
            item.LastError = Truncate(FriendlyErrorMapper.ToFriendly(exception, newStatus), 2048);
            item.LastErrorDetail = Truncate(exception.ToString(), 8000);
        }
        else if (IsFailure(newStatus))
        {
            // No exception, but a failure/skip message — still show a friendly summary.
            item.LastError = Truncate(FriendlyErrorMapper.ToFriendly(message, newStatus), 2048);
            item.LastErrorDetail = Truncate(message, 8000);
        }

        await WriteLogAsync(item.JobId, item.ItemId, newStatus, level, message, exception, cancellationToken);

        await UpdateRollupAsync(item.JobId, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task LogAsync(
        Guid jobId,
        Guid? itemId,
        MigrationLifecycleStatus status,
        LogLevel level,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        await WriteLogAsync(jobId, itemId, status, level, message, exception, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RecordCopySuccessAsync(
        Guid itemId,
        string blobContainerName,
        string blobPath,
        string blobUrl,
        string contentMd5Base64,
        CancellationToken cancellationToken = default)
    {
        var item = await _db.MigrationJobItems.FirstOrDefaultAsync(i => i.ItemId == itemId, cancellationToken);
        if (item == null)
        {
            return;
        }
        item.BlobContainerName = blobContainerName;
        item.BlobPath = blobPath;
        item.BlobUrl = blobUrl;
        item.ContentMd5Base64 = contentMd5Base64;
        item.CopiedAt = DateTime.UtcNow;
        ApplyTransitionInternal(item, MigrationLifecycleStatus.CopiedToColdStorage, "Source content copied to cold storage.");
        await WriteLogAsync(item.JobId, item.ItemId, MigrationLifecycleStatus.CopiedToColdStorage, LogLevel.Information,
            $"Copied to '{blobContainerName}/{blobPath}'.", null, cancellationToken);
        await UpdateRollupAsync(item.JobId, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RecordPlaceholderCreatedAsync(
        Guid itemId,
        string placeholderServerRelativeUrl,
        CancellationToken cancellationToken = default)
    {
        var item = await _db.MigrationJobItems.FirstOrDefaultAsync(i => i.ItemId == itemId, cancellationToken);
        if (item == null)
        {
            return;
        }
        item.PlaceholderServerRelativeUrl = placeholderServerRelativeUrl;
        item.PlaceholderCreatedAt = DateTime.UtcNow;
        ApplyTransitionInternal(item, MigrationLifecycleStatus.ColdStorageMigrationCompleted, "Placeholder created. Migration complete.");
        item.CompletedAt = DateTime.UtcNow;
        await WriteLogAsync(item.JobId, item.ItemId, MigrationLifecycleStatus.ColdStorageMigrationCompleted, LogLevel.Information,
            $"Placeholder created at '{placeholderServerRelativeUrl}'.", null, cancellationToken);
        await UpdateRollupAsync(item.JobId, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RecordSourceDeletedAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var item = await _db.MigrationJobItems.FirstOrDefaultAsync(i => i.ItemId == itemId, cancellationToken);
        if (item == null)
        {
            return;
        }
        item.SourceDeletedAt = DateTime.UtcNow;
        ApplyTransitionInternal(item, MigrationLifecycleStatus.PlaceholderCreating, "Source deleted. Creating placeholder.");
        await WriteLogAsync(item.JobId, item.ItemId, MigrationLifecycleStatus.PlaceholderCreating, LogLevel.Information,
            "Source file deleted from SharePoint.", null, cancellationToken);
        await UpdateRollupAsync(item.JobId, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RecordSourceMetadataAsync(
        Guid itemId,
        string? originalCreatedBy,
        string? originalModifiedBy,
        DateTime? originalCreated,
        DateTime? originalModified,
        CancellationToken cancellationToken = default)
    {
        var item = await _db.MigrationJobItems.FirstOrDefaultAsync(i => i.ItemId == itemId, cancellationToken);
        if (item == null)
        {
            return;
        }
        item.OriginalCreatedBy = originalCreatedBy is null ? null : Truncate(originalCreatedBy, 256);
        item.OriginalModifiedBy = originalModifiedBy is null ? null : Truncate(originalModifiedBy, 256);
        item.OriginalCreated = originalCreated;
        if (originalModified.HasValue)
        {
            item.SourceLastModified = originalModified;
        }
        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RecordRestoredAsync(Guid itemId, string restoredServerRelativeUrl, CancellationToken cancellationToken = default)
    {
        var item = await _db.MigrationJobItems.FirstOrDefaultAsync(i => i.ItemId == itemId, cancellationToken);
        if (item == null)
        {
            return;
        }
        item.RestoredAt = DateTime.UtcNow;
        item.SpServerRelativeUrl = restoredServerRelativeUrl;
        ApplyTransitionInternal(item, MigrationLifecycleStatus.RestoredToSharePoint, "Content restored to SharePoint.");
        await WriteLogAsync(item.JobId, item.ItemId, MigrationLifecycleStatus.RestoredToSharePoint, LogLevel.Information,
            $"File restored to '{restoredServerRelativeUrl}'.", null, cancellationToken);
        await UpdateRollupAsync(item.JobId, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> IsRestoreInFlightForOtherItemAsync(
        Guid itemId,
        string placeholderServerRelativeUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(placeholderServerRelativeUrl))
        {
            return false;
        }
        // Active-restore statuses can't be expressed in the EF query via the
        // IsActiveRestore() helper, so enumerate them explicitly here.
        return await _db.MigrationJobItems
            .Where(i => i.ItemId != itemId
                        && i.PlaceholderServerRelativeUrl == placeholderServerRelativeUrl
                        && (i.Status == MigrationLifecycleStatus.RestoreInProgress
                            || i.Status == MigrationLifecycleStatus.RestoredToSharePoint
                            || i.Status == MigrationLifecycleStatus.PostRestoreValidation
                            || i.Status == MigrationLifecycleStatus.PlaceholderRemoving))
            .AnyAsync(cancellationToken);
    }

    /// <summary>
    /// Increments the item's processing-attempt counter and returns the new value.
    /// Used by the worker to bound retries on a poison message. Returns
    /// <see cref="int.MaxValue"/> if the item no longer exists (treat as exhausted).
    /// </summary>
    public async Task<int> IncrementAttemptsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var item = await _db.MigrationJobItems.FirstOrDefaultAsync(i => i.ItemId == itemId, cancellationToken);
        if (item == null)
        {
            return int.MaxValue;
        }
        item.Attempts++;
        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return item.Attempts;
    }

    /// <summary>
    /// Recomputes the job-level rollup status from its items and saves. Used by the
    /// dispatch reconciler after it changes item statuses in bulk, so a job whose
    /// items are all terminal doesn't stay stuck showing an in-progress status.
    /// </summary>
    public async Task RecomputeJobRollupAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await UpdateRollupAsync(jobId, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private void ApplyTransitionInternal(MigrationJobItem item, MigrationLifecycleStatus newStatus, string message)
    {
        var previous = item.Status;
        item.Status = newStatus;
        item.UpdatedAt = DateTime.UtcNow;
        if (newStatus == MigrationLifecycleStatus.Validating
            || newStatus == MigrationLifecycleStatus.PostCopyValidation)
        {
            item.ValidatedAt = DateTime.UtcNow;
        }
        if (newStatus == MigrationLifecycleStatus.RestoreCompleted
            || newStatus == MigrationLifecycleStatus.ColdStorageMigrationCompleted
            || newStatus == MigrationLifecycleStatus.Cancelled
            || newStatus == MigrationLifecycleStatus.Skipped
            || newStatus == MigrationLifecycleStatus.ValidationFailed
            || newStatus == MigrationLifecycleStatus.CopyToColdStorageFailed)
        {
            item.CompletedAt = DateTime.UtcNow;
        }
        _logger.LogDebug("Item {ItemId}: {PrevStatus} -> {NewStatus} ({Message})", item.ItemId, previous, newStatus, message);
    }

    private async Task WriteLogAsync(
        Guid jobId,
        Guid? itemId,
        MigrationLifecycleStatus status,
        LogLevel level,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        var log = new MigrationJobLog
        {
            JobId = jobId,
            ItemId = itemId,
            Timestamp = DateTime.UtcNow,
            Level = (int)level,
            Status = status,
            Message = Truncate(message, 4000),
            Exception = exception?.ToString(),
        };
        await _db.MigrationJobLogs.AddAsync(log, cancellationToken);
    }

    /// <summary>
    /// Aggregates per-item status into a single job-level status. Conservative
    /// rules so the SPFx column doesn't flap.
    /// </summary>
    private async Task UpdateRollupAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _db.MigrationJobs.FirstOrDefaultAsync(j => j.JobId == jobId, cancellationToken);
        if (job == null)
        {
            return;
        }
        var items = await _db.MigrationJobItems.Where(i => i.JobId == jobId).Select(i => i.Status).ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            return;
        }
        job.UpdatedAt = DateTime.UtcNow;

        if (items.All(s => s == MigrationLifecycleStatus.ColdStorageMigrationCompleted))
        {
            job.Status = MigrationLifecycleStatus.ColdStorageMigrationCompleted;
            job.CompletedAt = DateTime.UtcNow;
        }
        else if (items.All(s => s == MigrationLifecycleStatus.RestoreCompleted))
        {
            job.Status = MigrationLifecycleStatus.RestoreCompleted;
            job.CompletedAt = DateTime.UtcNow;
        }
        else if (items.Any(s => s == MigrationLifecycleStatus.CopyToColdStorageFailed
                                || s == MigrationLifecycleStatus.RestoreFailed
                                || s == MigrationLifecycleStatus.PlaceholderFailed
                                || s == MigrationLifecycleStatus.PlaceholderRemoveFailed
                                || s == MigrationLifecycleStatus.DeleteFailed
                                || s == MigrationLifecycleStatus.ValidationFailed
                                || s == MigrationLifecycleStatus.Skipped))
        {
            // At least one terminal failure or skip, but other items may still be
            // running. Surface "completed with warning" once every item is
            // terminal, otherwise leave the job in-progress.
            if (items.All(s => s.IsTerminal()))
            {
                job.Status = MigrationLifecycleStatus.CompletedWithWarning;
                job.CompletedAt = DateTime.UtcNow;
            }
            else
            {
                // Pick the dominant in-progress status without overwriting on every save.
                job.Status = items.FirstOrDefault(s => !s.IsTerminal());
            }
        }
        else
        {
            job.Status = items.FirstOrDefault(s => !s.IsTerminal()) is var inProgress
                && inProgress != default ? inProgress : items[0];
        }
    }

    private static string Truncate(string value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value
            : value[..maxLength];

    private static bool IsFailure(MigrationLifecycleStatus status) => status switch
    {
        MigrationLifecycleStatus.ValidationFailed => true,
        MigrationLifecycleStatus.CopyToColdStorageFailed => true,
        MigrationLifecycleStatus.DeleteFailed => true,
        MigrationLifecycleStatus.PlaceholderFailed => true,
        MigrationLifecycleStatus.RestoreFailed => true,
        MigrationLifecycleStatus.PlaceholderRemoveFailed => true,
        MigrationLifecycleStatus.Skipped => true,
        MigrationLifecycleStatus.Cancelled => true,
        _ => false,
    };
}
