using Entities;
using Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Migration.Engine.Lifecycle;
using Migration.Engine.Utils;
using Microsoft.SharePoint.Client;
using Models.ColdStorage;

namespace Migration.Engine.Reconciliation;

/// <summary>
/// Policy for what to do with a cold-storage blob whose placeholder/site no
/// longer exists.
/// </summary>
public enum OrphanPolicy
{
    /// <summary>Audit only — never touch the blob (default, safest).</summary>
    Report,
    /// <summary>Tag + flag the blob, keep it for a human to review.</summary>
    Quarantine,
    /// <summary>Delete the blob — permanent (the source was deleted at migration).</summary>
    Delete,
}

public sealed record ReconcileSummary(int Checked, int Orphans, int BlobsDeleted, int Quarantined, int Errors);

/// <summary>
/// Reconciles cold-storage blobs against their SharePoint placeholders (issue
/// #3). For each completed migration item it checks whether the .url placeholder
/// (and its site) still exist; when they don't, the item's blob is orphaned —
/// SharePoint no longer references it and we'd keep paying for it forever — so
/// it is handled per <see cref="OrphanPolicy"/> and audited.
///
/// Coverage is round-robin via <c>last_reconciled_at</c> and bounded per run, so
/// a large library is reconciled over several scheduled passes without long
/// single runs.
/// </summary>
public sealed class ColdStorageReconciler : BaseComponent
{
    private const int MaxItemsPerRun = 500;

    public ColdStorageReconciler(Config config, ILogger logger) : base(config, logger)
    {
    }

    public static OrphanPolicy ParsePolicy(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "delete" => OrphanPolicy.Delete,
        "quarantine" => OrphanPolicy.Quarantine,
        _ => OrphanPolicy.Report,
    };

    public async Task<ReconcileSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        var policy = ParsePolicy(_config.ColdStorageOrphanPolicy);
        using var db = new SPOColdStorageDbContext(_config);
        var writer = new JobStatusWriter(db, _logger);

        var items = await db.MigrationJobItems
            .Where(i => i.Status == MigrationLifecycleStatus.ColdStorageMigrationCompleted
                        && i.BlobPath != null
                        && i.BlobContainerName != null
                        && i.PlaceholderServerRelativeUrl != null
                        && i.OrphanDetectedAt == null)
            .OrderBy(i => i.LastReconciledAt == null ? DateTime.MinValue : i.LastReconciledAt)
            .Take(MaxItemsPerRun)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int checkedCount = 0, orphans = 0, deleted = 0, quarantined = 0, errors = 0;
        var now = DateTime.UtcNow;

        foreach (var group in items.GroupBy(i => i.SpSiteUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();

            ClientContext? ctx = null;
            var siteReachable = true;
            try
            {
                ctx = await AuthUtils.GetClientContext(_config, group.Key, _logger, null).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                siteReachable = false;
                _logger.LogWarning(ex, "Reconcile: site '{Site}' unreachable; treating its items as orphaned.", group.Key);
            }

            try
            {
                foreach (var item in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    checkedCount++;
                    item.LastReconciledAt = now;
                    try
                    {
                        var placeholderExists = siteReachable
                            && await PlaceholderExistsAsync(ctx!, item.PlaceholderServerRelativeUrl!, cancellationToken).ConfigureAwait(false);
                        if (placeholderExists)
                        {
                            continue;
                        }

                        orphans++;
                        var outcome = await HandleOrphanAsync(writer, item, policy, siteReachable, cancellationToken).ConfigureAwait(false);
                        if (outcome == OrphanPolicy.Delete)
                        {
                            deleted++;
                        }
                        else if (outcome == OrphanPolicy.Quarantine)
                        {
                            quarantined++;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        errors++;
                        _logger.LogError(ex, "Reconcile failed for item {ItemId}.", item.ItemId);
                    }
                }
            }
            finally
            {
                ctx?.Dispose();
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var summary = new ReconcileSummary(checkedCount, orphans, deleted, quarantined, errors);
        _logger.LogInformation(
            "Reconcile complete: checked={Checked}, orphans={Orphans}, deleted={Deleted}, quarantined={Quarantined}, errors={Errors}.",
            summary.Checked, summary.Orphans, summary.BlobsDeleted, summary.Quarantined, summary.Errors);
        return summary;
    }

    private static async Task<bool> PlaceholderExistsAsync(ClientContext ctx, string serverRelativeUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = ctx.Web.GetFileByServerRelativeUrl(serverRelativeUrl);
        ctx.Load(file, f => f.Exists);
        try
        {
            await ctx.ExecuteQueryAsync().ConfigureAwait(false);
            return file.Exists;
        }
        catch (ServerException)
        {
            // Folder/library/path no longer exists -> placeholder is gone.
            return false;
        }
    }

    private async Task<OrphanPolicy> HandleOrphanAsync(
        JobStatusWriter writer,
        Entities.DBEntities.ColdStorage.MigrationJobItem item,
        OrphanPolicy policy,
        bool siteReachable,
        CancellationToken cancellationToken)
    {
        var reason = siteReachable
            ? $"placeholder '{item.PlaceholderServerRelativeUrl}' no longer exists"
            : $"site '{item.SpSiteUrl}' is unreachable/deleted";
        item.OrphanDetectedAt = DateTime.UtcNow;
        item.UpdatedAt = DateTime.UtcNow;

        switch (policy)
        {
            case OrphanPolicy.Delete:
                await DeleteBlobAsync(item.BlobContainerName!, item.BlobPath!, cancellationToken).ConfigureAwait(false);
                await writer.TransitionAsync(item.ItemId, MigrationLifecycleStatus.Cancelled,
                    $"Orphan ({reason}); cold-storage blob deleted per reconciliation policy.",
                    level: LogLevel.Warning, cancellationToken: cancellationToken).ConfigureAwait(false);
                return OrphanPolicy.Delete;

            case OrphanPolicy.Quarantine:
                await TryTagBlobQuarantineAsync(item.BlobContainerName!, item.BlobPath!, cancellationToken).ConfigureAwait(false);
                await writer.LogAsync(item.JobId, item.ItemId, item.Status, LogLevel.Warning,
                    $"Orphan ({reason}); quarantined — blob retained for review.", null, cancellationToken).ConfigureAwait(false);
                return OrphanPolicy.Quarantine;

            default:
                await writer.LogAsync(item.JobId, item.ItemId, item.Status, LogLevel.Warning,
                    $"Orphan detected ({reason}); reported only (policy = report).", null, cancellationToken).ConfigureAwait(false);
                return OrphanPolicy.Report;
        }
    }

    private async Task DeleteBlobAsync(string containerName, string blobPath, CancellationToken cancellationToken)
    {
        var serviceClient = BlobServiceClientFactory.Create(_config.ConnectionStrings.Storage, _config);
        var blob = serviceClient.GetBlobContainerClient(containerName).GetBlobClient(blobPath);
        await blob.DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task TryTagBlobQuarantineAsync(string containerName, string blobPath, CancellationToken cancellationToken)
    {
        try
        {
            var serviceClient = BlobServiceClientFactory.Create(_config.ConnectionStrings.Storage, _config);
            var blob = serviceClient.GetBlobContainerClient(containerName).GetBlobClient(blobPath);
            var props = await blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var metadata = new Dictionary<string, string>(props.Value.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                [BlobMetadataKeys.OrphanQuarantinedUtc] = DateTime.UtcNow.ToString("O"),
            };
            await blob.SetMetadataAsync(metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reconcile: could not tag blob '{Container}/{Path}' as quarantined.", containerName, blobPath);
        }
    }
}
