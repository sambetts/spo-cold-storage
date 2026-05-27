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
}

public class StagingTempFile : BaseSharePointFileInfo
{
    public StagingTempFile() { }
    public StagingTempFile(BaseSharePointFileInfo driveArg, Guid blockGuid, DateTime inserted) : base(driveArg)
    {
        this.ImportBlockId = blockGuid;
        this.Inserted = inserted;
    }

    [Key]
    [Column("id")]
    public int ID { get; set; }

    public Guid ImportBlockId { get; set; } = Guid.Empty;
    public DateTime Inserted { get; set; } = DateTime.Now;
}
