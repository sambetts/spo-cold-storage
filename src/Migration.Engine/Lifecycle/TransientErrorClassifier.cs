using System.Net;
using System.Net.Sockets;

namespace Migration.Engine.Lifecycle;

/// <summary>
/// Classifies an exception as a <b>transient</b>, retry-worthy failure — SharePoint/Azure
/// throttling (HTTP 429), a timeout, or a transient gateway/availability error (502/503/504)
/// — versus a permanent one (auth, not-found, integrity mismatch). Transient failures are
/// retried with exponential backoff via the non-terminal
/// <see cref="Models.ColdStorage.MigrationLifecycleStatus.RetryScheduled"/> status and the
/// dispatch reconciler, instead of failing the item terminally on the first hit. That is what
/// makes the "it will be retried automatically" promise true for a throttle.
/// </summary>
public static class TransientErrorClassifier
{
    // Substrings that indicate a transient condition. Deliberately avoids bare 3-digit
    // numeric codes (e.g. "503"/"502"/"504") that could match a byte count or request id
    // in an unrelated message — those HTTP codes are matched typed via HttpRequestException
    // below, and their textual forms ("service unavailable" etc.) are matched here. "429" is
    // kept as a strong, specific throttle signal. Excludes permanent signals (401/403/404).
    private static readonly string[] TransientMarkers =
    [
        "429", "throttl", "too many requests",
        "server is busy", "serverbusy",
        "timeout", "timed out", "the operation has timed out", "operation timed out",
        "operation was canceled", "operation was cancelled", "taskcanceled", "task was canceled",
        "service unavailable", "bad gateway", "gateway timeout",
        "temporarily unavailable", "connection reset", "an existing connection was forcibly closed",
        // Transient SharePoint CSOM hiccup under load: an "I/O error occurred" while reading the
        // response stream of an upload/query. The operation may even have succeeded server-side;
        // either way it should be retried (the migrate/restore pipelines are idempotent on retry —
        // overwriting uploads, skip-if-already-present guards) rather than failing terminally.
        "i/o error",
    ];

    private static readonly HttpStatusCode[] TransientStatusCodes =
    [
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.RequestTimeout,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    ];

    /// <summary>True if the exception (or any inner exception) looks transient/retryable.</summary>
    public static bool IsTransient(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            // A storage adaptor that has already classified its own failure wins outright — it
            // knows its provider's semantics better than any text heuristic below.
            if (ex is Providers.TransferProviderException typed)
            {
                return typed.IsTransient;
            }
            if (ex is TimeoutException or SocketException)
            {
                return true;
            }
            if (ex is HttpRequestException httpEx && httpEx.StatusCode is { } code && Array.IndexOf(TransientStatusCodes, code) >= 0)
            {
                return true;
            }
            if (IsTransient(ex.Message))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True if the message text carries a transient/throttle marker.</summary>
    public static bool IsTransient(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }
        foreach (var marker in TransientMarkers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
