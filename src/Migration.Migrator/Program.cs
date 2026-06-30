using Microsoft.Extensions.Logging;
using Entities.Configuration;
using Migration.Engine;
using Migration.Engine.Reconciliation;
using Migration.Engine.Utils;

Console.WriteLine("SPO Cold Storage - Migrator Listener");
Console.WriteLine("This app will listen for messages from service-bus and handle them when they arrive, untill you close this application.");

var config = ConsoleUtils.GetConfigurationWithDefaultBuilder<Program>();
ConsoleUtils.PrintCommonStartupDetails();

using var loggerFactory = ConsoleUtils.CreateLoggerFactory(config, "Migrator");
var logger = loggerFactory.CreateLogger<ColdStorageBusListener>();
var reconcileLogger = loggerFactory.CreateLogger<ColdStorageReconciler>();

var listener = new ColdStorageBusListener(config, logger);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Run the bus listener and the scheduled orphan-reconciliation loop together.
await Task.WhenAll(
    listener.ListenAsync(cts.Token),
    RunReconcileLoopAsync(config, reconcileLogger, cts.Token));

static async Task RunReconcileLoopAsync(Config config, ILogger logger, CancellationToken token)
{
    var hours = config.ColdStorageReconcileIntervalHours;
    if (hours <= 0)
    {
        logger.LogInformation("Orphan reconciliation disabled (ColdStorageReconcileIntervalHours=0).");
        return;
    }

    var interval = TimeSpan.FromHours(hours);
    logger.LogInformation("Orphan reconciliation enabled; running every {Hours}h.", hours);

    // Small initial delay so we don't contend with startup.
    try { await Task.Delay(TimeSpan.FromMinutes(1), token); }
    catch (TaskCanceledException) { return; }

    while (!token.IsCancellationRequested)
    {
        try
        {
            await new ColdStorageReconciler(config, logger).RunAsync(token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Orphan reconciliation pass failed; will retry next interval.");
        }

        try { await Task.Delay(interval, token); }
        catch (TaskCanceledException) { break; }
    }
}

