using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.DBEntities.ColdStorage;

/// <summary>
/// Liveness beacon written periodically by the <c>Migration.Migrator</c> worker
/// (one row per worker instance, keyed by machine name). The web API reads the
/// most recent <see cref="LastSeenUtc"/> to tell the SPFx UI whether a background
/// worker is actually online — so a message stuck in the Service Bus queue can be
/// explained ("worker offline") instead of showing a mysterious, endless
/// "Queued". See <c>WorkerHeartbeatService</c> and <c>WorkerHealthController</c>.
/// </summary>
[Table("cold_storage_worker_heartbeat")]
public class ColdStorageWorkerHeartbeat
{
    /// <summary>
    /// Stable identity of the worker instance (machine name). Multiple instances
    /// each keep their own row; "online" is the newest heartbeat across all rows.
    /// </summary>
    [Key]
    [MaxLength(256)]
    [Column("worker_id")]
    public string WorkerId { get; set; } = string.Empty;

    [MaxLength(256)]
    [Column("machine_name")]
    public string? MachineName { get; set; }

    [MaxLength(64)]
    [Column("worker_version")]
    public string? WorkerVersion { get; set; }

    /// <summary>Service Bus namespace the listener is attached to (diagnostics).</summary>
    [MaxLength(512)]
    [Column("service_bus_namespace")]
    public string? ServiceBusNamespace { get; set; }

    /// <summary>True while the Service Bus processor is actively receiving.</summary>
    [Column("listener_connected")]
    public bool ListenerConnected { get; set; }

    [Column("started_at_utc")]
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    [Column("last_seen_utc")]
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}
