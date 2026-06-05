using Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Web.Models.Api;

namespace Web.Controllers;

/// <summary>
/// <c>GET /api/jobs/{jobId}</c> – status rollup.
/// <c>GET /api/jobs/{jobId}/logs</c> – detailed audit log.
/// </summary>
[Authorize]
[ApiController]
[Route("api/jobs")]
public class JobsController(SPOColdStorageDbContext db, ILogger<JobsController> logger) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly ILogger<JobsController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    [HttpGet("{jobId:guid}")]
    public async Task<ActionResult<JobStatusResponse>> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _db.MigrationJobs
            .Include(j => j.Container)
            .Include(j => j.Items)
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.JobId == jobId, cancellationToken)
            .ConfigureAwait(false);
        if (job is null)
        {
            return NotFound();
        }
        return await BuildJobResponseAsync(job, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /api/jobs?siteUrl={url}&amp;take={n}</c> – recent jobs for a single
    /// SharePoint site collection (used by the SPFx COLDSTORAGE_STATUS toolbar
    /// command to surface activity without requiring a file selection). Ordered
    /// by created_at desc, capped at 100. The endpoint does NOT enforce the
    /// site-owner check on read because the per-job payload only carries
    /// metadata the caller would have already seen in the SPFx UI (server URLs,
    /// item statuses, job IDs); we rely on the user being signed in (the
    /// [Authorize] attribute above) plus the siteUrl filter.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<JobStatusResponse>>> ListAsync(
        [FromQuery] string? siteUrl,
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(siteUrl))
        {
            return BadRequest("siteUrl query parameter is required.");
        }

        var capped = take is null ? 20 : Math.Clamp(take.Value, 1, 100);
        var trimmedSite = siteUrl.TrimEnd('/');

        var jobs = await _db.MigrationJobs
            .Include(j => j.Container)
            .Include(j => j.Items)
            .AsNoTracking()
            .Where(j => j.SiteUrl == siteUrl || j.SiteUrl == trimmedSite)
            .OrderByDescending(j => j.CreatedAt)
            .Take(capped)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = new List<JobStatusResponse>(jobs.Count);
        foreach (var job in jobs)
        {
            result.Add(await BuildJobResponseAsync(job, cancellationToken).ConfigureAwait(false));
        }
        return result;
    }

    private async Task<JobStatusResponse> BuildJobResponseAsync(Entities.DBEntities.ColdStorage.MigrationJob job, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        foreach (var i in job.Items)
        {
            if (!string.IsNullOrEmpty(i.LastError))
            {
                if (i.Status is global::Models.ColdStorage.MigrationLifecycleStatus.CompletedWithWarning
                             or global::Models.ColdStorage.MigrationLifecycleStatus.PlaceholderRemoveFailed)
                {
                    warnings.Add($"{i.SpServerRelativeUrl}: {i.LastError}");
                }
                else
                {
                    errors.Add($"{i.SpServerRelativeUrl}: {i.LastError}");
                }
            }
        }

        // Also include job-level warning log rows (e.g. the "no eligible items"
        // accept-time warnings that aren't tied to a specific item). Without
        // these, a job that was rejected before any item was queued would come
        // back from GET /api/jobs/{id} with an empty warnings list — leaving
        // the SPFx UI showing "Job has no items yet" with no explanation.
        var jobLevelWarnings = await _db.MigrationJobLogs
            .Where(l => l.JobId == job.JobId && l.ItemId == null && l.Level == (int)LogLevel.Warning)
            .OrderBy(l => l.Timestamp)
            .Select(l => l.Message)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var w in jobLevelWarnings)
        {
            if (!warnings.Contains(w))
            {
                warnings.Add(w);
            }
        }

        return new JobStatusResponse
        {
            JobId = job.JobId,
            Operation = job.Operation,
            Status = job.Status,
            Summary = job.Summary,
            SiteUrl = job.SiteUrl,
            RequestedByUpn = job.RequestedByUpn,
            ContainerName = job.Container?.Name,
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            CompletedAt = job.CompletedAt,
            Items = job.Items
                .OrderBy(i => i.CreatedAt)
                .Select(i => new JobItemStatusResponse
                {
                    ItemId = i.ItemId,
                    SpServerRelativeUrl = i.SpServerRelativeUrl,
                    PlaceholderServerRelativeUrl = i.PlaceholderServerRelativeUrl,
                    ItemKind = i.ItemKind,
                    Status = i.Status,
                    Attempts = i.Attempts,
                    LastError = i.LastError,
                    ValidatedAt = i.ValidatedAt,
                    CopiedAt = i.CopiedAt,
                    SourceDeletedAt = i.SourceDeletedAt,
                    PlaceholderCreatedAt = i.PlaceholderCreatedAt,
                    RestoredAt = i.RestoredAt,
                    CompletedAt = i.CompletedAt,
                })
                .ToList(),
            Warnings = warnings,
            Errors = errors,
        };
    }

    [HttpGet("{jobId:guid}/logs")]
    public async Task<ActionResult<IEnumerable<JobLogEntryResponse>>> GetLogsAsync(Guid jobId, [FromQuery] int? take, CancellationToken cancellationToken)
    {
        var capped = take is null ? 500 : Math.Clamp(take.Value, 1, 5000);
        var exists = await _db.MigrationJobs.AnyAsync(j => j.JobId == jobId, cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            return NotFound();
        }
        var logs = await _db.MigrationJobLogs
            .Where(l => l.JobId == jobId)
            .OrderBy(l => l.Timestamp)
            .Take(capped)
            .Select(l => new JobLogEntryResponse
            {
                ItemId = l.ItemId,
                Timestamp = l.Timestamp,
                Status = l.Status,
                Level = l.Level,
                Message = l.Message,
                Exception = l.Exception,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return logs;
    }
}
