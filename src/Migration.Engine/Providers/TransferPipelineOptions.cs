using Entities.Configuration;

namespace Migration.Engine.Providers;

/// <summary>
/// The handful of tunables the transfer pipelines actually need, lifted out of the big
/// <see cref="Config"/> so the pipelines are decoupled from configuration binding and can be unit
/// tested with a one-line literal. Production builds this from <see cref="Config"/> via
/// <see cref="FromConfig"/>; tests construct it directly.
/// </summary>
public sealed record TransferPipelineOptions
{
    /// <summary>Max processing attempts before a transient failure gives up terminally.</summary>
    public int MaxProcessAttempts { get; init; } = 5;

    /// <summary>Exponential-backoff base (attempt 1) in seconds.</summary>
    public int ThrottleBackoffBaseSeconds { get; init; } = 30;

    /// <summary>Exponential-backoff cap in seconds.</summary>
    public int ThrottleBackoffMaxSeconds { get; init; } = 600;

    /// <summary>
    /// Optional public app base URL; when set, the placeholder points at the app's download route
    /// instead of the raw cold-storage URL. Null falls back to the raw URL.
    /// </summary>
    public string? AppBaseUrl { get; init; }

    public static TransferPipelineOptions FromConfig(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new TransferPipelineOptions
        {
            MaxProcessAttempts = config.ColdStorageMaxProcessAttempts > 0 ? config.ColdStorageMaxProcessAttempts : 5,
            ThrottleBackoffBaseSeconds = config.ColdStorageThrottleBackoffBaseSeconds > 0 ? config.ColdStorageThrottleBackoffBaseSeconds : 30,
            ThrottleBackoffMaxSeconds = config.ColdStorageThrottleBackoffMaxSeconds > 0 ? config.ColdStorageThrottleBackoffMaxSeconds : 600,
            AppBaseUrl = config.AppBaseUrl,
        };
    }
}
