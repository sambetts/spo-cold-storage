using System.Net;

namespace Migration.Engine.Lifecycle;

/// <summary>
/// Extracts a server-provided <c>Retry-After</c> hint (in seconds) from a throttling
/// exception chain so the pipeline can honour exactly how long SharePoint/Azure asked us
/// to wait instead of guessing with a generic backoff.
///
/// SharePoint CSOM surfaces a throttle as a <see cref="WebException"/> whose
/// <see cref="HttpWebResponse"/> carries the <c>Retry-After</c> header; the throttle-retry
/// helper stashes the last value it saw into <see cref="Exception.Data"/> under the
/// <see cref="RetryAfterDataKey"/> so it survives the give-up rethrow; and HttpClient-based
/// callers may attach it the same way. We walk the whole inner-exception chain and return
/// the first non-negative value found.
/// </summary>
public static class ThrottleInfo
{
    /// <summary>Key used to carry a Retry-After hint on <see cref="Exception.Data"/>.</summary>
    public const string RetryAfterDataKey = "ColdStorage.RetryAfterSeconds";

    /// <summary>
    /// Returns the server-requested wait in seconds if the throttle carried a
    /// <c>Retry-After</c> hint, otherwise null.
    /// </summary>
    public static int? TryGetRetryAfterSeconds(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex.Data.Contains(RetryAfterDataKey)
                && TryParseSeconds(ex.Data[RetryAfterDataKey]?.ToString(), out var stashed))
            {
                return stashed;
            }

            if (ex is WebException { Response: HttpWebResponse response }
                && TryParseSeconds(response.Headers["Retry-After"], out var fromResponse))
            {
                return fromResponse;
            }
        }
        return null;
    }

    /// <summary>
    /// Parses an HTTP <c>Retry-After</c> value. Honours both the delta-seconds form
    /// (e.g. <c>"120"</c>) and the HTTP-date form (converted to a delta from now).
    /// </summary>
    public static bool TryParseSeconds(string? headerValue, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return false;
        }
        if (int.TryParse(headerValue, out seconds) && seconds >= 0)
        {
            return true;
        }
        if (DateTimeOffset.TryParse(headerValue, out var when))
        {
            seconds = Math.Max(0, (int)Math.Ceiling((when - DateTimeOffset.UtcNow).TotalSeconds));
            return true;
        }
        seconds = 0;
        return false;
    }
}
