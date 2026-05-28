using Entities.Abstract;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.DBEntities.ColdStorage;

/// <summary>
/// Permission rule on a single cold-storage container. Each row grants a
/// specific Entra principal (user or group) one or more of browse/migrate/restore.
/// </summary>
[Table("cold_storage_container_acls")]
public class ColdStorageContainerAcl : BaseDBObject
{
    [ForeignKey(nameof(Container))]
    [Column("container_id")]
    public int ContainerId { get; set; }

    public ColdStorageContainer Container { get; set; } = null!;

    /// <summary>
    /// Entra object id of the user or group.
    /// </summary>
    [Required]
    [MaxLength(64)]
    [Column("principal_id")]
    public string PrincipalId { get; set; } = string.Empty;

    /// <summary>
    /// 0 = user, 1 = group. Stored as int so callers can avoid taking a
    /// dependency on the enum just to read the ACL.
    /// </summary>
    [Column("principal_type")]
    public int PrincipalType { get; set; }

    /// <summary>
    /// Cached display value (UPN for users, display name for groups). For
    /// audit / UI only - not used for authorization.
    /// </summary>
    [MaxLength(256)]
    [Column("principal_display")]
    public string? PrincipalDisplay { get; set; }

    [Column("can_browse")]
    public bool CanBrowse { get; set; }

    [Column("can_migrate")]
    public bool CanMigrate { get; set; }

    [Column("can_restore")]
    public bool CanRestore { get; set; }
}

/// <summary>
/// Stable values for <see cref="ColdStorageContainerAcl.PrincipalType"/>.
/// </summary>
public enum ColdStoragePrincipalType
{
    User = 0,
    Group = 1,
}
