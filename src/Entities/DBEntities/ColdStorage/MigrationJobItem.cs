using Models.ColdStorage;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.DBEntities.ColdStorage;

/// <summary>
/// One file or folder within a <see cref="MigrationJob"/>. Carries the
/// per-item lifecycle status plus the placeholder/blob coordinates so a
/// restore can be triggered later without re-crawling SharePoint.
/// </summary>
[Table("migration_job_items")]
public class MigrationJobItem
{
    [Key]
    [Column("item_id")]
    public Guid ItemId { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(Job))]
    [Column("job_id")]
    public Guid JobId { get; set; }

    public MigrationJob Job { get; set; } = null!;

    [Column("item_kind")]
    public ColdStorageItemKind ItemKind { get; set; } = ColdStorageItemKind.File;

    [Column("recursive")]
    public bool Recursive { get; set; }

    [Required]
    [MaxLength(2048)]
    [Column("sp_site_url")]
    public string SpSiteUrl { get; set; } = string.Empty;

    [Required]
    [MaxLength(2048)]
    [Column("sp_web_url")]
    public string SpWebUrl { get; set; } = string.Empty;

    [Required]
    [MaxLength(2048)]
    [Column("sp_server_relative_url")]
    public string SpServerRelativeUrl { get; set; } = string.Empty;

    /// <summary>
    /// For folder items, optional sub-path inside the folder. Empty = the
    /// folder root.
    /// </summary>
    [MaxLength(2048)]
    [Column("sp_subpath")]
    public string SpSubPath { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes captured when the item was validated. 0 for folders.
    /// </summary>
    [Column("file_size")]
    public long FileSize { get; set; }

    /// <summary>
    /// Source file last-modified timestamp. Used for idempotency: a re-run
    /// against the same item with an unchanged LastModified is short-circuited.
    /// </summary>
    [Column("source_last_modified")]
    public DateTime? SourceLastModified { get; set; }

    /// <summary>
    /// Display name of the source file's original author (SharePoint "Created By"),
    /// captured at migration time so it survives the source delete and is visible
    /// on the placeholder and after a restore.
    /// </summary>
    [MaxLength(256)]
    [Column("original_created_by")]
    public string? OriginalCreatedBy { get; set; }

    /// <summary>
    /// Display name of the last person to edit the source file (SharePoint
    /// "Modified By"), captured at migration time.
    /// </summary>
    [MaxLength(256)]
    [Column("original_modified_by")]
    public string? OriginalModifiedBy { get; set; }

    /// <summary>
    /// Original created timestamp of the source file, captured at migration time.
    /// </summary>
    [Column("original_created")]
    public DateTime? OriginalCreated { get; set; }

    [ForeignKey(nameof(Container))]
    [Column("container_id")]
    public int? ContainerId { get; set; }

    public ColdStorageContainer? Container { get; set; }

    [MaxLength(63)]
    [Column("blob_container_name")]
    public string? BlobContainerName { get; set; }

    [MaxLength(1024)]
    [Column("blob_path")]
    public string? BlobPath { get; set; }

    [MaxLength(2048)]
    [Column("blob_url")]
    public string? BlobUrl { get; set; }

    [MaxLength(2048)]
    [Column("placeholder_server_relative_url")]
    public string? PlaceholderServerRelativeUrl { get; set; }

    [MaxLength(64)]
    [Column("content_md5_base64")]
    public string? ContentMd5Base64 { get; set; }

    /// <summary>
    /// Snapshot of the role assignments that were on the source item before
    /// it was deleted, serialised as JSON. Used to restore permissions onto
    /// the .url placeholder and onto the restored file later.
    /// </summary>
    [Column("permissions_json")]
    public string? PermissionsJson { get; set; }

    [Column("status")]
    public MigrationLifecycleStatus Status { get; set; } = MigrationLifecycleStatus.Queued;

    [MaxLength(2048)]
    [Column("last_error")]
    public string? LastError { get; set; }

    /// <summary>
    /// Raw technical detail (exception text / stack) behind <see cref="LastError"/>,
    /// kept for support staff while the column shows a friendly summary (issue #5).
    /// </summary>
    [Column("last_error_detail")]
    public string? LastErrorDetail { get; set; }

    [Column("attempts")]
    public int Attempts { get; set; }

    /// <summary>
    /// Admin-set processing priority (issue #16): higher is more urgent.
    /// Surfaced in the admin queue view and used to order app-side processing /
    /// display. (The Service Bus queue itself is FIFO; see QueueController.)
    /// </summary>
    [Column("priority")]
    public int Priority { get; set; }

    [Column("validated_at")]
    public DateTime? ValidatedAt { get; set; }

    [Column("copied_at")]
    public DateTime? CopiedAt { get; set; }

    [Column("source_deleted_at")]
    public DateTime? SourceDeletedAt { get; set; }

    [Column("placeholder_created_at")]
    public DateTime? PlaceholderCreatedAt { get; set; }

    [Column("restored_at")]
    public DateTime? RestoredAt { get; set; }

    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// When orphan reconciliation last checked this item (issue #3). Drives
    /// round-robin coverage so each run checks the least-recently-checked items.
    /// </summary>
    [Column("last_reconciled_at")]
    public DateTime? LastReconciledAt { get; set; }

    /// <summary>
    /// Set when reconciliation found this item's placeholder/site gone, so the
    /// orphan isn't reported repeatedly (issue #3).
    /// </summary>
    [Column("orphan_detected_at")]
    public DateTime? OrphanDetectedAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
