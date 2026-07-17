using Entities;
using Entities.DBEntities.ColdStorage;
using Entities.Configuration;
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
    IArchiveEligibilityEvaluator eligibility,
    ISharePointFolderExpansionService folderExpansion,
    IColdStorageBusPublisher publisher) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger<MigrationsController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ISiteOwnerAuthorizationService _siteOwners = siteOwners ?? throw new ArgumentNullException(nameof(siteOwners));
    private readonly IContainerAccessService _containerAccess = containerAccess ?? throw new ArgumentNullException(nameof(containerAccess));
    private readonly IArchiveEligibilityEvaluator _eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility));
    private readonly ISharePointFolderExpansionService _folderExpansion = folderExpansion ?? throw new ArgumentNullException(nameof(folderExpansion));
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
        // Coalesce high-volume skip categories so re-submitting a large folder does
        // not emit thousands of near-identical warnings into the job record, the
        // accept response, and the UI (a 4,000-file folder previously produced 4,000
        // "already in flight; skipping" lines).
        var alreadyInFlight = 0;
        var notEligible = 0;
        var emptyPaths = 0;
        var notEligibleSamples = new List<string>();
        var queueWork = new List<ColdStorageBusEnvelope>();

        // Expand any selected folders into their constituent files up front, so the
        // per-file loop below (and the worker) only ever deal with individual files.
        // A folder handed straight to the migrator was treated as a single file:
        // it downloaded garbage then failed at the delete step (issue: folders can't
        // be migrated). Files are de-duplicated so a file selected both directly and
        // via its parent folder is only queued once, and a request-wide file cap keeps
        // one submit from enqueueing an unbounded number of items.
        var requestFileCap = _config.ColdStorageMaxFilesPerRequest > 0 ? _config.ColdStorageMaxFilesPerRequest : 5000;
        var expandedItems = new List<StartMigrationItem>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in request.Items)
        {
            if (string.IsNullOrWhiteSpace(dto.ServerRelativeUrl))
            {
                emptyPaths++;
                continue;
            }

            if (expandedItems.Count >= requestFileCap)
            {
                break;
            }

            if (dto.ItemKind == ColdStorageItemKind.Folder)
            {
                var expansion = await _folderExpansion
                    .ExpandAsync(request.SiteUrl, dto.ServerRelativeUrl, request.Recursive, requestFileCap - expandedItems.Count, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrEmpty(expansion.Warning))
                {
                    warnings.Add(expansion.Warning);
                }
                foreach (var f in expansion.Files)
                {
                    if (seenUrls.Add(f.ServerRelativeUrl))
                    {
                        expandedItems.Add(new StartMigrationItem
                        {
                            ServerRelativeUrl = f.ServerRelativeUrl,
                            ItemKind = ColdStorageItemKind.File,
                            FileSize = f.FileSize,
                            LastModified = f.LastModified,
                        });
                    }
                }
            }
            else if (seenUrls.Add(dto.ServerRelativeUrl))
            {
                expandedItems.Add(dto);
            }
        }

        if (expandedItems.Count >= requestFileCap)
        {
            warnings.Add($"This request hit the {requestFileCap:N0}-file limit; not all files in the selection were queued. Migrate the remaining subfolders separately, or raise ColdStorageMaxFilesPerRequest.");
        }

        foreach (var dto in expandedItems)
        {
            if (string.IsNullOrWhiteSpace(dto.ServerRelativeUrl))
            {
                emptyPaths++;
                continue;
            }

            // Idempotency: short-circuit if a non-terminal item already exists
            // for the same URL on this container. Avoids duplicate work without
            // silently dropping a legitimate retry of a failed job.
            //
            // SELF-HEAL: if the existing row is still `Queued` and was created
            // more than 5 minutes ago, treat it as an orphan from a previous
            // publish failure (DB row written, bus publish threw / app crashed
            // between SaveChanges and SendMessageAsync). Cancel it and let this
            // request re-create + republish. Without this, a single publish
            // failure permanently blocks any future retry for that file because
            // the controller keeps seeing the stuck-Queued row and skipping.
            var existing = await _db.MigrationJobItems
                .Where(i => i.SpServerRelativeUrl == dto.ServerRelativeUrl
                            && i.ContainerId == container.ID)
                .OrderByDescending(i => i.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null && !existing.Status.IsTerminal())
            {
                var ageMinutes = (DateTime.UtcNow - existing.UpdatedAt).TotalMinutes;
                if (existing.Status == MigrationLifecycleStatus.Queued && ageMinutes > 5)
                {
                    existing.Status = MigrationLifecycleStatus.Cancelled;
                    existing.LastError = $"Auto-cancelled by retry: item was {(int)ageMinutes} min old in 'Queued' (likely orphaned by an earlier publish failure).";
                    existing.UpdatedAt = DateTime.UtcNow;
                    existing.CompletedAt = DateTime.UtcNow;
                    _db.MigrationJobLogs.Add(new MigrationJobLog
                    {
                        JobId = existing.JobId,
                        ItemId = existing.ItemId,
                        Status = MigrationLifecycleStatus.Cancelled,
                        Level = (int)LogLevel.Warning,
                        Message = $"Auto-cancelled to unblock retry. Was stuck in Queued for {(int)ageMinutes} min - likely a publish failure on the original submit.",
                    });
                    // Fall through and create a fresh item below.
                }
                else
                {
                    alreadyInFlight++;
                    continue;
                }
            }

            // Eligibility gate (issue #2): skip items that fail the configured
            // size / file-type rules before queueing, with a clear reason
            // surfaced to the caller (and persisted via the no-work path below).
            var eligibility = await _eligibility.EvaluateAsync(new ArchiveCandidate
            {
                ServerRelativeUrl = dto.ServerRelativeUrl,
                SiteUrl = request.SiteUrl,
                WebUrl = request.WebUrl ?? string.Empty,
                FileSizeBytes = dto.FileSize,
                ItemKind = dto.ItemKind,
                LastModified = dto.LastModified,
            }, cancellationToken).ConfigureAwait(false);
            if (!eligibility.IsEligible)
            {
                notEligible++;
                if (notEligibleSamples.Count < 3)
                {
                    notEligibleSamples.Add($"'{dto.ServerRelativeUrl}': {eligibility.SkipReason}");
                }
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
                Priority = request.Priority,
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
                ActorUpn = upn,
                Action = "Migrate",
            }, cancellationToken).ConfigureAwait(false);
        }

        // Fold the coalesced skip categories into a few summary warnings instead of
        // one line per skipped file.
        if (emptyPaths > 0)
        {
            warnings.Add($"{emptyPaths} item(s) skipped: empty path.");
        }
        if (alreadyInFlight > 0)
        {
            warnings.Add($"{alreadyInFlight} item(s) already queued or in progress; skipped to avoid duplicate work.");
        }
        if (notEligible > 0)
        {
            warnings.Add($"{notEligible} item(s) skipped as not eligible for archiving.");
            warnings.AddRange(notEligibleSamples.Select(s => "  • " + s));
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

        // Persist accept-time warnings (folder-expansion caps, skipped items, …) as
        // job-level log rows so they remain visible when the job is reopened later,
        // not only in the synchronous accept response. Publish failures below are
        // logged per-item separately, so this runs before publishing to avoid dupes.
        if (warnings.Count > 0)
        {
            foreach (var w in warnings)
            {
                _db.MigrationJobLogs.Add(new MigrationJobLog
                {
                    JobId = job.JobId,
                    Status = MigrationLifecycleStatus.Queued,
                    Level = (int)LogLevel.Warning,
                    Message = w,
                });
            }
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // Publish decoupled from the request lifetime: use CancellationToken.None so a
        // client disconnect (the SPFx call giving up on a large submit) can't abort the
        // publish and orphan un-enqueued items — the exact failure that froze a
        // 4,000-file job. Batched for throughput. Every item is already persisted as
        // Queued above, so anything not sent here (a publish error, or a crash before
        // this line) is re-driven by the worker's dispatch reconciler within the
        // enqueue grace, rather than sitting Queued with no message forever.
        DateTime? enqueuedAt = DateTime.UtcNow;
        try
        {
            await _publisher.PublishManyAsync(queueWork, CancellationToken.None).ConfigureAwait(false);
            await _db.MigrationJobItems
                .Where(i => i.JobId == job.JobId && i.Status == MigrationLifecycleStatus.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.LastEnqueuedAt, enqueuedAt), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Non-fatal: items stay Queued (LastEnqueuedAt unset) and the dispatch
            // reconciler re-drives them, so a transient Service Bus blip no longer
            // fails the submit or loses items.
            _logger.LogError(ex, "Batch publish for job {JobId} failed; {Count} item(s) will be re-driven by the dispatch reconciler.", job.JobId, queueWork.Count);
            warnings.Add($"{queueWork.Count} item(s) could not be enqueued immediately and will be retried automatically.");
        }

        return Accepted(new AcceptedJobResponse
        {
            JobId = job.JobId,
            Status = MigrationLifecycleStatus.Queued,
            Warnings = warnings,
        });
    }
}
