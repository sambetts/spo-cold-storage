using Entities;
using Entities.DBEntities.ColdStorage;
using Entities.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Models;
using Models.ColdStorage;
using Web.Authorization;
using Web.Models.Api;
using Web.Services;

namespace Web.Controllers;

/// <summary>
/// <c>POST /api/migrations/start</c> – starts a migration request for one or
/// more selected SharePoint files or folders. Implements the contract from
/// requirements.md including site-owner authorization, container ACL
/// enforcement, idempotency, and acceptance with validation warnings.
/// </summary>
[Authorize]
[ApiController]
[Route("api/migrations")]
public class MigrationsController(
    SPOColdStorageDbContext db,
    Config config,
    ILogger<MigrationsController> logger,
    ISiteOwnerAuthorizationService siteOwners,
    IContainerAccessService containerAccess,
    IColdStorageBusPublisher publisher) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger<MigrationsController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ISiteOwnerAuthorizationService _siteOwners = siteOwners ?? throw new ArgumentNullException(nameof(siteOwners));
    private readonly IContainerAccessService _containerAccess = containerAccess ?? throw new ArgumentNullException(nameof(containerAccess));
    private readonly IColdStorageBusPublisher _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));

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

        if (!await _siteOwners.IsCallerSiteOwnerAsync(User, request.SiteUrl, cancellationToken).ConfigureAwait(false))
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
        await _db.MigrationJobs.AddAsync(job, cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        var queueWork = new List<ColdStorageBusEnvelope>();

        foreach (var dto in request.Items)
        {
            if (string.IsNullOrWhiteSpace(dto.ServerRelativeUrl))
            {
                warnings.Add("Skipping item with empty serverRelativeUrl.");
                continue;
            }

            // Idempotency: short-circuit if a non-terminal item already exists
            // for the same URL on this container. Avoids duplicate work without
            // silently dropping a legitimate retry of a failed job.
            var existing = await _db.MigrationJobItems
                .Where(i => i.SpServerRelativeUrl == dto.ServerRelativeUrl
                            && i.ContainerId == container.ID)
                .OrderByDescending(i => i.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null && !existing.Status.IsTerminal())
            {
                warnings.Add($"'{dto.ServerRelativeUrl}' is already in flight (status {existing.Status}); skipping.");
                continue;
            }

            var item = new MigrationJobItem
            {
                ItemId = Guid.NewGuid(),
                JobId = job.JobId,
                ItemKind = dto.ItemKind,
                Recursive = request.Recursive,
                SpSiteUrl = request.SiteUrl,
                SpWebUrl = request.WebUrl ?? string.Empty,
                SpServerRelativeUrl = dto.ServerRelativeUrl,
                FileSize = dto.FileSize,
                SourceLastModified = dto.LastModified,
                ContainerId = container.ID,
                BlobContainerName = container.BlobContainerName,
                Status = MigrationLifecycleStatus.Queued,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            await _db.MigrationJobItems.AddAsync(item, cancellationToken).ConfigureAwait(false);

            queueWork.Add(new ColdStorageBusEnvelope
            {
                JobId = job.JobId,
                ItemId = item.ItemId,
                Operation = MigrationOperationKind.Migrate,
                ContainerName = container.BlobContainerName,
                RequestedByUpn = upn,
                Recursive = request.Recursive,
                File = new BaseSharePointFileInfo
                {
                    SiteUrl = request.SiteUrl,
                    WebUrl = string.IsNullOrEmpty(request.WebUrl) ? request.SiteUrl : request.WebUrl,
                    ServerRelativeFilePath = dto.ServerRelativeUrl,
                    LastModified = dto.LastModified ?? DateTime.UtcNow,
                    FileSize = dto.FileSize,
                },
            });

            await _db.MigrationJobLogs.AddAsync(new MigrationJobLog
            {
                JobId = job.JobId,
                ItemId = item.ItemId,
                Status = MigrationLifecycleStatus.Queued,
                Level = (int)LogLevel.Information,
                Message = $"Queued for migration to '{container.Name}'.",
            }, cancellationToken).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (queueWork.Count == 0)
        {
            // Nothing to do - the job row carries the warnings so the SPFx UI
            // can render them, but no bus messages are produced. Persist the
            // accept-time warnings into the job's Summary AND surface them via
            // a synthetic log row so a GET /api/jobs/{id} after the accept
            // response is gone still tells the caller why nothing happened.
            job.Status = MigrationLifecycleStatus.CompletedWithWarning;
            job.Summary = warnings.Count > 0
                ? string.Join(" | ", warnings).Length > 1000
                    ? string.Join(" | ", warnings)[..1000]
                    : string.Join(" | ", warnings)
                : "No eligible items.";
            job.CompletedAt = DateTime.UtcNow;

            foreach (var w in warnings)
            {
                _db.MigrationJobLogs.Add(new MigrationJobLog
                {
                    JobId = job.JobId,
                    Status = MigrationLifecycleStatus.CompletedWithWarning,
                    Level = (int)LogLevel.Warning,
                    Message = w,
                });
            }
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Migration job {JobId} accepted but had no queueable items: {Warnings}", job.JobId, string.Join(" | ", warnings));
            return Accepted(new AcceptedJobResponse
            {
                JobId = job.JobId,
                Status = job.Status,
                Warnings = warnings,
            });
        }

        foreach (var envelope in queueWork)
        {
            await _publisher.PublishAsync(envelope, cancellationToken).ConfigureAwait(false);
        }

        return Accepted(new AcceptedJobResponse
        {
            JobId = job.JobId,
            Status = MigrationLifecycleStatus.Queued,
            Warnings = warnings,
        });
    }
}
