using Entities.DBEntities.ColdStorage;
using Microsoft.Extensions.Logging;
using Migration.Engine.Lifecycle;
using Models.ColdStorage;

namespace Migration.Engine.Tests.Providers.InMemory;

/// <summary>
/// In-memory <see cref="IJobStatusWriter"/> for pipeline tests: holds the lifecycle rows in a
/// dictionary and applies exactly the side effects the real <c>JobStatusWriter</c> does (status +
/// the CopiedAt / SourceDeletedAt / PlaceholderCreatedAt / RestoredAt / NextRetryAt / Attempts
/// stamps the pipelines read back), so the orchestration can be driven and asserted without SQL.
/// A recorded <see cref="Transitions"/> log makes it easy to assert the exact path an item took.
/// </summary>
public sealed class InMemoryJobStatusWriter : IJobStatusWriter
{
    private readonly Dictionary<Guid, MigrationJobItem> _items = new();

    /// <summary>Ordered (itemId, status) log of every transition, for asserting the lifecycle path.</summary>
    public List<(Guid ItemId, MigrationLifecycleStatus Status, string Message)> Transitions { get; } = new();

    /// <summary>When set, <see cref="IsRestoreInFlightForOtherItemAsync"/> returns true (simulate a concurrent restore).</summary>
    public bool SimulateRestoreInFlight { get; set; }

    /// <summary>Seed a lifecycle row (e.g. to test a resume from a partially-completed prior attempt).</summary>
    public MigrationJobItem Seed(Guid itemId, Guid jobId, MigrationLifecycleStatus status = MigrationLifecycleStatus.Queued)
    {
        var item = new MigrationJobItem { ItemId = itemId, JobId = jobId, Status = status };
        _items[itemId] = item;
        return item;
    }

    public MigrationJobItem Get(Guid itemId) => _items[itemId];

    private MigrationJobItem Ensure(Guid itemId)
    {
        if (!_items.TryGetValue(itemId, out var item))
        {
            item = new MigrationJobItem { ItemId = itemId };
            _items[itemId] = item;
        }
        return item;
    }

    public Task<MigrationJobItem?> FindItemAsync(Guid itemId, CancellationToken cancellationToken = default)
        => Task.FromResult(_items.TryGetValue(itemId, out var item) ? item : null);

