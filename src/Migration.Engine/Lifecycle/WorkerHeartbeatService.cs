using Entities;
using Entities.DBEntities.ColdStorage;
using Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Migration.Engine.Lifecycle;

/// <summary>
/// Periodically writes a liveness beacon (one row per worker instance) so the
/// web API / SPFx UI can tell whether a background worker is actually online.
/// This turns a message stuck in the Service Bus queue into an explainable
/// "worker offline" state instead of an endless, mysterious "Queued".
///
/// Best-effort by design: a heartbeat write must never take the worker down, so
/// every DB error is swallowed and retried on the next tick (the table may not
/// exist yet on a brand-new deployment until the web app has run DbInitializer).
/// </summary>
public sealed class WorkerHeartbeatService
{
    /// <summary>How often the beacon is refreshed.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A worker is considered "online" if its most recent heartbeat is within
    /// this window (three missed beats). Shared with the health API so the
    /// staleness rule lives in one place.
    /// </summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(100);

    private readonly Config _config;
    private readonly ILogger _logger;
    private readonly string _workerId;
    private readonly string? _serviceBusNamespace;
    private readonly string? _workerVersion;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;

    public WorkerHeartbeatService(Config config, ILogger logger, string? serviceBusNamespace = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceBusNamespace = serviceBusNamespace;
        _workerId = Environment.MachineName;
        _workerVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
    }

    /// <summary>
    /// Runs the heartbeat loop until cancelled. Writes an initial beat
    /// immediately so the worker shows online as soon as it starts.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Worker heartbeat started for '{WorkerId}' (every {Seconds}s).", _workerId, HeartbeatInterval.TotalSeconds);
        while (!cancellationToken.IsCancellationRequested)
        {
            await WriteHeartbeatAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task WriteHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var db = new SPOColdStorageDbContext(_config);
            var existing = await db.ColdStorageWorkerHeartbeats
                .FirstOrDefaultAsync(h => h.WorkerId == _workerId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                await db.ColdStorageWorkerHeartbeats.AddAsync(new ColdStorageWorkerHeartbeat
                {
                    WorkerId = _workerId,
                    MachineName = _workerId,
                    WorkerVersion = _workerVersion,
                    ServiceBusNamespace = _serviceBusNamespace,
                    ListenerConnected = true,
                    StartedAtUtc = _startedAtUtc,
                    LastSeenUtc = DateTime.UtcNow,
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                existing.MachineName = _workerId;
                existing.WorkerVersion = _workerVersion;
                existing.ServiceBusNamespace = _serviceBusNamespace;
                existing.ListenerConnected = true;
                existing.StartedAtUtc = _startedAtUtc;
                existing.LastSeenUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never let a heartbeat failure disturb message processing. The table
            // may not exist yet on a fresh deploy; it will next tick.
            _logger.LogWarning(ex, "Worker heartbeat write failed (will retry next tick).");
        }
    }
}
