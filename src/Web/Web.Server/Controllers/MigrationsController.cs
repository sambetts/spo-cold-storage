using Entities;
using Entities.DBEntities.ColdStorage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Models.ColdStorage;
using System.Text.Json;
using Web.Authorization;
using Web.Models.Api;
using Web.Services;

namespace Web.Controllers;

/// <summary>
/// <c>POST /api/migrations/start</c> – starts a migration request for one or
/// more selected SharePoint files or folders. Implements the contract from
/// requirements.md including site-owner authorization, container ACL
/// enforcement, and acceptance.
///
/// The request only validates + authorizes + records the job, then hands the
/// (potentially minutes-long) folder expansion + per-file item creation + enqueue
/// to <see cref="MigrationExpander"/> via a background service, so a large-folder
/// submit returns immediately instead of blocking the request until every file is
/// enumerated and inserted. The SPFx/SPA poll the job for progress.
/// </summary>
[Authorize]
[ApiController]
[Route("api/migrations")]
public class MigrationsController(
    SPOColdStorageDbContext db,
    ILogger<MigrationsController> logger,
    ISiteContributorAuthorizationService siteContributors,
    IContainerAccessService containerAccess,
    IMigrationSubmissionQueue submissionQueue) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly ILogger<MigrationsController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ISiteContributorAuthorizationService _siteContributors = siteContributors ?? throw new ArgumentNullException(nameof(siteContributors));
    private readonly IContainerAccessService _containerAccess = containerAccess ?? throw new ArgumentNullException(nameof(containerAccess));
    private readonly IMigrationSubmissionQueue _submissionQueue = submissionQueue ?? throw new ArgumentNullException(nameof(submissionQueue));

    [HttpPost("start")]
    public async Task<ActionResult<AcceptedJobResponse>> StartAsync([FromBody] StartMigrationRequest request, CancellationToken cancellationToken)
    {
        if (request is null
            || string.IsNullOrEmpty(request.SiteUrl)
            || string.IsNullOrEmpty(request.ContainerName)
            || request.Items.Count == 0)
        {
            return BadRequest("siteUrl, containerName and at least one item are required.");
        }

        var upn = User.GetUpn();
        if (string.IsNullOrEmpty(upn))
        {
            return Unauthorized("Caller has no UPN claim.");
        }

        if (!await _siteContributors.IsCallerSiteContributorAsync(User, request.SiteUrl, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }

        var container = await _containerAccess.ResolveAsync(request.ContainerName, cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            return BadRequest($"Unknown cold-storage container '{request.ContainerName}'.");
        }
        if (!await _containerAccess.CanAsync(User, container, ContainerAction.Migrate, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }

        var job = new MigrationJob
        {
            JobId = Guid.NewGuid(),
            Operation = MigrationOperationKind.Migrate,
            RequestedByUpn = upn,
            SiteUrl = request.SiteUrl,
            WebUrl = request.WebUrl ?? string.Empty,
            ContainerId = container.ID,
            Status = MigrationLifecycleStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Persist the selection as the job's submission; a background service expands
        // folders, creates the per-file items and enqueues them off the request thread.
        var submission = new MigrationSubmission
        {
            Recursive = request.Recursive,
            CopyMetadataColumns = request.CopyMetadataColumns,
            Priority = request.Priority,
            Items = request.Items.Select(i => new MigrationSubmissionItem
            {
                ServerRelativeUrl = i.ServerRelativeUrl,
                ItemKind = i.ItemKind,
                FileSize = i.FileSize,
                LastModified = i.LastModified,
            }).ToList(),
        };
        job.SubmissionRequestJson = JsonSerializer.Serialize(submission);

        await _db.MigrationJobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _submissionQueue.Enqueue(job.JobId);
        _logger.LogInformation(
            "Accepted migrate job {JobId} for background expansion ({Count} selected item(s)).",
            job.JobId, request.Items.Count);

        return Accepted(new AcceptedJobResponse
        {
            JobId = job.JobId,
            Status = MigrationLifecycleStatus.Queued,
            Warnings = [],
        });
    }
}
