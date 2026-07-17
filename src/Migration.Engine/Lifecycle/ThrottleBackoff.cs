using Entities.Configuration;

namespace Migration.Engine.Lifecycle;

/// <summary>
/// Exponential backoff schedule for transient/throttle retries. Attempt 1 waits
/// <c>Base</c> seconds, doubling each subsequent attempt, capped at <c>Max</c>.
/// Shared by the migrate pipeline (to tell the user how long the wait is) and the
/// dispatch reconciler (to decide when a <c>RetryScheduled</c> item is due to be
/// re-driven), so both agree on the schedule.
/// </summary>
public static class ThrottleBackoff
{
    public static int SecondsFor(int attempt, Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var baseSeconds = config.ColdStorageThrottleBackoffBaseSeconds > 0 ? config.ColdStorageThrottleBackoffBaseSeconds : 30;
        var maxSeconds = config.ColdStorageThrottleBackoffMaxSeconds > 0 ? config.ColdStorageThrottleBackoffMaxSeconds : 600;
        return SecondsFor(attempt, baseSeconds, maxSeconds);
    }

    /// <summary>base * 2^(attempt-1), capped at max. attempt is 1-based.</summary>
    public static int SecondsFor(int attempt, int baseSeconds, int maxSeconds)
    {
        if (attempt < 1)
        {
            attempt = 1;
        }
        long seconds = baseSeconds;
        for (var i = 1; i < attempt && seconds < maxSeconds; i++)
        {
            seconds *= 2;
        }
        return (int)Math.Min(seconds, maxSeconds);
    }
}
