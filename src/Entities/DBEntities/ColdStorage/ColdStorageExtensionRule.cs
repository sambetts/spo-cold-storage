using Entities.Abstract;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.DBEntities.ColdStorage;

/// <summary>
/// How a file-extension rule affects archive eligibility.
/// </summary>
public enum ArchiveExtensionRuleMode
{
    /// <summary>Files with this extension are never archived (denylist).</summary>
    Exclude = 0,

    /// <summary>
    /// When at least one Include rule exists, ONLY listed extensions are eligible
    /// for archiving (allowlist); everything else is skipped.
    /// </summary>
    Include = 1,
}

/// <summary>
/// A runtime-editable file-extension archiving rule. Admins manage these via the
/// exclusions API/UI so which file types get archived can change without a
/// redeploy — the counterpart to <see cref="ColdStorageExclusion"/> (which scopes
/// by site/folder).
///
/// NOTE: <c>.url</c> (cold-storage placeholders) is ALWAYS excluded in code
/// (<c>ArchiveEligibilityEvaluator</c>) and is deliberately NOT represented as a
/// row here, so it can never be turned back on for archiving from the UI.
/// </summary>
[Table("cold_storage_extension_rules")]
public class ColdStorageExtensionRule : BaseDBObject
{
    /// <summary>
    /// The file extension, stored normalised with a single leading dot and
    /// lower-cased, e.g. <c>.tmp</c>.
    /// </summary>
    [MaxLength(64)]
    [Column("extension")]
    public string Extension { get; set; } = string.Empty;

    /// <summary>Exclude (denylist) or Include (allowlist).</summary>
    [Column("mode")]
    public ArchiveExtensionRuleMode Mode { get; set; } = ArchiveExtensionRuleMode.Exclude;

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
