using Entities.Abstract;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.DBEntities;

[Table("webs")]
public class Web : BaseDBObjectWithUrl
{
    [ForeignKey(nameof(Site))]
    [Column("site_id")]
    public int SiteId { get; set; }

    public Site Site { get; set; } = null!;
}
