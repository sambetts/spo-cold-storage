using Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Migration.Engine.Lifecycle;
using Web.Models.Api;

namespace Web.Controllers;

/// <summary>
/// <c>GET /api/worker/health</c> – liveness of the background <c>Migration.Migrator</c>
/// worker, derived from the heartbeat beacon it writes every ~30s. The SPFx
/// progress dialog polls this so a message stuck in the Service Bus queue is
/// explained ("worker offline") rather than showing an endless "Queued".
/// </summary>
[Authorize]
[ApiController]
[Route("api/worker")]
public class WorkerHealthController(SPOColdStorageDbContext db) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    [HttpGet("health")]
    public async Task<ActionResult<WorkerHealthResponse>> GetHealthAsync(CancellationToken cancellationToken)
    {
        var beats = await _db.ColdStorageWorkerHeartbeats
            .AsNoTracking()
            .OrderByDescending(h => h.LastSeenUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var windowSeconds = (int)WorkerHeartbeatService.OnlineWindow.TotalSeconds;

        if (beats.Count == 0)
        {
            // No worker has ever reported. Treat as offline so the UI can warn.
            return new WorkerHealthResponse
            {
                IsOnline = false,
                OnlineWindowSeconds = windowSeconds,
                WorkerCount = 0,
            };
        }

        var newest = beats[0];
        var secondsSince = (DateTime.UtcNow - newest.LastSeenUtc).TotalSeconds;

        // "Active" = instances whose heartbeat is within the online window. On Flex
        // Consumption the worker scales out to many short-lived instances (each with a
        // unique machine name / row), so counting every row that ever beat would report
        // hundreds. Count only the currently-live ones so the number means "how many
        // worker instances are processing right now".
        var activeCutoff = DateTime.UtcNow - WorkerHeartbeatService.OnlineWindow;
        var activeCount = beats.Count(b => b.LastSeenUtc >= activeCutoff);

        return new WorkerHealthResponse
        {
            IsOnline = secondsSince <= WorkerHeartbeatService.OnlineWindow.TotalSeconds,
            LastSeenUtc = newest.LastSeenUtc,
            SecondsSinceLastSeen = Math.Max(0, secondsSince),
            OnlineWindowSeconds = windowSeconds,
            WorkerId = newest.WorkerId,
            MachineName = newest.MachineName,
            WorkerVersion = newest.WorkerVersion,
            StartedAtUtc = newest.StartedAtUtc,
            WorkerCount = activeCount,
        };
    }
}
