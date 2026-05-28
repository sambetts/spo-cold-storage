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
/// <c>POST /api/restores/start</c> – starts a restore request for a
/// previously migrated item represented by a .url placeholder.
/// </summary>
[Authorize]
[ApiController]
[Route("api/restores")]
public class RestoresController(
    SPOColdStorageDbContext db,
    ILogger<RestoresController> logger,
    ISiteOwnerAuthorizationService siteOwners,
    IContainerAccessService containerAccess,
    IColdStorageBusPublisher publisher) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly ILogger<RestoresController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ISiteOwnerAuthorizationService _siteOwners = siteOwners ?? throw new ArgumentNullException(nameof(siteOwners));
    private readonly IContainerAccessService _containerAccess = containerAccess ?? throw new ArgumentNullException(nameof(containerAccess));
    private readonly IColdStorageBusPublisher _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));

    [HttpPost("start")]
    public async Task<ActionResult<AcceptedJobResponse>> StartAsync([FromBody] StartRestoreRequest request, CancellationToken cancellationToken)
    {
        if (request is null
            || string.IsNullOrEmpty(request.SiteUrl)
            || string.IsNullOrEmpty(request.PlaceholderServerRelativeUrl))
        {
            return BadRequest("siteUrl and placeholderServerRelativeUrl are required.");
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

        // Idempotency: refuse to restore the same placeholder while a previous
        // restore is in flight.
        var inFlight = await _db.MigrationJobItems
            .Where(i => i.PlaceholderServerRelativeUrl == request.PlaceholderServerRelativeUrl
                        && i.Job.Operation == MigrationOperationKind.Restore)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (inFlight is not null && !inFlight.Status.IsTerminal())
        {
            return Accepted(new AcceptedJobResponse
            {
                JobId = inFlight.JobId,
                Status = inFlight.Status,
                Warnings = { $"Restore already in progress (status {inFlight.Status}); reusing existing job." },
            });
        }

        // Resolve container from the existing migrate row so the caller doesn't
        // need to remember it. If we can't find one, the SPFx component should
        // have invoked /api/placeholders/resolve first.
        var migrateItem = await _db.MigrationJobItems
            .Where(i => i.PlaceholderServerRelativeUrl == request.PlaceholderServerRelativeUrl)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (migrateItem is null || string.IsNullOrEmpty(migrateItem.BlobContainerName) || migrateItem.ContainerId is null)
        {
            return NotFound("Could not locate the source migration record for that placeholder.");
        }

        var container = await _db.ColdStorageContainers
            .Include(c => c.Acls)
            .FirstOrDefaultAsync(c => c.ID == migrateItem.ContainerId, cancellationToken)
            .ConfigureAwait(false);
        if (container is null)
        {
            return NotFound("Cold-storage container has been removed from configuration.");
        }
        if (!await _containerAccess.CanAsync(User, container, ContainerAction.Restore, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }

        var job = new MigrationJob
        {
            JobId = Guid.NewGuid(),
            Operation = MigrationOperationKind.Restore,
            RequestedByUpn = upn,
            SiteUrl = request.SiteUrl,
            WebUrl = request.WebUrl ?? string.Empty,
            ContainerId = container.ID,
            ConflictBehavior = request.ConflictBehavior,
            Status = MigrationLifecycleStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var item = new MigrationJobItem
        {
            ItemId = Guid.NewGuid(),
            JobId = job.JobId,
            SpSiteUrl = request.SiteUrl,
            SpWebUrl = request.WebUrl ?? string.Empty,
            SpServerRelativeUrl = request.OriginalServerRelativeUrl ?? migrateItem.SpServerRelativeUrl,
            PlaceholderServerRelativeUrl = request.PlaceholderServerRelativeUrl,
            ContainerId = container.ID,
            BlobContainerName = migrateItem.BlobContainerName,
            BlobPath = migrateItem.BlobPath,
            BlobUrl = migrateItem.BlobUrl,
            Status = MigrationLifecycleStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await _db.MigrationJobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
        await _db.MigrationJobItems.AddAsync(item, cancellationToken).ConfigureAwait(false);
        await _db.MigrationJobLogs.AddAsync(new MigrationJobLog
        {
            JobId = job.JobId,
            ItemId = item.ItemId,
            Status = MigrationLifecycleStatus.Queued,
            Level = (int)LogLevel.Information,
            Message = $"Queued for restore from '{container.Name}'.",
        }, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _publisher.PublishAsync(new ColdStorageBusEnvelope
        {
            JobId = job.JobId,
            ItemId = item.ItemId,
            Operation = MigrationOperationKind.Restore,
            ContainerName = migrateItem.BlobContainerName!,
            RequestedByUpn = upn,
            ConflictBehavior = request.ConflictBehavior,
            RestoreTarget = new PlaceholderRestoreTarget
            {
                SiteUrl = request.SiteUrl,
                WebUrl = string.IsNullOrEmpty(request.WebUrl) ? request.SiteUrl : request.WebUrl,
                PlaceholderServerRelativeUrl = request.PlaceholderServerRelativeUrl,
                OriginalServerRelativeUrl = item.SpServerRelativeUrl,
            },
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Restore job {JobId} accepted for placeholder {Placeholder}.", job.JobId, request.PlaceholderServerRelativeUrl);
        return Accepted(new AcceptedJobResponse
        {
            JobId = job.JobId,
            Status = MigrationLifecycleStatus.Queued,
        });
    }
}
