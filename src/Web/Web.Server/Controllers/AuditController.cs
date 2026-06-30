using Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Web.Models.Api;
using Web.Services;

namespace Web.Controllers;

/// <summary>
/// <c>GET /api/audit</c> – admin-visible audit view of cold-storage
/// downloads/restores/migrations (issue #13). Reads the user-initiated rows
/// (those with an Action) from migration_job_logs, newest first, with optional
/// filters by action and actor.
///
/// Scope note: this covers our-side auditing only. External-portal downloads
/// and surfacing in Purview / the M365 unified audit log are platform
/// limitations outside this system.
/// </summary>
[Authorize]
[ApiController]
[Route("api/audit")]
public class AuditController(
    SPOColdStorageDbContext db,
    IColdStorageAdminAuthorizationService admin) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IColdStorageAdminAuthorizationService _admin = admin ?? throw new ArgumentNullException(nameof(admin));

    [HttpGet]
    public async Task<ActionResult<IEnumerable<AuditEntryResponse>>> GetAsync(
        [FromQuery] string? action,
        [FromQuery] string? actorUpn,
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }

        var capped = take is null ? 200 : Math.Clamp(take.Value, 1, 2000);

        var query = _db.MigrationJobLogs
            .AsNoTracking()
            .Where(l => l.Action != null);

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(l => l.Action == action);
        }
        if (!string.IsNullOrWhiteSpace(actorUpn))
        {
            query = query.Where(l => l.ActorUpn == actorUpn);
        }

        var rows = await query
            .OrderByDescending(l => l.Timestamp)
            .Take(capped)
            .Select(l => new AuditEntryResponse
            {
                Timestamp = l.Timestamp,
                ActorUpn = l.ActorUpn,
                Action = l.Action,
                JobId = l.JobId,
                ItemId = l.ItemId,
                ItemUrl = l.Item != null ? l.Item.SpServerRelativeUrl : null,
                Message = l.Message,
                Status = l.Status,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows;
    }
}
