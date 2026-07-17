using Entities.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Migration.Engine;
using Migration.Functions;

// Isolated-worker Azure Functions host for the cold-storage queue trigger.
// This replaces the continuous-WebJob worker's dependency on Always On: the
// Functions scale controller wakes this function when a message lands on the
// 'filediscovery' queue, so items no longer sit in "Queued" when the app idles.
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        // Reuse the exact same Config binding the WebJob worker uses. Every value
        // (ConnectionStrings__*, AzureAd__*, BlobContainerName, …) comes from the
        // Function App's application settings.
        var config = new Config(context.Configuration);
        services.AddSingleton(config);

        // Shared, transport-agnostic dispatch core — identical behaviour to the
        // WebJob listener (envelope + legacy fallback, per-host in-flight guards,
        // dead-letter of unparseable messages).
        services.AddSingleton(sp => new ColdStorageMessageProcessor(
            sp.GetRequiredService<Config>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("ColdStorage")));

        // Emit the worker heartbeat so the health API / SPFx UI shows this
        // queue-triggered worker as online (the WebJob is being retired).
        services.AddHostedService<HeartbeatBackgroundService>();

        // Safety net: re-drive Queued items whose bus message was never sent and
        // fail items stuck by a crashed worker, so a migration can never silently
        // freeze (see DispatchReconcilerService / MigrationDispatchReconciler).
        services.AddHostedService<DispatchReconcilerService>();
    })
    .Build();

host.Run();
