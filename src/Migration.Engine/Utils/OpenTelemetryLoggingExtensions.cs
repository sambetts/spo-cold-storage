using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace Migration.Engine.Utils;

/// <summary>
/// Bridges the Microsoft.Extensions.Logging pipeline to OpenTelemetry with the
/// Azure Monitor exporter, so logs flow into Application Insights.
/// </summary>
public static class OpenTelemetryLoggingExtensions
{
    /// <summary>
    /// Adds an OpenTelemetry log provider that exports to Azure Monitor / Application Insights.
    /// </summary>
    /// <param name="builder">Logging builder to extend.</param>
    /// <param name="connectionStringOrKey">Either an Application Insights connection string
    /// (preferred) or a raw instrumentation key (for backwards compatibility).</param>
    /// <param name="roleName">Cloud role name reported with telemetry.</param>
    public static ILoggingBuilder AddOpenTelemetryAzureMonitor(
        this ILoggingBuilder builder,
        string connectionStringOrKey,
        string roleName)
    {
        if (string.IsNullOrWhiteSpace(connectionStringOrKey))
        {
            return builder;
        }

        var connectionString = connectionStringOrKey.Contains('=', StringComparison.Ordinal)
            ? connectionStringOrKey
            : $"InstrumentationKey={connectionStringOrKey}";

        builder.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(roleName));
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.AddAzureMonitorLogExporter(o => o.ConnectionString = connectionString);
        });

        return builder;
    }
}
