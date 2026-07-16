using Entities;
using Entities.DBEntities.ColdStorage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Migration.Engine.Migration;
using Models;
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
/// <c>POST /api/admin/queue/requeue</c> – re-enqueue failed transfers for recovery.
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
    IColdStorageBusPublisher publisher,
    ILogger<QueueController> logger) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IColdStorageAdminAuthorizationService _admin = admin ?? throw new ArgumentNullException(nameof(admin));
    private readonly IColdStorageBusPublisher _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
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

    /// <summary>
    /// <c>POST /api/admin/queue/requeue</c> — re-enqueue failed transfers so they
    /// can be recovered (e.g. after a transient error was fixed). Only items in a
    /// *Failed lifecycle state are ever touched; in-flight, completed, cancelled
    /// and skipped items are left alone.
    ///
    /// <c>Status = "StaleQueued"</c> instead recovers orphaned items whose row is
    /// still <c>Queued</c> but whose bus message was lost (age-gated by
    /// <c>OlderThanMinutes</c>, default 15, so freshly-enqueued items are never
    /// re-published). Duplicate messages, if any, are coalesced by the pipeline's
    /// in-flight + DB status guards.
    ///
    /// SAFETY: requeuing is safe against the never-delete-source invariant. The
    /// migrate pipeline re-validates and re-copies from scratch and only deletes
    /// the source after a confirmed copy; an item whose source was already deleted
    /// (PlaceholderFailed) resumes by re-writing the placeholder from the existing
    /// blob rather than re-downloading. Each requeue is logged with an audit action.
    /// </summary>
    [HttpPost("requeue")]
    public async Task<ActionResult<RequeueResultResponse>> RequeueAsync([FromBody] RequeueRequest request, CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }
        var upn = User.GetUpn();

        // "StaleQueued" mode recovers orphaned items (row says Queued but the bus
        // message was lost) rather than failed items. Tracked so the eligibility
        // gate below allows Queued (not just *Failed) in this mode only.
        var staleQueuedMode = false;

        IQueryable<MigrationJobItem> query = _db.MigrationJobItems.Include(i => i.Job);
        if (request?.ItemIds is { Count: > 0 } itemIds)
        {
            var ids = itemIds.ToHashSet();
            query = query.Where(i => ids.Contains(i.ItemId));
        }
        else if (request?.JobId is Guid jobId && jobId != Guid.Empty)
        {
            query = query.Where(i => i.JobId == jobId);
        }
        else if (!string.IsNullOrWhiteSpace(request?.Status))
        {
            if (string.Equals(request.Status, "AllFailed", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(i => FailedStatuses.Contains(i.Status));
            }
            else if (string.Equals(request.Status, "StaleQueued", StringComparison.OrdinalIgnoreCase))
            {
                staleQueuedMode = true;
                var minutes = request.OlderThanMinutes is int mm ? Math.Clamp(mm, 1, 1440) : 15;
                var cutoff = DateTime.UtcNow.AddMinutes(-minutes);
                query = query.Where(i => i.Status == MigrationLifecycleStatus.Queued && i.UpdatedAt < cutoff);
            }
            else if (Enum.TryParse<MigrationLifecycleStatus>(request.Status, true, out var st))
            {
                query = query.Where(i => i.Status == st);
            }
            else
            {
                return BadRequest($"Unknown status '{request.Status}'. Use a lifecycle status name, 'AllFailed', or 'StaleQueued'.");
            }
            if (!string.IsNullOrWhiteSpace(request.SiteUrl))
            {
                var site = request.SiteUrl.TrimEnd('/');
                query = query.Where(i => i.SpSiteUrl == request.SiteUrl || i.SpSiteUrl == site);
            }
        }
        else
        {
            return BadRequest("Provide itemIds, jobId, or a status ('AllFailed' / 'StaleQueued' / a lifecycle status) to requeue.");
        }

        var capped = request?.Max is int m ? Math.Clamp(m, 1, 5000) : 500;
        var candidates = await query
            .OrderBy(i => i.CreatedAt)
            .Take(capped)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = new RequeueResultResponse();
        var toPublish = new List<ColdStorageBusEnvelope>();

        foreach (var item in candidates)
        {
            var eligible = staleQueuedMode ? item.Status == MigrationLifecycleStatus.Queued : IsRequeueable(item.Status);
            if (!eligible)
            {
                result.Skipped++;
                continue;
            }

            var envelope = BuildEnvelope(item, upn);
            if (envelope is null)
            {
                result.Skipped++;
                result.Messages.Add(
                    $"{item.SpServerRelativeUrl}: cannot requeue — missing " +
                    (item.Job.Operation == MigrationOperationKind.Restore ? "placeholder location." : "blob container."));
                continue;
            }

            var previous = item.Status;
            item.Status = MigrationLifecycleStatus.Queued;
            item.LastError = null;
            item.LastErrorDetail = null;
            item.CompletedAt = null;
            item.UpdatedAt = DateTime.UtcNow;
            _db.MigrationJobLogs.Add(new MigrationJobLog
            {
                JobId = item.JobId,
                ItemId = item.ItemId,
                Status = MigrationLifecycleStatus.Queued,
                Level = (int)LogLevel.Information,
                Message = staleQueuedMode
                    ? $"Re-published orphaned Queued item by {upn}."
                    : $"Requeued by {upn} (was {previous}).",
                ActorUpn = upn,
                Action = "Requeue",
            });
            toPublish.Add(envelope);
            result.Requeued++;
        }

        // Commit the DB reset to Queued BEFORE publishing so the worker never sees
        // a message for an item whose row still reads as failed/terminal.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var publishFailures = new List<(Guid ItemId, string Error)>();
        foreach (var envelope in toPublish)
        {
            try
            {
                await _publisher.PublishAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                publishFailures.Add((envelope.ItemId, ex.Message));
                _logger.LogError(ex, "Requeue publish failed for item {ItemId}.", envelope.ItemId);
            }
        }

        if (publishFailures.Count > 0)
        {
            var failedIds = publishFailures.Select(f => f.ItemId).ToHashSet();
            var failedItems = await _db.MigrationJobItems
                .Include(i => i.Job)
                .Where(i => failedIds.Contains(i.ItemId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in failedItems)
            {
                var err = publishFailures.First(f => f.ItemId == item.ItemId).Error;
                item.Status = item.Job.Operation == MigrationOperationKind.Restore
                    ? MigrationLifecycleStatus.RestoreFailed
                    : MigrationLifecycleStatus.CopyToColdStorageFailed;
                item.LastError = $"Requeue publish failed: {err}";
                item.UpdatedAt = DateTime.UtcNow;
                item.CompletedAt = DateTime.UtcNow;
                _db.MigrationJobLogs.Add(new MigrationJobLog
                {
                    JobId = item.JobId,
                    ItemId = item.ItemId,
                    Status = item.Status,
                    Level = (int)LogLevel.Error,
                    Message = item.LastError!,
                    ActorUpn = upn,
                    Action = "Requeue",
                });
            }
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            result.Requeued -= publishFailures.Count;
            result.PublishFailed = publishFailures.Count;
        }

        _logger.LogInformation("Requeue by {Upn}: {Requeued} requeued, {Skipped} skipped, {Failed} publish-failed.",
            upn, result.Requeued, result.Skipped, result.PublishFailed);
        return result;
    }

    // Only failed lifecycle states may be requeued.
    private static readonly MigrationLifecycleStatus[] FailedStatuses =
    [
        MigrationLifecycleStatus.ValidationFailed,
        MigrationLifecycleStatus.CopyToColdStorageFailed,
        MigrationLifecycleStatus.DeleteFailed,
        MigrationLifecycleStatus.PlaceholderFailed,
        MigrationLifecycleStatus.RestoreFailed,
        MigrationLifecycleStatus.PlaceholderRemoveFailed,
    ];

    private static bool IsRequeueable(MigrationLifecycleStatus status) => FailedStatuses.Contains(status);

    // Reconstruct the bus envelope from the persisted item so the worker can
    // reprocess it. Returns null when required coordinates are missing.
    private static ColdStorageBusEnvelope? BuildEnvelope(MigrationJobItem item, string? upn)
    {
        if (item.Job.Operation == MigrationOperationKind.Migrate)
        {
            if (string.IsNullOrEmpty(item.BlobContainerName))
            {
                return null;
            }
            return new ColdStorageBusEnvelope
            {
                JobId = item.JobId,
                ItemId = item.ItemId,
                Operation = MigrationOperationKind.Migrate,
                ContainerName = item.BlobContainerName,
                RequestedByUpn = upn ?? item.Job.RequestedByUpn,
                Recursive = item.Recursive,
                File = new BaseSharePointFileInfo
                {
                    SiteUrl = item.SpSiteUrl,
                    WebUrl = string.IsNullOrEmpty(item.SpWebUrl) ? item.SpSiteUrl : item.SpWebUrl,
                    ServerRelativeFilePath = item.SpServerRelativeUrl,
                    LastModified = item.SourceLastModified ?? DateTime.UtcNow,
                    FileSize = item.FileSize,
                },
            };
        }

        if (string.IsNullOrEmpty(item.BlobContainerName) || string.IsNullOrEmpty(item.PlaceholderServerRelativeUrl))
        {
            return null;
        }
        return new ColdStorageBusEnvelope
        {
            JobId = item.JobId,
            ItemId = item.ItemId,
            Operation = MigrationOperationKind.Restore,
            ContainerName = item.BlobContainerName,
            RequestedByUpn = upn ?? item.Job.RequestedByUpn,
            RestoreTarget = new PlaceholderRestoreTarget
            {
                SiteUrl = item.SpSiteUrl,
                WebUrl = string.IsNullOrEmpty(item.SpWebUrl) ? item.SpSiteUrl : item.SpWebUrl,
                PlaceholderServerRelativeUrl = item.PlaceholderServerRelativeUrl,
                OriginalServerRelativeUrl = item.SpServerRelativeUrl,
            },
        };
    }
}
