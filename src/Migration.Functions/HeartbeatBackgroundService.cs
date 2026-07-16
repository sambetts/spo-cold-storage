using Entities.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Migration.Engine.Lifecycle;

namespace Migration.Functions;

/// <summary>
/// Runs the worker heartbeat loop inside the Function host so the web API /
/// SPFx UI reports the queue-triggered worker as online. With an always-ready
/// instance this loop runs continuously; the same best-effort beacon the WebJob
/// writes (<see cref="WorkerHeartbeatService"/>) keeps the health check green so
/// users don't see a false "worker offline" warning after the WebJob cutover.
/// </summary>
public sealed class HeartbeatBackgroundService(Config config, ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var heartbeat = new WorkerHeartbeatService(
            _config,
            _loggerFactory.CreateLogger("WorkerHeartbeat"),
            serviceBusNamespace: "func-flex");
        return heartbeat.RunAsync(stoppingToken);
    }
}
