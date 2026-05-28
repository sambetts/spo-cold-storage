using Entities.Abstract;
using Models.ColdStorage;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.DBEntities.ColdStorage;

/// <summary>
/// Step-by-step audit trail for a migration or restore job. Backs the
/// <c>GET /api/jobs/{jobId}/logs</c> endpoint described in requirements.md.
/// </summary>
[Table("migration_job_logs")]
public class MigrationJobLog : BaseDBObject
{
    [ForeignKey(nameof(Job))]
    [Column("job_id")]
    public Guid JobId { get; set; }

    public MigrationJob Job { get; set; } = null!;

    [ForeignKey(nameof(Item))]
    [Column("item_id")]
    public Guid? ItemId { get; set; }

    public MigrationJobItem? Item { get; set; }

    [Column("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Standard logging level (Information, Warning, Error, Critical).
    /// Stored as the small int representation so it can be filtered without a join.
    /// </summary>
    [Column("level")]
    public int Level { get; set; }

    /// <summary>
    /// Lifecycle status the item/job was in when this entry was written.
    /// Lets the UI render a status timeline without parsing message strings.
    /// </summary>
    [Column("status")]
    public MigrationLifecycleStatus Status { get; set; }

    [Required]
    [MaxLength(4000)]
    [Column("message")]
    public string Message { get; set; } = string.Empty;

    [Column("exception")]
    public string? Exception { get; set; }
}
