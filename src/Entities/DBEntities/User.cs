using Entities.Abstract;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;

namespace Entities.DBEntities;

[Table("users")]
public class User : BaseDBObject
{
    [Column("email")]
    public string Email { get; set; } = string.Empty;
}
