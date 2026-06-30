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
    IColdStorageBusPublisher publisher) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger<MigrationsController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ISiteOwnerAuthorizationService _siteOwners = siteOwners ?? throw new ArgumentNullException(nameof(siteOwners));
    private readonly IContainerAccessService _containerAccess = containerAccess ?? throw new ArgumentNullException(nameof(containerAccess));
    private readonly IArchiveEligibilityEvaluator _eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility));
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
                    warnings.Add($"'{dto.ServerRelativeUrl}' is already in flight (status {existing.Status}); skipping.");
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
                warnings.Add($"'{dto.ServerRelativeUrl}' skipped: {eligibility.SkipReason}");
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

        // Publish each envelope. If a publish fails, mark the corresponding DB row
        // as CopyToColdStorageFailed with the error text so:
        //   (1) the SPFx UI sees the failure rather than a row stuck in Queued forever
        //   (2) the idempotency check above will let the user resubmit immediately
        //       (failed items are terminal -> the next call creates a fresh row)
        //   (3) ops can correlate the worker log to the specific item
        // We use Promise.allSettled-style accounting: keep going even if one fails so
        // partial submits at least queue the publishable items.
        var publishFailures = new List<(Guid ItemId, string Error)>();
        foreach (var envelope in queueWork)
        {
            try
            {
                await _publisher.PublishAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                publishFailures.Add((envelope.ItemId, ex.Message));
                _logger.LogError(ex, "Failed to publish envelope for item {ItemId} on job {JobId}.", envelope.ItemId, envelope.JobId);
            }
        }

        if (publishFailures.Count > 0)
        {
            var failedIds = publishFailures.Select(f => f.ItemId).ToHashSet();
            var failedItems = await _db.MigrationJobItems
                .Where(i => failedIds.Contains(i.ItemId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in failedItems)
            {
                var err = publishFailures.First(f => f.ItemId == item.ItemId).Error;
                item.Status = MigrationLifecycleStatus.CopyToColdStorageFailed;
                item.LastError = $"Failed to publish to Service Bus: {err}";
                item.UpdatedAt = DateTime.UtcNow;
                item.CompletedAt = DateTime.UtcNow;
                _db.MigrationJobLogs.Add(new MigrationJobLog
                {
                    JobId = item.JobId,
                    ItemId = item.ItemId,
                    Status = MigrationLifecycleStatus.CopyToColdStorageFailed,
                    Level = (int)LogLevel.Error,
                    Message = item.LastError!,
                });
                warnings.Add($"'{item.SpServerRelativeUrl}': publish failed - {err}");
            }
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return Accepted(new AcceptedJobResponse
        {
            JobId = job.JobId,
            Status = MigrationLifecycleStatus.Queued,
            Warnings = warnings,
        });
    }
}
