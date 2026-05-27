using Entities.Abstract;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.DBEntities;

[Table("file_migrations_completed")]
public class FileMigrationCompletedLog : BaseFileRelatedClass
{
    [Column("migrated")]
    public DateTime Migrated { get; set; } = DateTime.MinValue;

}
