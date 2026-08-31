using Entities.Abstract;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.DBEntities.ColdStorage;

/// <summary>
/// A runtime-editable scalar product setting, managed by an admin from the portal
/// (issue #66) rather than by redeploying app settings.
///
/// <para>
/// Precedence is: DB row (this table) → the host's app setting on <see cref="Configuration.Config"/>
/// → the code default. That keeps the deploy-time parameter meaningful as the initial
/// value while letting an operator change behaviour live, and it applies to BOTH hosts
/// (the API and the queue worker) because both read the same table.
/// </para>
///
/// <para>
/// Only keys the server explicitly allow-lists are readable/writable through the API —
/// this is not a general-purpose config escape hatch.
/// </para>
/// </summary>
[Table("cold_storage_settings")]
public class ColdStorageSetting : BaseDBObject
{
    /// <summary>Stable machine key, e.g. <c>CaptureVersionHistory</c>.</summary>
    [MaxLength(128)]
    [Column("setting_key")]
    public string SettingKey { get; set; } = string.Empty;

    /// <summary>Raw value as text; parsed by the consumer (ints are used as 0/1 flags).</summary>
    [MaxLength(1024)]
    [Column("setting_value")]
    public string? SettingValue { get; set; }

    [MaxLength(256)]
    [Column("updated_by")]
    public string? UpdatedBy { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
