using Entities.Abstract;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;

namespace Entities.DBEntities;

[Table("sites")]
public class Site : BaseDBObjectWithUrl
{
}
