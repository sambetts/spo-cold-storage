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
