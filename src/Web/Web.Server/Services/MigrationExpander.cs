using Entities;
using Entities.Configuration;
using Entities.DBEntities.ColdStorage;
using Microsoft.EntityFrameworkCore;
using Migration.Engine.Migration;
using Models;
using Models.ColdStorage;
using System.Text.Json;

namespace Web.Services;

/// <summary>
/// Persisted submission payload for an async migrate. Serialized onto
/// <see cref="MigrationJob.SubmissionRequestJson"/> by the controller and read back
/// by <see cref="MigrationExpander"/> so folder expansion + item creation + enqueue
/// happen off the HTTP request thread.
/// </summary>
public sealed class MigrationSubmission
{
    public List<MigrationSubmissionItem> Items { get; set; } = [];
    public bool Recursive { get; set; }
    public bool CopyMetadataColumns { get; set; }
    public int Priority { get; set; }
}

public sealed class MigrationSubmissionItem
{
    public string ServerRelativeUrl { get; set; } = string.Empty;
    public ColdStorageItemKind ItemKind { get; set; }
    public long FileSize { get; set; }
    public DateTime? LastModified { get; set; }
}

/// <summary>
/// Persisted submission payload for an async bulk <b>restore</b> (issue #9 + the submit-timeout
/// fix). Serialized onto <see cref="MigrationJob.SubmissionRequestJson"/> by
/// <c>RestoresController.StartBatchAsync</c> and read back by <see cref="MigrationExpander"/> so the
/// folder→placeholder resolution + per-item creation + enqueue happen off the HTTP request thread
/// (a large-folder restore previously blocked the request until it timed out with "Failed to fetch").
/// The container ACL is evaluated on the request thread and captured as
/// <see cref="AllowedContainerIds"/>, so the background expansion needs no <c>ClaimsPrincipal</c>.
/// </summary>
public sealed class RestoreSubmission
{
    public List<string> Placeholders { get; set; } = [];
    public List<string> FolderServerRelativeUrls { get; set; } = [];
    /// <summary>Container IDs the caller was authorised to restore from, evaluated at submit time.</summary>
    public List<int> AllowedContainerIds { get; set; } = [];
}

