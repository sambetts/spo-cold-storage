using Entities;
using Entities.DBEntities.ColdStorage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Models.ColdStorage;
using Web.Authorization;
using Web.Models.Api;
using Web.Services;

namespace Web.Controllers;

/// <summary>
/// Admin queue control (issue #16).
/// <c>GET  /api/admin/queue</c> – live view of in-flight items + status counts.
/// <c>POST /api/admin/queue/{itemId}/priority</c> – set an item's priority.
/// <c>POST /api/admin/queue/{itemId}/cancel</c> – cancel a not-yet-finished item;
/// the worker honours this before doing any work.
///
/// Prioritisation note: the backing Service Bus queue is FIFO, so a stored
/// priority can't physically re-order messages already enqueued. It is surfaced
/// here and used to order the view + any app-side processing; true re-ordering
/// would need a dedicated high-priority queue or a pull-based dispatcher (a
/// deliberate follow-up). The immediately effective lever today is cancel, which
/// frees worker capacity for the urgent items.
/// </summary>
[Authorize]
[ApiController]
[Route("api/admin/queue")]
public class QueueController(
    SPOColdStorageDbContext db,
    IColdStorageAdminAuthorizationService admin,
    ILogger<QueueController> logger) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IColdStorageAdminAuthorizationService _admin = admin ?? throw new ArgumentNullException(nameof(admin));
    private readonly ILogger<QueueController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    // Non-terminal statuses = "in flight".
    private static readonly MigrationLifecycleStatus[] ActiveStatuses =
        Enum.GetValues<MigrationLifecycleStatus>().Where(s => !s.IsTerminal()).ToArray();

    [HttpGet]
    public async Task<ActionResult<QueueViewResponse>> GetAsync([FromQuery] int? take, CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }
        var capped = take is null ? 200 : Math.Clamp(take.Value, 1, 2000);

        var active = await _db.MigrationJobItems
            .AsNoTracking()
            .Where(i => ActiveStatuses.Contains(i.Status))
            .OrderByDescending(i => i.Priority)
            .ThenBy(i => i.CreatedAt)
            .Take(capped)
            .Select(i => new
            {
                i.ItemId,
                i.JobId,
                i.Job.Operation,
                i.SpServerRelativeUrl,
                i.Status,
                i.Priority,
                i.Attempts,
                i.Job.RequestedByUpn,
                i.CreatedAt,
                i.UpdatedAt,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var counts = await _db.MigrationJobItems
            .AsNoTracking()
            .Where(i => ActiveStatuses.Contains(i.Status))
            .GroupBy(i => i.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new QueueViewResponse
        {
            TotalInFlight = counts.Sum(c => c.Count),
            CountsByStatus = counts.ToDictionary(c => c.Key.ToString(), c => c.Count),
            Items = active.Select(i => new QueueItemResponse
            {
                ItemId = i.ItemId,
                JobId = i.JobId,
                Operation = i.Operation,
                SpServerRelativeUrl = i.SpServerRelativeUrl,
                Status = i.Status,
                Priority = i.Priority,
                Attempts = i.Attempts,
                RequestedByUpn = i.RequestedByUpn,
                CreatedAt = i.CreatedAt,
                UpdatedAt = i.UpdatedAt,
            }).ToList(),
        };
    }

    [HttpPost("{itemId:guid}/priority")]
    public async Task<IActionResult> SetPriorityAsync(Guid itemId, [FromBody] SetPriorityRequest request, CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }
        var item = await _db.MigrationJobItems.FirstOrDefaultAsync(i => i.ItemId == itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return NotFound();
        }
        item.Priority = request?.Priority ?? 0;
        item.UpdatedAt = DateTime.UtcNow;
        _db.MigrationJobLogs.Add(new MigrationJobLog
        {
            JobId = item.JobId,
            ItemId = item.ItemId,
            Status = item.Status,
            Level = (int)LogLevel.Information,
            Message = $"Priority set to {item.Priority} by {User.GetUpn()}.",
            ActorUpn = User.GetUpn(),
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("{itemId:guid}/cancel")]
    public async Task<IActionResult> CancelAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }
        var item = await _db.MigrationJobItems.FirstOrDefaultAsync(i => i.ItemId == itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return NotFound();
        }
        if (item.Status.IsTerminal())
        {
            return Conflict(new { error = $"Item is already terminal ({item.Status}); nothing to cancel." });
        }
        item.Status = MigrationLifecycleStatus.Cancelled;
        item.LastError = "Cancelled by admin from the queue.";
        item.UpdatedAt = DateTime.UtcNow;
        item.CompletedAt = DateTime.UtcNow;
        _db.MigrationJobLogs.Add(new MigrationJobLog
        {
            JobId = item.JobId,
            ItemId = item.ItemId,
            Status = MigrationLifecycleStatus.Cancelled,
            Level = (int)LogLevel.Warning,
            Message = $"Cancelled by {User.GetUpn()} from the admin queue.",
            ActorUpn = User.GetUpn(),
            Action = "Cancel",
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Item {ItemId} cancelled from admin queue by {Upn}.", itemId, User.GetUpn());
        return NoContent();
    }
}
