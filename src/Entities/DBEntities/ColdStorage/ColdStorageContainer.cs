using Entities.Abstract;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.DBEntities.ColdStorage;

/// <summary>
/// A configured cold-storage destination. Multiple containers exist so each
/// can be given unique access permissions.
/// </summary>
[Table("cold_storage_containers")]
public class ColdStorageContainer : BaseDBObject
{
    /// <summary>
    /// Stable machine name used by API requests and bus messages. Must be
    /// unique within the system.
    /// </summary>
    [Required]
    [MaxLength(128)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Friendly name displayed in the SPFx picker and the web UI.
    /// </summary>
    [Required]
    [MaxLength(256)]
    [Column("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Azure blob container the migrator writes into.
    /// </summary>
    [Required]
    [MaxLength(63)]
    [Column("blob_container_name")]
    public string BlobContainerName { get; set; } = string.Empty;

    /// <summary>
    /// Storage account URI, e.g. <c>https://contoso.blob.core.windows.net</c>.
    /// Empty means use the system default storage account.
    /// </summary>
    [MaxLength(2048)]
    [Column("storage_account_uri")]
    public string StorageAccountUri { get; set; } = string.Empty;

    /// <summary>
    /// True if this is the default container used by legacy indexer-driven
    /// scheduled migrations when no explicit container is specified.
    /// </summary>
    [Column("is_default")]
    public bool IsDefault { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    public ICollection<ColdStorageContainerAcl> Acls { get; set; } = [];
}