public interface IMigrationExpander
{
    /// <summary>
    /// Expands a job's persisted submission into per-file items and enqueues them.
    /// Idempotent: a no-op once the job's expansion has already completed, and safe to
    /// re-run (the per-URL idempotency guard coalesces items created by a prior pass).
    /// </summary>
    Task ExpandAsync(Guid jobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs the (potentially minutes-long) folder expansion + item creation + enqueue for
/// a migrate job in the background, so <c>POST /api/migrations/start</c> can return
/// immediately for large-folder submissions instead of blocking the request until every
/// file is enumerated and inserted.
/// </summary>
public sealed class MigrationExpander(
    SPOColdStorageDbContext db,
    Config config,
    ILogger<MigrationExpander> logger,
    IArchiveEligibilityEvaluator eligibility,
    ISharePointFolderExpansionService folderExpansion,
    IColdStorageBusPublisher publisher,
    IColdStorageBlobEnumerator blobEnumerator) : IMigrationExpander
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger<MigrationExpander> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IArchiveEligibilityEvaluator _eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility));
    private readonly ISharePointFolderExpansionService _folderExpansion = folderExpansion ?? throw new ArgumentNullException(nameof(folderExpansion));
    private readonly IColdStorageBusPublisher _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly IColdStorageBlobEnumerator _blobEnumerator = blobEnumerator ?? throw new ArgumentNullException(nameof(blobEnumerator));

    public async Task ExpandAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _db.MigrationJobs.FirstOrDefaultAsync(j => j.JobId == jobId, cancellationToken).ConfigureAwait(false);
        if (job is null || job.ExpansionCompletedAt is not null || string.IsNullOrEmpty(job.SubmissionRequestJson))
        {
            return; // nothing to do / already expanded
        }

        if (job.Operation == MigrationOperationKind.Restore)
        {
            await ExpandRestoreAsync(job, cancellationToken).ConfigureAwait(false);
            return;
        }

        MigrationSubmission? submission = null;
        try
        {
            submission = JsonSerializer.Deserialize<MigrationSubmission>(job.SubmissionRequestJson);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Job {JobId}: submission request could not be parsed.", jobId);
        }
        if (submission is null)
        {
            await FinishEmptyAsync(job, "The submission request could not be read; nothing was queued.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var container = await _db.ColdStorageContainers.FirstOrDefaultAsync(c => c.ID == job.ContainerId, cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            await FinishEmptyAsync(job, "The target cold-storage container no longer exists.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var upn = job.RequestedByUpn;
        var siteUrl = job.SiteUrl;
        var webUrl = job.WebUrl;

        var warnings = new List<string>();
        var alreadyInFlight = 0;
        var notEligible = 0;
        var emptyPaths = 0;
        var notEligibleSamples = new List<string>();
        var queueWork = new List<ColdStorageBusEnvelope>();

        // Expand selected folders into their files (bounded by the per-request cap).
        var requestFileCap = _config.ColdStorageMaxFilesPerRequest > 0 ? _config.ColdStorageMaxFilesPerRequest : 5000;
        var expandedItems = new List<MigrationSubmissionItem>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in submission.Items)
        {
            if (string.IsNullOrWhiteSpace(dto.ServerRelativeUrl)) { emptyPaths++; continue; }
            if (expandedItems.Count >= requestFileCap) { break; }

            if (dto.ItemKind == ColdStorageItemKind.Folder)
            {
                var expansion = await _folderExpansion
                    .ExpandAsync(siteUrl, dto.ServerRelativeUrl, submission.Recursive, requestFileCap - expandedItems.Count, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrEmpty(expansion.Warning)) { warnings.Add(expansion.Warning); }
                foreach (var f in expansion.Files)
                {
                    if (seenUrls.Add(f.ServerRelativeUrl))
                    {
                        expandedItems.Add(new MigrationSubmissionItem
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
            if (string.IsNullOrWhiteSpace(dto.ServerRelativeUrl)) { emptyPaths++; continue; }

            // Idempotency + self-heal of orphaned Queued rows (see MigrationsController).
            var existing = await _db.MigrationJobItems
                .Where(i => i.SpServerRelativeUrl == dto.ServerRelativeUrl && i.ContainerId == container.ID)
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
                }
                else
                {
                    alreadyInFlight++;
                    continue;
                }
            }

            var eligibilityResult = await _eligibility.EvaluateAsync(new ArchiveCandidate
            {
                ServerRelativeUrl = dto.ServerRelativeUrl,
                SiteUrl = siteUrl,
                WebUrl = webUrl,
                FileSizeBytes = dto.FileSize,
                ItemKind = dto.ItemKind,
                LastModified = dto.LastModified,
            }, cancellationToken).ConfigureAwait(false);
            if (!eligibilityResult.IsEligible)
            {
                notEligible++;
                if (notEligibleSamples.Count < 3)
                {
                    notEligibleSamples.Add($"'{dto.ServerRelativeUrl}': {eligibilityResult.SkipReason}");
                }
                continue;
            }

            var item = new MigrationJobItem
            {
                ItemId = Guid.NewGuid(),
                JobId = job.JobId,
                ItemKind = dto.ItemKind,
                Recursive = submission.Recursive,
                CopyMetadataColumns = submission.CopyMetadataColumns,
                SpSiteUrl = siteUrl,
                SpWebUrl = webUrl,
                SpServerRelativeUrl = dto.ServerRelativeUrl,
                FileSize = dto.FileSize,
                SourceLastModified = dto.LastModified,
                ContainerId = container.ID,
                BlobContainerName = container.BlobContainerName,
                Priority = submission.Priority,
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
                Recursive = submission.Recursive,
                CopyMetadataColumns = submission.CopyMetadataColumns,
                File = new BaseSharePointFileInfo
                {
                    SiteUrl = siteUrl,
                    WebUrl = string.IsNullOrEmpty(webUrl) ? siteUrl : webUrl,
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

        if (emptyPaths > 0) { warnings.Add($"{emptyPaths} item(s) skipped: empty path."); }
        if (alreadyInFlight > 0) { warnings.Add($"{alreadyInFlight} item(s) already queued or in progress; skipped to avoid duplicate work."); }
        if (notEligible > 0)
        {
            warnings.Add($"{notEligible} item(s) skipped as not eligible for archiving.");
            warnings.AddRange(notEligibleSamples.Select(s => "  • " + s));
        }

        // Persist warnings as job-level log rows so they survive to GET /api/jobs/{id}.
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

        if (queueWork.Count == 0)
        {
            job.Status = MigrationLifecycleStatus.CompletedWithWarning;
            var summary = warnings.Count > 0 ? string.Join(" | ", warnings) : "No eligible items.";
            job.Summary = summary.Length > 1000 ? summary[..1000] : summary;
            job.CompletedAt = DateTime.UtcNow;
        }

        job.ExpansionCompletedAt = DateTime.UtcNow;
        job.SubmissionRequestJson = null; // done — don't re-drive on restart
        job.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (queueWork.Count == 0)
        {
            _logger.LogInformation("Job {JobId} expanded to no queueable items: {Warnings}", job.JobId, string.Join(" | ", warnings));
            return;
        }

        // Publish decoupled from any request lifetime, batched. Anything not sent here
        // is re-driven by the worker's dispatch reconciler (items stay Queued).
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
            _logger.LogError(ex, "Batch publish for job {JobId} failed; {Count} item(s) will be re-driven by the dispatch reconciler.", job.JobId, queueWork.Count);
        }

        _logger.LogInformation("Job {JobId} expanded and enqueued {Count} item(s).", job.JobId, queueWork.Count);
    }

    /// <summary>
    /// Restore counterpart of the migrate expansion: resolves the selected folders into their
    /// archived placeholders, creates a restore item + bus envelope for each (honouring the
    /// container ACL captured at submit time and an in-flight guard), and batch-publishes them —
    /// all off the HTTP request thread so a large-folder restore returns immediately instead of
    /// timing out. Idempotent + re-drivable like the migrate path.
    /// </summary>
    private async Task ExpandRestoreAsync(MigrationJob job, CancellationToken cancellationToken)
    {
        RestoreSubmission? submission = null;
        try
        {
            submission = JsonSerializer.Deserialize<RestoreSubmission>(job.SubmissionRequestJson!);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Restore job {JobId}: submission request could not be parsed.", job.JobId);
        }
        if (submission is null)
        {
            await FinishEmptyAsync(job, "The restore submission request could not be read; nothing was queued.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var upn = job.RequestedByUpn;
        var siteUrl = job.SiteUrl;
        var webUrl = job.WebUrl;
        var allowedContainerIds = submission.AllowedContainerIds.ToHashSet();

        // Blob-driven restore: cold storage is the source of truth. We enumerate the archived blobs
        // under the selected scope (and resolve explicit placeholders to their blob) and restore each
        // straight from its blob — so an archive is restorable even when its SharePoint placeholder or
        // its migration_job_items row is missing. The database is treated as an audit log, not the
        // authority for "what should be restored".
        var containers = await _db.ColdStorageContainers
            .Where(c => allowedContainerIds.Contains(c.ID))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (containers.Count == 0)
        {
            await FinishEmptyAsync(job, "You don't have restore permission on any cold-storage container; nothing was queued.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var cap = _config.ColdStorageMaxFilesPerRequest > 0 ? _config.ColdStorageMaxFilesPerRequest : 5000;
        var warnings = new List<string>();
        var queueWork = new List<ColdStorageBusEnvelope>();
        var skipped = 0;
        var skipSamples = new List<string>();
        var seenBlobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hitCap = false;
        void Skip(string reason)
        {
            skipped++;
            if (skipSamples.Count < 5) { skipSamples.Add(reason); }
        }

        // Creates a queued restore item + blob-driven envelope for one archived blob. Returns false
        // when the per-request cap is reached (the caller stops enumerating).
        async Task<bool> ConsiderBlobAsync(ColdStorageContainer container, ArchivedBlob blob)
        {
            if (queueWork.Count >= cap)
            {
                return false;
            }
            if (!seenBlobs.Add(container.BlobContainerName + "|" + blob.BlobPath))
            {
                return true; // already queued in this pass
            }

            var destination = blob.OriginalServerRelativeUrl;
            var site = string.IsNullOrEmpty(blob.OriginalSiteUrl) ? siteUrl : blob.OriginalSiteUrl;
            var web = string.IsNullOrEmpty(blob.OriginalWebUrl)
                ? (string.IsNullOrEmpty(webUrl) ? site : webUrl)
                : blob.OriginalWebUrl;
            var placeholder = destination + ".url";

            // Idempotency: skip if a restore for this destination is already in flight (non-terminal).
            var inFlight = await _db.MigrationJobItems
                .Where(i => i.SpServerRelativeUrl == destination && i.Job.Operation == MigrationOperationKind.Restore)
                .OrderByDescending(i => i.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (inFlight is not null && !inFlight.Status.IsTerminal())
            {
                Skip($"'{destination}': restore already in progress (status {inFlight.Status}).");
                return true;
            }

            var item = new MigrationJobItem
            {
                ItemId = Guid.NewGuid(),
                JobId = job.JobId,
                SpSiteUrl = site,
                SpWebUrl = web,
                SpServerRelativeUrl = destination,
                PlaceholderServerRelativeUrl = placeholder,
                ContainerId = container.ID,
                BlobContainerName = container.BlobContainerName,
                BlobPath = blob.BlobPath,
                FileSize = blob.Length,
                Status = MigrationLifecycleStatus.Queued,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            await _db.MigrationJobItems.AddAsync(item, cancellationToken).ConfigureAwait(false);
            await _db.MigrationJobLogs.AddAsync(new MigrationJobLog
            {
                JobId = job.JobId,
                ItemId = item.ItemId,
                Status = MigrationLifecycleStatus.Queued,
                Level = (int)LogLevel.Information,
                Message = $"Queued for restore from cold storage '{container.Name}' ({container.BlobContainerName}/{blob.BlobPath}).",
                ActorUpn = upn,
                Action = "Restore",
            }, cancellationToken).ConfigureAwait(false);

            queueWork.Add(new ColdStorageBusEnvelope
            {
                JobId = job.JobId,
                ItemId = item.ItemId,
                Operation = MigrationOperationKind.Restore,
                ContainerName = container.BlobContainerName,
                RequestedByUpn = upn,
                ConflictBehavior = job.ConflictBehavior,
                RestoreTarget = new PlaceholderRestoreTarget
                {
                    SiteUrl = site,
                    WebUrl = web,
                    PlaceholderServerRelativeUrl = placeholder,
                    OriginalServerRelativeUrl = destination,
                    BlobPath = blob.BlobPath,
                },
            });
            return queueWork.Count < cap;
        }

        // (1) Folders -> enumerate every archived blob under the folder's blob prefix in each container.
        foreach (var folder in submission.FolderServerRelativeUrls.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            if (hitCap) { break; }
            var prefix = ColdStorageBlobKey.Build(siteUrl, folder.TrimEnd('/')) + "/";
            foreach (var container in containers)
            {
                if (hitCap) { break; }
                await foreach (var blob in _blobEnumerator.EnumerateAsync(container, prefix, cancellationToken).ConfigureAwait(false))
                {
                    if (!await ConsiderBlobAsync(container, blob).ConfigureAwait(false))
                    {
                        hitCap = true;
                        break;
                    }
                }
            }
        }

        // (2) Explicit placeholders -> resolve each to its archived blob and restore it.
        foreach (var ph in submission.Placeholders.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (hitCap) { break; }
            var destination = ph.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ? ph[..^4] : ph;
            var blobKey = ColdStorageBlobKey.Build(siteUrl, destination);
            ArchivedBlob? resolved = null;
            ColdStorageContainer? resolvedContainer = null;
            foreach (var container in containers)
            {
                resolved = await _blobEnumerator.GetAsync(container, blobKey, cancellationToken).ConfigureAwait(false);
                if (resolved is not null) { resolvedContainer = container; break; }
            }
            if (resolved is null)
            {
                Skip($"'{ph}': no archived blob found in cold storage.");
                continue;
            }
            if (!await ConsiderBlobAsync(resolvedContainer!, resolved).ConfigureAwait(false))
            {
                hitCap = true;
            }
        }

        if (hitCap)
        {
            warnings.Add($"This restore hit the {cap:N0}-item limit; not all files were queued. Restore the remaining subfolders separately, or raise ColdStorageMaxFilesPerRequest.");
        }

        if (skipped > 0)
        {
            warnings.Add($"{skipped} item(s) skipped.");
            warnings.AddRange(skipSamples.Select(s => "  • " + s));
        }
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

        if (queueWork.Count == 0)
        {
            job.Status = MigrationLifecycleStatus.CompletedWithWarning;
            var summary = warnings.Count > 0 ? string.Join(" | ", warnings) : "No restorable items.";
            job.Summary = summary.Length > 1000 ? summary[..1000] : summary;
            job.CompletedAt = DateTime.UtcNow;
        }

        job.ExpansionCompletedAt = DateTime.UtcNow;
        job.SubmissionRequestJson = null;
        job.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (queueWork.Count == 0)
        {
            _logger.LogInformation("Restore job {JobId} expanded to no queueable items.", job.JobId);
            return;
        }

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
            _logger.LogError(ex, "Batch publish for restore job {JobId} failed; {Count} item(s) will be re-driven by the dispatch reconciler.", job.JobId, queueWork.Count);
        }

        _logger.LogInformation("Restore job {JobId} expanded and enqueued {Count} item(s).", job.JobId, queueWork.Count);
    }

    private async Task FinishEmptyAsync(MigrationJob job, string message, CancellationToken cancellationToken)
    {
        job.Status = MigrationLifecycleStatus.CompletedWithWarning;
        job.Summary = message;
        job.CompletedAt = DateTime.UtcNow;
        job.ExpansionCompletedAt = DateTime.UtcNow;
        job.SubmissionRequestJson = null;
        job.UpdatedAt = DateTime.UtcNow;
        _db.MigrationJobLogs.Add(new MigrationJobLog
        {
            JobId = job.JobId,
            Status = MigrationLifecycleStatus.CompletedWithWarning,
            Level = (int)LogLevel.Warning,
            Message = message,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}