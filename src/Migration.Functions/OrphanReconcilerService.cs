using Entities.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Migration.Engine.Reconciliation;
using Migration.Engine.Settings;

namespace Migration.Functions;

/// <summary>
/// Periodic orphan reconciliation running inside the Function host (issue #21).
///
/// <para>
/// A cold-storage blob is "orphaned" when the SharePoint side that pointed at it is
/// gone — the <c>.url</c> placeholder was deleted, or the whole site was. Nothing else
/// references those blobs, so without a sweep they are billed forever and never
/// surface anywhere. <see cref="ColdStorageReconciler"/> already detects and handles
/// them per <see cref="Config.ColdStorageOrphanPolicy"/>, but until now it only ran
/// when an admin manually called <c>POST /api/admin/reconcile</c> —
/// <see cref="Config.ColdStorageReconcileIntervalHours"/> was declared and read by
/// nothing.
/// </para>
///
/// <para>
/// Disabled by default (interval 0), matching the documented behaviour, so enabling it
/// is a deliberate operator choice. The pass is idempotent and bounded (the reconciler
/// caps items per run), so it is safe if more than one always-ready instance runs it.
/// </para>
///
/// <para>
/// Note the policy still governs what actually happens: the default <c>report</c> only
/// audits. <c>delete</c> is permanent — the SharePoint source was already removed at
/// archive time, so a deleted blob is unrecoverable.
/// </para>
/// </summary>
public sealed class OrphanReconcilerService(Config config, ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
        .CreateLogger("OrphanReconciler");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = new DbColdStorageSettingsSource(_config, _logger);
        var intervalHours = await settings.GetIntAsync(
            ColdStorageSettingKeys.ReconcileIntervalHours, _config.ColdStorageReconcileIntervalHours, stoppingToken).ConfigureAwait(false);
        if (intervalHours <= 0)
        {
            _logger.LogInformation(
                "Orphan reconciler disabled (ColdStorageReconcileIntervalHours <= 0). Reconciliation can still be run on demand via POST /api/admin/reconcile.");
            return;
        }

        var policy = ColdStorageReconciler.ParsePolicy(await settings.GetStringAsync(
            ColdStorageSettingKeys.OrphanPolicy, _config.ColdStorageOrphanPolicy, stoppingToken).ConfigureAwait(false));
        _logger.LogInformation("Orphan reconciler starting; interval {Hours}h, policy {Policy}.", intervalHours, policy);

        // Stagger the first pass so two always-ready instances don't sweep in lockstep,
        // and so a cold start isn't competing with the queue drain.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(30, 120)), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var reconciler = new ColdStorageReconciler(_config, _logger);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));
        try
        {
            // Run once at startup so enabling the setting takes effect without waiting
            // out a full interval (which can be a day or more).
            do
            {
                try
                {
                    var summary = await reconciler.RunAsync(stoppingToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Orphan reconciler pass ({Policy}): checked={Checked} orphans={Orphans} quarantined={Quarantined} blobsDeleted={BlobsDeleted} errors={Errors}.",
                        policy, summary.Checked, summary.Orphans, summary.Quarantined, summary.BlobsDeleted, summary.Errors);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Orphan reconciler pass failed; continuing.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
