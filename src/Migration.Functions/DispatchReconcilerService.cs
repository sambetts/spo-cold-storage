using Entities.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Migration.Engine.Lifecycle;
using Migration.Engine.Reconciliation;

namespace Migration.Functions;

/// <summary>
/// Periodic dispatch reconciler running inside the Function host — the safety net
/// that stops a migration ever silently freezing. Every
/// <see cref="Config.ColdStorageDispatchIntervalSeconds"/> it re-publishes Queued
/// items whose bus message was never sent (e.g. a start request cancelled
/// mid-publish left the row persisted but no message on the queue), fails items
/// stuck by a crashed/stalled worker, and closes jobs that never produced items.
///
/// With an always-ready instance this loop runs continuously; the work is
/// idempotent (re-drives are coalesced by the processor's in-flight and DB status
/// guards) so it is safe even if more than one instance runs it.
/// </summary>
public sealed class DispatchReconcilerService(Config config, ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
        .CreateLogger("DispatchReconciler");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _config.ColdStorageDispatchIntervalSeconds;
        if (intervalSeconds <= 0)
        {
            _logger.LogInformation("Dispatch reconciler disabled (ColdStorageDispatchIntervalSeconds <= 0).");
            return;
        }

        _logger.LogInformation("Dispatch reconciler starting; interval {Seconds}s.", intervalSeconds);

        // Small random start delay so two always-ready instances don't tick in
        // lockstep (duplicate re-drives are coalesced, but this trims churn).
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(1, 10)), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await using var publisher = new ColdStorageQueuePublisher(_config);
        var reconciler = new MigrationDispatchReconciler(_config, _logger, publisher);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var summary = await reconciler.RunAsync(stoppingToken).ConfigureAwait(false);
                    // Log EVERY pass (even no-work) at Information so the reconciler's liveness is
                    // visible in App Insights — its silence is exactly what hid the earlier freeze.
                    // A pass is cheap and runs on the dispatch interval, so this is low volume.
                    _logger.LogInformation(
                        "ColdStorage reconciler pass: hasWork={HasWork} reDriven={ReDriven} throttleRetried={ThrottleRetried} failedGaveUp={FailedGaveUp} failedStalled={FailedStalled} emptyJobsClosed={EmptyJobsClosed} jobsFinalized={JobsFinalized}.",
                        summary.HasWork, summary.ReDriven, summary.ThrottleRetried, summary.FailedGaveUp, summary.FailedStalled, summary.EmptyJobsClosed, summary.JobsFinalized);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Dispatch reconciler pass failed; continuing.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
