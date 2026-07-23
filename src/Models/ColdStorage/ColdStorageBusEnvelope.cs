using System.Text.Json.Serialization;

namespace Models.ColdStorage;

/// <summary>
/// Discriminated envelope for messages on the file-discovery / cold-storage
/// service-bus queue.
/// Keeps the queue single-purpose while allowing the migrator listener to
/// dispatch between migrate and restore operations.
/// </summary>
public class ColdStorageBusEnvelope
{
    /// <summary>
    /// Job correlation id. Each request created by the web API gets a unique id
    /// so the lifecycle table can be updated as the item moves through the pipeline.
    /// </summary>
    public Guid JobId { get; set; } = Guid.Empty;

    /// <summary>
    /// Item correlation id - corresponds to a row in migration_job_items.
    /// </summary>
    public Guid ItemId { get; set; } = Guid.Empty;

    public MigrationOperationKind Operation { get; set; }

    /// <summary>
    /// Cold storage container name to read/write from. Required for both
    /// operations - migration writes into it, restore reads from it.
    /// </summary>
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>
    /// Caller UPN as established from the validated Entra access token at
    /// queue time. The backend uses this when logging so the audit trail can
    /// be filtered per user.
    /// </summary>
    public string RequestedByUpn { get; set; } = string.Empty;

    /// <summary>
    /// Conflict behavior when restoring. Ignored for migrate operations.
    /// </summary>
    public ConflictBehavior ConflictBehavior { get; set; } = ConflictBehavior.Fail;

    /// <summary>
    /// Whether folder enumeration is required. Ignored for restore operations
    /// (a placeholder always represents a single file).
    /// </summary>
    public bool Recursive { get; set; }

    /// <summary>
    /// Migrate-only: when true, the migrator also copies the captured original
    /// authorship (Created By / Modified By / Created / Modified) onto visible
    /// "Original *" columns on the placeholder's library. When false (default),
    /// the placeholder is left as just the <c>.url</c> file. Either way the
    /// original metadata is preserved in cold storage (blob metadata + the
    /// placeholder's <c>[ColdStorage]</c> section), so restore is unaffected.
    /// </summary>
    public bool CopyMetadataColumns { get; set; }

    /// <summary>
    /// File information for migrate operations. Null for restore operations.
    /// </summary>
    public BaseSharePointFileInfo? File { get; set; }

    /// <summary>
    /// Information needed to locate the placeholder to restore. Null for
    /// migrate operations.
    /// </summary>
    public PlaceholderRestoreTarget? RestoreTarget { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        JobId != Guid.Empty &&
        ItemId != Guid.Empty &&
        !string.IsNullOrEmpty(ContainerName) &&
        (Operation == MigrationOperationKind.Migrate
            ? File is not null && File.IsValidInfo
            : RestoreTarget is not null && RestoreTarget.IsValid);
}

/// <summary>
/// Identifies a placeholder file in SharePoint that should be restored.
/// </summary>
public class PlaceholderRestoreTarget
{
    public string SiteUrl { get; set; } = string.Empty;
    public string WebUrl { get; set; } = string.Empty;

    /// <summary>
    /// Server-relative URL of the <c>.url</c> placeholder. For a blob-driven restore this is the
    /// placeholder to remove AFTER a verified restore, and may be empty when the archive has no
    /// surviving placeholder — an orphaned archive is still restorable directly from its blob.
    /// </summary>
    public string PlaceholderServerRelativeUrl { get; set; } = string.Empty;

    /// <summary>
    /// Original (pre-migration) location to restore the file to. Allows the
    /// restore worker to recreate the file at the correct path even if the
    /// placeholder has been moved.
    /// </summary>
    public string? OriginalServerRelativeUrl { get; set; }

    /// <summary>
    /// Cold-storage blob key for the archived file. When set, the restore is BLOB-DRIVEN: the
    /// blob is the source of truth, so the worker restores straight from it (its metadata carries
    /// the authoritative original location) and only uses the placeholder — if one still exists —
    /// as post-restore cleanup. This is what lets an archive be restored even when its SharePoint
    /// placeholder and/or database record are missing. Empty for legacy placeholder-driven messages.
    /// </summary>
    public string? BlobPath { get; set; }

    /// <summary>True when this target carries a blob key + destination, so it can be restored
    /// straight from cold storage without reading a placeholder.</summary>
    [JsonIgnore]
    public bool IsBlobDriven =>
        !string.IsNullOrEmpty(BlobPath) && !string.IsNullOrEmpty(OriginalServerRelativeUrl);

    [JsonIgnore]
    public bool IsValid =>
        !string.IsNullOrEmpty(SiteUrl) &&
        (IsBlobDriven || !string.IsNullOrEmpty(PlaceholderServerRelativeUrl));
}
