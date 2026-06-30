using Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Models.ColdStorage;
using Web.Models.Api;
using Web.Services;

namespace Web.Controllers;

/// <summary>
/// Pre-archive notice admin surface (issue #17).
/// <c>POST /api/admin/pre-archive/evaluate</c> – run the grace-period workflow for
/// a set of candidate files (the action a future auto-archive scheduler performs):
/// the first call sends a notice + starts the grace window, later calls report
/// whether the window has elapsed.
/// <c>GET  /api/admin/pre-archive/notices</c> – list pending notices (the
/// in-product notification surface).
///
/// Scope note: the auto-archive TRIGGER/scheduler itself isn't defined yet, so
/// this delivers the notification + grace mechanism it will call. Today's
/// archiving is user-initiated and needs no advance notice.
/// </summary>
[Authorize]
[ApiController]
[Route("api/admin/pre-archive")]
public class PreArchiveController(
    SPOColdStorageDbContext db,
    PreArchiveNoticeService notices,
    IColdStorageAdminAuthorizationService admin) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly PreArchiveNoticeService _notices = notices ?? throw new ArgumentNullException(nameof(notices));
    private readonly IColdStorageAdminAuthorizationService _admin = admin ?? throw new ArgumentNullException(nameof(admin));

    [HttpPost("evaluate")]
    public async Task<ActionResult<IEnumerable<PreArchiveEvaluationResult>>> EvaluateAsync([FromBody] EvaluatePreArchiveRequest request, CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }
        if (request is null || string.IsNullOrEmpty(request.SiteUrl) || request.Items.Count == 0)
        {
            return BadRequest("siteUrl and at least one item are required.");
        }

        var results = new List<PreArchiveEvaluationResult>();
        foreach (var item in request.Items)
        {
            if (string.IsNullOrWhiteSpace(item.ServerRelativeUrl))
            {
                continue;
            }
            var decision = await _notices.EvaluateAsync(request.SiteUrl, item.ServerRelativeUrl, item.OwnerUpn, cancellationToken).ConfigureAwait(false);
            results.Add(new PreArchiveEvaluationResult
            {
                ServerRelativeUrl = item.ServerRelativeUrl,
                Decision = decision.ToString(),
            });
        }
        return results;
    }

    [HttpGet("notices")]
    public async Task<ActionResult<IEnumerable<PreArchiveNoticeResponse>>> ListAsync([FromQuery] int? take, CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }
        var capped = take is null ? 200 : Math.Clamp(take.Value, 1, 2000);
        var rows = await _db.PreArchiveNotices
            .AsNoTracking()
            .Where(n => n.Status == PreArchiveNoticeStatus.Pending)
            .OrderBy(n => n.GraceUntil)
            .Take(capped)
            .Select(n => new PreArchiveNoticeResponse
            {
                Id = n.ID,
                SiteUrl = n.SiteUrl,
                ServerRelativeUrl = n.ServerRelativeUrl,
                NotifiedUpn = n.NotifiedUpn,
                NotifiedAt = n.NotifiedAt,
                GraceUntil = n.GraceUntil,
                Status = n.Status.ToString(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows;
    }
}
