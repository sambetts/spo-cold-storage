using Entities.Abstract;
using Models;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.DBEntities;

[Table("files")]
public class SPFile : BaseDBObjectWithUrl
{
    public SPFile() { }
    public SPFile(BaseSharePointFileInfo fileDiscovered, Web parentWeb) : this()
    {
        this.Url = fileDiscovered.FullSharePointUrl;
        this.Web = parentWeb;
    }

    [ForeignKey(nameof(Web))]
    [Column("web_id")]
    public int WebId { get; set; }

    public Web Web { get; set; } = null!;

    [ForeignKey(nameof(Directory))]
    [Column("directory_id")]
    public int? DirectoryId { get; set; }

    public FileDirectory? Directory { get; set; }

    [Column("access_count")]
    public int? AccessCount { get; set; } = null;

    [Column("analysis_completed")]
    public DateTime? AnalysisCompleted { get; set; }

    [Column("last_modified")]
    public DateTime LastModified { get; set; } = DateTime.MinValue;

    [Column("created_date")]
    public DateTime? CreatedDate { get; set; }

    public User LastModifiedBy { get; set; } = new User();

    [ForeignKey(nameof(LastModifiedBy))]
    [Column("last_modified_by_user_id")]
    public int LastModifiedByUserId { get; set; }

    [Column("version_count")]
    public int VersionCount { get; set; } = 0;

    [Column("versions_total_size")]
    public long VersionHistorySize { get; set; } = 0;

    [Column("file_size")]
    public long FileSize { get; set; } = 0;

    /// <summary>
    /// Graph drive id (Drive resource ID). Persisted so we can call
    /// /drives/{id}/items/{itemId}/analytics on later runs without re-crawling.
    /// Nullable to allow legacy rows that pre-date this column.
    /// </summary>
    // MaxLength 450 keeps the column as NVARCHAR(450) so it can participate
    // in IX_files_analysis_completed_null. Without this, EF maps string? to
    // NVARCHAR(MAX), which SQL Server rejects as an index key column.
    [Column("drive_id")]
    [MaxLength(450)]
    public string? DriveId { get; set; }

    /// <summary>
    /// Graph drive-item id. Pairs with <see cref="DriveId"/>.
    /// </summary>
    // See DriveId comment: must stay NVARCHAR(450) for the filtered index.
    [Column("graph_item_id")]
    [MaxLength(450)]
    public string? GraphItemId { get; set; }
}
