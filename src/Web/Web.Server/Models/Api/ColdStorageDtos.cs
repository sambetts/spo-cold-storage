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
    public string? ContainerName { get; set; }
    public string? BlobPath { get; set; }
    public string? BlobUrl { get; set; }
    public DateTime MigratedAt { get; set; }
    public Guid? JobId { get; set; }
    public MigrationLifecycleStatus? CurrentStatus { get; set; }
    public bool IsEligibleForRestore { get; set; }
    public string? UnavailableReason { get; set; }
}
