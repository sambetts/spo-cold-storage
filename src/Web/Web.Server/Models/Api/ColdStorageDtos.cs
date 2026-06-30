using Models.ColdStorage;

namespace Web.Models.Api;

/// <summary>
/// Request body for <c>POST /api/migrations/start</c>.
/// </summary>
public class StartMigrationRequest
{
    public string SiteUrl { get; set; } = string.Empty;
    public string WebUrl { get; set; } = string.Empty;
    public string? ListId { get; set; }

    /// <summary>
    /// Stable name of the configured cold-storage container to write into.
    /// </summary>
    public string ContainerName { get; set; } = string.Empty;

    public bool Recursive { get; set; }

    public List<StartMigrationItem> Items { get; set; } = [];
}

public class StartMigrationItem
{
    public string ServerRelativeUrl { get; set; } = string.Empty;
    public ColdStorageItemKind ItemKind { get; set; } = ColdStorageItemKind.File;
    public long FileSize { get; set; }
    public DateTime? LastModified { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/restores/start</c>.
/// </summary>
public class StartRestoreRequest
{
    public string SiteUrl { get; set; } = string.Empty;
    public string WebUrl { get; set; } = string.Empty;
    public string PlaceholderServerRelativeUrl { get; set; } = string.Empty;
    public string? OriginalServerRelativeUrl { get; set; }
    public ConflictBehavior ConflictBehavior { get; set; } = ConflictBehavior.Fail;
}

/// <summary>
/// Request body for <c>POST /api/restores/force</c> (admin break-glass, issue #6).
/// Either supply <see cref="ItemId"/> to resolve the blob/target from an existing
/// migration record, or supply the explicit blob + target coordinates. Runs
/// synchronously and bypasses the queue + placeholder.
/// </summary>
public class ForceRestoreRequest
{
    public Guid? ItemId { get; set; }
    public string? SiteUrl { get; set; }

    /// <summary>Azure blob container name holding the archived copy.</summary>
    public string? BlobContainerName { get; set; }
    public string? BlobPath { get; set; }

    /// <summary>Server-relative URL to restore the file to. Defaults to the item's original location.</summary>
    public string? TargetServerRelativeUrl { get; set; }

    /// <summary>Break-glass defaults to overwrite so a VIP recovery isn't blocked by an existing file.</summary>
    public ConflictBehavior ConflictBehavior { get; set; } = ConflictBehavior.Overwrite;
}

/// <summary>
/// Common accepted-job response. Mirrors the contract documented in
/// requirements.md so the SPFx component can show a deterministic message.
/// </summary>
public class AcceptedJobResponse
{
    public Guid JobId { get; set; }
    public MigrationLifecycleStatus Status { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public class JobStatusResponse
{
    public Guid JobId { get; set; }
    public MigrationOperationKind Operation { get; set; }
    public MigrationLifecycleStatus Status { get; set; }
    public string? Summary { get; set; }
    public string SiteUrl { get; set; } = string.Empty;
    public string RequestedByUpn { get; set; } = string.Empty;
    public string? ContainerName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<JobItemStatusResponse> Items { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

public class JobItemStatusResponse
{
    public Guid ItemId { get; set; }
    public string SpServerRelativeUrl { get; set; } = string.Empty;
    public string? PlaceholderServerRelativeUrl { get; set; }
    public ColdStorageItemKind ItemKind { get; set; }
    public MigrationLifecycleStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public string? LastErrorDetail { get; set; }
    public DateTime? ValidatedAt { get; set; }
    public DateTime? CopiedAt { get; set; }
    public DateTime? SourceDeletedAt { get; set; }
    public DateTime? PlaceholderCreatedAt { get; set; }
    public DateTime? RestoredAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class JobLogEntryResponse
{
    public Guid? ItemId { get; set; }
    public DateTime Timestamp { get; set; }
    public MigrationLifecycleStatus Status { get; set; }
    public int Level { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Exception { get; set; }
}

public class ContainerResponse
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool CanBrowse { get; set; }
    public bool CanMigrate { get; set; }
    public bool CanRestore { get; set; }
    public bool IsDefault { get; set; }
}

public class PlaceholderMetadataResponse
{
    public bool IsResolved { get; set; }
    public string? OriginalSiteUrl { get; set; }
    public string? OriginalWebUrl { get; set; }
    public string? OriginalServerRelativeUrl { get; set; }
    public string? OriginalFileName { get; set; }
    public long OriginalFileSize { get; set; }
    public DateTime OriginalLastModified { get; set; }
    public string? OriginalCreatedBy { get; set; }
    public string? OriginalModifiedBy { get; set; }
    public DateTime? OriginalCreated { get; set; }
    public string? ContainerName { get; set; }
    public string? BlobPath { get; set; }
    public string? BlobUrl { get; set; }
    public DateTime MigratedAt { get; set; }
    public Guid? JobId { get; set; }
    public MigrationLifecycleStatus? CurrentStatus { get; set; }
    public bool IsEligibleForRestore { get; set; }
    public string? UnavailableReason { get; set; }
}

/// <summary>
/// Short-lived signed URL the SPA uses to bounce the user's browser straight
/// to the blob, returned by <c>GET /api/placeholders/download/{itemId}</c>.
/// </summary>
public class DownloadUrlResponse
{
    public string Url { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string? FileName { get; set; }
    public long ContentLength { get; set; }
}

/// <summary>
/// An archiving exclusion scope (issue #7) as returned by the admin API.
/// </summary>
public class ExclusionResponse
{
    public int Id { get; set; }
    public string? SiteUrl { get; set; }
    public string? ServerRelativePrefix { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Request body for <c>POST /api/exclusions</c>. At least one of
/// <see cref="SiteUrl"/> or <see cref="ServerRelativePrefix"/> must be set.
/// </summary>
public class CreateExclusionRequest
{
    public string? SiteUrl { get; set; }
    public string? ServerRelativePrefix { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// Result of an orphan-reconciliation run (issue #3).
/// </summary>
public class ReconcileSummaryResponse
{
    public int Checked { get; set; }
    public int Orphans { get; set; }
    public int BlobsDeleted { get; set; }
    public int Quarantined { get; set; }
    public int Errors { get; set; }
    public string Policy { get; set; } = string.Empty;
}

/// <summary>
/// Cost &amp; savings KPIs for the cold-storage dashboard (issue #8).
/// </summary>
public class SavingsReportResponse
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public long ArchivedItemCount { get; set; }
    public long ReclaimedBytes { get; set; }
    public double ReclaimedGb { get; set; }
    public decimal AzurePricePerGbMonth { get; set; }
    public decimal SpoPricePerGbMonth { get; set; }
    public decimal EstimatedAzureCostPerMonth { get; set; }
    public decimal EstimatedSpoValuePerMonth { get; set; }
    public decimal EstimatedNetSavingsPerMonth { get; set; }
    public string Currency { get; set; } = "USD";
}

/// <summary>
/// One row of the cold-storage audit view (issue #13): who did what to which
/// item, and when.
/// </summary>
public class AuditEntryResponse
{
    public DateTime Timestamp { get; set; }
    public string? ActorUpn { get; set; }
    public string? Action { get; set; }
    public Guid JobId { get; set; }
    public Guid? ItemId { get; set; }
    public string? ItemUrl { get; set; }
    public string Message { get; set; } = string.Empty;
    public MigrationLifecycleStatus Status { get; set; }
}

