using Entities;
using Entities.Configuration;
using Entities.DBEntities.ColdStorage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Migration.Engine.Lifecycle;
using Migration.Engine.Restore;
using Models.ColdStorage;
using System.Text.Json;
using Web.Authorization;
using Web.Models.Api;
using Web.Services;

namespace Web.Controllers;

/// <summary>
/// <c>POST /api/restores/start</c> – starts a restore request for a
/// previously migrated item represented by a .url placeholder.
/// <c>POST /api/restores/force</c> – admin break-glass restore straight from a
/// blob, bypassing the queue and placeholder (issue #6).
/// </summary>
[Authorize]
[ApiController]
[Route("api/restores")]
public class RestoresController(
    SPOColdStorageDbContext db,
    Config config,
    ILogger<RestoresController> logger,
    ISiteOwnerAuthorizationService siteOwners,
    IContainerAccessService containerAccess,
    IColdStorageAdminAuthorizationService admin,
    IColdStorageBusPublisher publisher,
    IMigrationSubmissionQueue submissionQueue) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger<RestoresController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ISiteOwnerAuthorizationService _siteOwners = siteOwners ?? throw new ArgumentNullException(nameof(siteOwners));
    private readonly IContainerAccessService _containerAccess = containerAccess ?? throw new ArgumentNullException(nameof(containerAccess));
    private readonly IColdStorageAdminAuthorizationService _admin = admin ?? throw new ArgumentNullException(nameof(admin));
    private readonly IColdStorageBusPublisher _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly IMigrationSubmissionQueue _submissionQueue = submissionQueue ?? throw new ArgumentNullException(nameof(submissionQueue));

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
            ActorUpn = upn,
            Action = "Restore",
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

    /// <summary>
    /// Admin break-glass restore (issue #6). Runs synchronously and pushes a blob
    /// straight back to a target library, bypassing the queue and not requiring a
    /// placeholder — for when normal self-service restore can't run. Gated by
    /// admin authorization and fully audited.
    /// </summary>
    [HttpPost("force")]
    public async Task<ActionResult<AcceptedJobResponse>> ForceAsync([FromBody] ForceRestoreRequest request, CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        var upn = User.GetUpn();
        if (string.IsNullOrEmpty(upn))
        {
            return Unauthorized("Caller has no UPN claim.");
        }

        string? siteUrl = request.SiteUrl;
        string? blobContainerName = request.BlobContainerName;
        string? blobPath = request.BlobPath;
        string? target = request.TargetServerRelativeUrl;
        string? placeholder = null;
        int? containerId = null;

        if (request.ItemId is Guid itemId)
        {
            var src = await _db.MigrationJobItems
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.ItemId == itemId, cancellationToken)
                .ConfigureAwait(false);
            if (src is null)
            {
                return NotFound("No migration item with that id.");
            }
            siteUrl ??= src.SpSiteUrl;
            blobContainerName ??= src.BlobContainerName;
            blobPath ??= src.BlobPath;
            target ??= src.SpServerRelativeUrl;
            placeholder = src.PlaceholderServerRelativeUrl;
            containerId = src.ContainerId;
        }

        if (string.IsNullOrEmpty(siteUrl) || string.IsNullOrEmpty(blobContainerName)
            || string.IsNullOrEmpty(blobPath) || string.IsNullOrEmpty(target))
        {
            return BadRequest("Provide itemId, or all of siteUrl + blobContainerName + blobPath + targetServerRelativeUrl.");
        }

        var job = new MigrationJob
        {
            JobId = Guid.NewGuid(),
            Operation = MigrationOperationKind.Restore,
            RequestedByUpn = upn,
            SiteUrl = siteUrl,
            ContainerId = containerId,
            ConflictBehavior = request.ConflictBehavior,
            Status = MigrationLifecycleStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Summary = "Admin break-glass restore.",
        };
        var item = new MigrationJobItem
        {
            ItemId = Guid.NewGuid(),
            JobId = job.JobId,
            SpSiteUrl = siteUrl,
            SpServerRelativeUrl = target,
            PlaceholderServerRelativeUrl = placeholder,
            ContainerId = containerId,
            BlobContainerName = blobContainerName,
            BlobPath = blobPath,
            Status = MigrationLifecycleStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.MigrationJobs.Add(job);
        _db.MigrationJobItems.Add(item);
        _db.MigrationJobLogs.Add(new MigrationJobLog
        {
            JobId = job.JobId,
            ItemId = item.ItemId,
            Status = MigrationLifecycleStatus.Queued,
            Level = (int)LogLevel.Warning,
            Message = $"Break-glass restore requested by {upn} for '{blobContainerName}/{blobPath}' -> '{target}'.",
            ActorUpn = upn,
            Action = "ForceRestore",
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var writer = new JobStatusWriter(_db, _logger);
        var pipeline = new SharePointRestorePipeline(_config, _logger, writer);
        var ok = await pipeline.ForceRestoreFromBlobAsync(
            job.JobId, item.ItemId, siteUrl, blobContainerName, blobPath, target,
            request.ConflictBehavior, placeholder, cancellationToken).ConfigureAwait(false);

        var finalStatus = await _db.MigrationJobItems
            .AsNoTracking()
            .Where(i => i.ItemId == item.ItemId)
            .Select(i => i.Status)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Break-glass restore {JobId} by {Upn} finished: success={Ok}, status={Status}.", job.JobId, upn, ok, finalStatus);

        var response = new AcceptedJobResponse { JobId = job.JobId, Status = finalStatus };
        return ok ? Ok(response) : StatusCode(StatusCodes.Status502BadGateway, response);
    }

    /// <summary>
    /// Bulk/folder restore (issue #9): restores many placeholders — and every
    /// archived item under any supplied folder — in a single job. Poll
    /// <c>GET /api/jobs/{jobId}</c> for aggregated progress.
    /// </summary>
    [HttpPost("start-batch")]
    public async Task<ActionResult<BatchRestoreResponse>> StartBatchAsync([FromBody] StartBatchRestoreRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrEmpty(request.SiteUrl)
            || (request.Placeholders.Count == 0 && request.FolderServerRelativeUrls.Count == 0))
        {
            return BadRequest("siteUrl and at least one placeholder or folder are required.");
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

        // Async submit (fixes the large-folder "Failed to fetch" timeout): authorise the container
        // ACL now (few containers, cheap), persist the selection, and hand the folder expansion +
        // per-item creation + publish to the background expander. Returns 202 immediately instead
        // of blocking the request until every archived file is resolved, created and published.
        var allowedContainerIds = new List<int>();
        var containers = await _db.ColdStorageContainers.Include(c => c.Acls).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var c in containers)
        {
            if (await _containerAccess.CanAsync(User, c, ContainerAction.Restore, cancellationToken).ConfigureAwait(false))
            {
                allowedContainerIds.Add(c.ID);
            }
        }

        var job = new MigrationJob
        {
            JobId = Guid.NewGuid(),
            Operation = MigrationOperationKind.Restore,
            RequestedByUpn = upn,
            SiteUrl = request.SiteUrl,
            WebUrl = request.WebUrl ?? string.Empty,
            ConflictBehavior = request.ConflictBehavior,
            Status = MigrationLifecycleStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Summary = "Bulk restore.",
            SubmissionRequestJson = JsonSerializer.Serialize(new RestoreSubmission
            {
                Placeholders = request.Placeholders.Where(p => !string.IsNullOrWhiteSpace(p)).ToList(),
                FolderServerRelativeUrls = request.FolderServerRelativeUrls.Where(f => !string.IsNullOrWhiteSpace(f)).ToList(),
                AllowedContainerIds = allowedContainerIds,
            }),
        };

        await _db.MigrationJobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _submissionQueue.Enqueue(job.JobId);
        _logger.LogInformation("Accepted bulk-restore job {JobId} for background expansion ({Placeholders} placeholder(s) + {Folders} folder(s) selected).",
            job.JobId, request.Placeholders.Count, request.FolderServerRelativeUrls.Count);

        return Accepted(new BatchRestoreResponse
        {
            JobId = job.JobId,
            Status = MigrationLifecycleStatus.Queued,
            // Selection count (not the resolved per-file count, which the background expansion
            // determines). Non-zero so an older client doesn't misread it as "nothing to restore".
            Accepted = request.Placeholders.Count + request.FolderServerRelativeUrls.Count,
            Warnings = [],
        });
    }
}
