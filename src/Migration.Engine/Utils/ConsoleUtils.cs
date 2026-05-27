using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Entities.Configuration;

namespace Migration.Engine.Utils;

public class ConsoleUtils
{
    public static Config GetConfigurationWithDefaultBuilder<T>() where T : class
    {
        var builder = GetConfigurationBuilder<T>();

        var configCollection = builder.Build();
        return new Config(configCollection);
    }

    public static IConfigurationBuilder GetConfigurationBuilder<T>() where T : class
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddUserSecrets<T>()
            .AddEnvironmentVariables()
            .AddJsonFile("appsettings.json", true);
    }

    public static void PrintCommonStartupDetails()
    {
        var assembly = System.Reflection.Assembly.GetEntryAssembly();
        Console.WriteLine($"Start-up: '{assembly?.FullName}'.");
    }

    /// <summary>
    /// Creates an <see cref="ILoggerFactory"/> wired up with console logging and (when
    /// available) Azure Monitor / Application Insights via OpenTelemetry. Use the
    /// returned factory to obtain <see cref="ILogger{T}"/> instances for components.
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory(Config config, string roleName)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = false;
                options.TimestampFormat = "HH:mm:ss ";
                options.IncludeScopes = true;
            });
            builder.SetMinimumLevel(LogLevel.Information);

            if (config.HaveAppInsightsConfigured)
            {
                builder.AddOpenTelemetryAzureMonitor(config.AppInsightsInstrumentationKey, roleName);
            }
        });
    }
}
