using Entities.Abstract;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.DBEntities.ColdStorage;

/// <summary>
/// An archiving exclusion rule (issue #7). Content under an excluded scope is
/// never queued for cold storage — e.g. confidential/critical areas such as
/// Direction or Legal. A rule matches when EITHER the candidate's site URL
/// equals <see cref="SiteUrl"/> (exclude a whole site collection) OR the
/// candidate's server-relative URL is at/under <see cref="ServerRelativePrefix"/>
/// (exclude a library or folder subtree). Admins manage these at runtime via
/// the exclusions API, so no redeploy is needed to add a protected area.
/// </summary>
[Table("cold_storage_exclusions")]
public class ColdStorageExclusion : BaseDBObject
{
    /// <summary>
    /// Exclude an entire site collection by its URL, e.g.
    /// <c>https://contoso.sharepoint.com/sites/Direction</c>. Optional.
    /// </summary>
    [MaxLength(2048)]
    [Column("site_url")]
    public string? SiteUrl { get; set; }

    /// <summary>
    /// Exclude a library/folder subtree by server-relative URL prefix, e.g.
    /// <c>/sites/Contoso/Legal Documents</c>. Matching is segment-aware so it
    /// will not match a sibling like <c>/sites/Contoso/Legal Documents Archive</c>.
    /// Optional.
    /// </summary>
    [MaxLength(2048)]
    [Column("server_relative_prefix")]
    public string? ServerRelativePrefix { get; set; }

    [MaxLength(512)]
    [Column("description")]
    public string? Description { get; set; }

    /// <summary>Whether the rule is active. Lets an admin disable without deleting.</summary>
    [Column("enabled")]
    public bool Enabled { get; set; } = true;

    [MaxLength(256)]
    [Column("created_by")]
    public string? CreatedBy { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
