using Entities.Configuration;
using Microsoft.ApplicationInsights;
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
///
/// Each pass emits a <c>ColdStorage.ReconcilerPass</c> Application Insights custom
/// event (even when it does no work) so the reconciler's liveness + activity are
/// queryable — the exact signal that was missing when a throttled migration froze.
/// </summary>
public sealed class DispatchReconcilerService(Config config, ILoggerFactory loggerFactory, TelemetryClient telemetry) : BackgroundService
{
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
        .CreateLogger("DispatchReconciler");
    private readonly TelemetryClient _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _config.ColdStorageDispatchIntervalSeconds;
        if (intervalSeconds <= 0)
        {
            _logger.LogInformation("Dispatch reconciler disabled (ColdStorageDispatchIntervalSeconds <= 0).");
            return;
        }

        _logger.LogInformation("Dispatch reconciler starting; interval {Seconds}s.", intervalSeconds);
        _telemetry.TrackEvent("ColdStorage.ReconcilerStarted", new Dictionary<string, string>
        {
            ["intervalSeconds"] = intervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

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
                    // Emit every pass (even no-work) so reconciler liveness is queryable in AI.
                    _telemetry.TrackEvent("ColdStorage.ReconcilerPass", new Dictionary<string, string>
                    {
                        ["hasWork"] = summary.HasWork ? "true" : "false",
                        ["reDriven"] = summary.ReDriven.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["throttleRetried"] = summary.ThrottleRetried.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["failedGaveUp"] = summary.FailedGaveUp.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["failedStalled"] = summary.FailedStalled.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["emptyJobsClosed"] = summary.EmptyJobsClosed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["jobsFinalized"] = summary.JobsFinalized.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
                    if (summary.HasWork)
                    {
                        _logger.LogInformation(
                            "Dispatch reconciler: re-drove {ReDriven}, throttle-retried {Throttle}, failed-gave-up {GaveUp}, failed-stalled {Stalled}, closed {EmptyJobs} empty job(s), finalized {Finalized} completed job(s).",
                            summary.ReDriven, summary.ThrottleRetried, summary.FailedGaveUp, summary.FailedStalled, summary.EmptyJobsClosed, summary.JobsFinalized);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _telemetry.TrackException(ex);
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