    public Task<MigrationJobItem?> FindItemBySpUrlAsync(string serverRelativeUrl, CancellationToken cancellationToken = default)
        => Task.FromResult<MigrationJobItem?>(_items.Values
            .Where(i => string.Equals(i.SpServerRelativeUrl, serverRelativeUrl, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.CreatedAt).FirstOrDefault());

    public Task TransitionAsync(Guid itemId, MigrationLifecycleStatus newStatus, string message, Exception? exception = null, LogLevel level = LogLevel.Information, CancellationToken cancellationToken = default)
    {
        var item = Ensure(itemId);
        Apply(item, newStatus, message);
        if (IsFailure(newStatus))
        {
            item.LastError = message;
            item.LastErrorDetail = exception?.ToString() ?? message;
        }
        return Task.CompletedTask;
    }

    public Task ScheduleRetryAsync(Guid itemId, DateTime nextRetryUtc, int? retryAfterSeconds, string message, Exception? exception = null, CancellationToken cancellationToken = default)
    {
        var item = Ensure(itemId);
        Apply(item, MigrationLifecycleStatus.RetryScheduled, message);
        item.NextRetryAt = nextRetryUtc;
        item.LastRetryAfterSeconds = retryAfterSeconds;
        item.LastError = message;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAttemptsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var item = Ensure(itemId);
        item.Attempts++;
        return Task.FromResult(item.Attempts);
    }

    public Task LogAsync(Guid jobId, Guid? itemId, MigrationLifecycleStatus status, LogLevel level, string message, Exception? exception = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RecordCopySuccessAsync(Guid itemId, string blobContainerName, string blobPath, string blobUrl, string contentMd5Base64, CancellationToken cancellationToken = default)
    {
        var item = Ensure(itemId);
        item.BlobContainerName = blobContainerName;
        item.BlobPath = blobPath;
        item.BlobUrl = blobUrl;
        item.ContentMd5Base64 = contentMd5Base64;
        item.CopiedAt = DateTime.UtcNow;
        Apply(item, MigrationLifecycleStatus.CopiedToColdStorage, "Copied.");
        return Task.CompletedTask;
    }

    public Task RecordPlaceholderCreatedAsync(Guid itemId, string placeholderServerRelativeUrl, CancellationToken cancellationToken = default)
    {
        var item = Ensure(itemId);
        item.PlaceholderServerRelativeUrl = placeholderServerRelativeUrl;
        item.PlaceholderCreatedAt = DateTime.UtcNow;
        Apply(item, MigrationLifecycleStatus.ColdStorageMigrationCompleted, "Placeholder created. Migration complete.");
        return Task.CompletedTask;
    }

    public Task RecordSourceDeletedAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var item = Ensure(itemId);
        item.SourceDeletedAt = DateTime.UtcNow;
        Apply(item, MigrationLifecycleStatus.PlaceholderCreating, "Source deleted. Creating placeholder.");
        return Task.CompletedTask;
    }

    public Task RecordSourceMetadataAsync(Guid itemId, string? originalCreatedBy, string? originalModifiedBy, DateTime? originalCreated, DateTime? originalModified, CancellationToken cancellationToken = default)
    {
        var item = Ensure(itemId);
        item.OriginalCreatedBy = originalCreatedBy;
        item.OriginalModifiedBy = originalModifiedBy;
        item.OriginalCreated = originalCreated;
        item.SourceLastModified = originalModified ?? item.SourceLastModified;
        return Task.CompletedTask;
    }

    public Task RecordRestoredAsync(Guid itemId, string restoredServerRelativeUrl, CancellationToken cancellationToken = default)
    {
        var item = Ensure(itemId);
        item.RestoredAt = DateTime.UtcNow;
        Apply(item, MigrationLifecycleStatus.RestoredToSharePoint, "Content restored.");
        return Task.CompletedTask;
    }

    public Task<bool> IsRestoreInFlightForOtherItemAsync(Guid itemId, string placeholderServerRelativeUrl, CancellationToken cancellationToken = default)
        => Task.FromResult(SimulateRestoreInFlight);

    public Task<int> CompleteAlreadyArchivedAsync(int maxItems, CancellationToken cancellationToken = default)
    {
        // Reconciliation helper; not exercised by the pipelines under test. Mirror the real rule so
        // it's usable if a future test needs it: complete rows that are fully archived but not marked done.
        var fixedUp = 0;
        foreach (var item in _items.Values
            .Where(i => i.SourceDeletedAt is not null && i.PlaceholderCreatedAt is not null
                        && i.Status != MigrationLifecycleStatus.ColdStorageMigrationCompleted)
            .Take(maxItems <= 0 ? 0 : maxItems))
        {
            Apply(item, MigrationLifecycleStatus.ColdStorageMigrationCompleted, "Reconciled: already archived.");
            item.LastError = null;
            fixedUp++;
        }
        return Task.FromResult(fixedUp);
    }

    private void Apply(MigrationJobItem item, MigrationLifecycleStatus status, string message)
    {
        item.Status = status;
        item.UpdatedAt = DateTime.UtcNow;
        if (status is MigrationLifecycleStatus.Validating or MigrationLifecycleStatus.PostCopyValidation)
        {
            item.ValidatedAt = DateTime.UtcNow;
        }
        if (status is MigrationLifecycleStatus.ColdStorageMigrationCompleted or MigrationLifecycleStatus.RestoreCompleted
            or MigrationLifecycleStatus.Cancelled or MigrationLifecycleStatus.Skipped
            or MigrationLifecycleStatus.ValidationFailed or MigrationLifecycleStatus.CopyToColdStorageFailed)
        {
            item.CompletedAt = DateTime.UtcNow;
        }
        Transitions.Add((item.ItemId, status, message));
    }

    private static bool IsFailure(MigrationLifecycleStatus status) => status switch
    {
        MigrationLifecycleStatus.ValidationFailed => true,
        MigrationLifecycleStatus.CopyToColdStorageFailed => true,
        MigrationLifecycleStatus.DeleteFailed => true,
        MigrationLifecycleStatus.PlaceholderFailed => true,
        MigrationLifecycleStatus.RestoreFailed => true,
        MigrationLifecycleStatus.PlaceholderRemoveFailed => true,
        _ => false,
    };
}
