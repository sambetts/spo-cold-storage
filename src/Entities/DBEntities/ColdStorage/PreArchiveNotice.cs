using Entities.Abstract;
using Models.ColdStorage;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.DBEntities.ColdStorage;

/// <summary>
/// A pre-archive notice (issue #17): a warning recorded for a file before an
/// auto-archive moves it to cold storage, with a grace window during which the
/// owner can act. The row itself is the in-product notification surface; an
/// email/Teams channel can additionally be wired via IPreArchiveNotifier.
/// </summary>
[Table("pre_archive_notices")]
public class PreArchiveNotice : BaseDBObject
{
    [Required]
    [MaxLength(2048)]
    [Column("site_url")]
    public string SiteUrl { get; set; } = string.Empty;

    [Required]
    [MaxLength(2048)]
    [Column("server_relative_url")]
    public string ServerRelativeUrl { get; set; } = string.Empty;

    /// <summary>UPN the notice was addressed to (file owner/editor), if known.</summary>
    [MaxLength(256)]
    [Column("notified_upn")]
    public string? NotifiedUpn { get; set; }

    [Column("notified_at")]
    public DateTime NotifiedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Auto-archive may proceed once now &gt;= this time.</summary>
    [Column("grace_until")]
    public DateTime GraceUntil { get; set; }

    [Column("status")]
    public PreArchiveNoticeStatus Status { get; set; } = PreArchiveNoticeStatus.Pending;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
