using Entities.Abstract;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.DBEntities;
/// <summary>
/// Lookup table for file directories
/// </summary>
[Table("file_directories")]
public class FileDirectory : BaseDBObject
{
    public FileDirectory() { }

    /// <summary>
    /// The directory path where the file was found
    /// </summary>
    [Column("directory_path")]
    [Required]
    [MaxLength(500)]
    public string DirectoryPath { get; set; } = string.Empty;

    /// <summary>
    /// Files in this directory
    /// </summary>
    public ICollection<SPFile> Files { get; set; } = [];
}
