using Entities.Abstract;
using Models.ColdStorage;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.DBEntities.ColdStorage;

/// <summary>
/// Top-level migrate-or-restore request. One job groups one or more
/// <see cref="MigrationJobItem"/> rows so the SPFx component, web UI and
/// background workers can see a consistent rollup of progress.
/// </summary>
[Table("migration_jobs")]
public class MigrationJob
{
    /// <summary>
    /// Stable GUID surfaced via the REST API. Replaces the int primary key
    /// for cross-system references so the migrator can resolve a job from a
    /// bus message without hitting the DB twice.
    /// </summary>
    [Key]
    [Column("job_id")]
    public Guid JobId { get; set; } = Guid.NewGuid();

    [Column("operation")]
    public MigrationOperationKind Operation { get; set; }

    [Required]
    [MaxLength(256)]
    [Column("requested_by_upn")]
    public string RequestedByUpn { get; set; } = string.Empty;

    [Required]
    [MaxLength(2048)]
    [Column("site_url")]
    public string SiteUrl { get; set; } = string.Empty;

    [MaxLength(2048)]
    [Column("web_url")]
    public string WebUrl { get; set; } = string.Empty;

    [ForeignKey(nameof(Container))]
    [Column("container_id")]
    public int? ContainerId { get; set; }

    public ColdStorageContainer? Container { get; set; }

    [Column("conflict_behavior")]
    public ConflictBehavior ConflictBehavior { get; set; } = ConflictBehavior.Fail;

    [Column("status")]
    public MigrationLifecycleStatus Status { get; set; } = MigrationLifecycleStatus.Queued;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Short text summary surfaced to the SPFx status column.
    /// </summary>
    [MaxLength(1024)]
    [Column("summary")]
    public string? Summary { get; set; }

    /// <summary>
    /// Serialized submission (selected items + recursive + priority) for an async
    /// submit. The MigrationExpander background service reads this to expand folders
    /// and create the per-file items off the request thread, so a large-folder submit
    /// returns immediately instead of blocking for minutes. Null once expansion has
    /// run (or for a job that was expanded inline).
    /// </summary>
    [Column("submission_request")]
    public string? SubmissionRequestJson { get; set; }

    /// <summary>
    /// When the async expansion finished creating + enqueuing this job's items. Null
    /// while a submission is still pending expansion (used to re-drive after a restart).
    /// </summary>
    [Column("expansion_completed_at")]
    public DateTime? ExpansionCompletedAt { get; set; }

    public ICollection<MigrationJobItem> Items { get; set; } = [];
}
