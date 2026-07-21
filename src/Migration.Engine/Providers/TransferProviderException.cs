namespace Migration.Engine.Providers;

/// <summary>
/// The single, provider-neutral exception a storage adaptor raises so the pipelines can make a
/// uniform retry decision without knowing anything about SharePoint, Azure Blob, or any future
/// provider. Each concrete adaptor is responsible for translating its own failures into this:
/// a throttle/timeout/transient gateway error becomes <see cref="IsTransient"/> == true (carrying
/// the server's <see cref="RetryAfterSeconds"/> when it gave one); a permanent error (auth,
/// not-found used as a hard error, integrity mismatch) becomes <see cref="IsTransient"/> == false.
///
/// This is the abstraction of the hard-won throttling lessons: the pipelines park transient
/// failures in <c>RetryScheduled</c> with a Retry-After-aware backoff, and only fail terminally on
/// permanent errors or an exhausted attempt budget. The in-memory adaptor throws this directly to
/// simulate throttling and transient blips in unit tests, so the retry logic is fully testable
/// without any live service.
/// </summary>
public sealed class TransferProviderException : Exception
{
    /// <summary>True for a retry-worthy failure (throttle 429, timeout, transient 5xx, I/O blip).</summary>
    public bool IsTransient { get; }

    /// <summary>
    /// The server-requested wait, in seconds, when the transient failure carried one (e.g. a
    /// SharePoint/Azure <c>Retry-After</c> header). Null when the provider gave no hint and the
    /// caller should fall back to its own backoff schedule.
    /// </summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>The provider that raised this, for diagnostics (e.g. "SharePointOnline", "AzureBlob").</summary>
    public string? Provider { get; }

    public TransferProviderException(
        string message,
        bool isTransient,
        int? retryAfterSeconds = null,
        string? provider = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        IsTransient = isTransient;
        RetryAfterSeconds = retryAfterSeconds;
        Provider = provider;
        // Also stash the Retry-After on Exception.Data under the key ThrottleInfo already reads,
        // so the existing Retry-After plumbing works whether an adaptor throws this typed exception
        // or a raw provider exception.
        if (retryAfterSeconds is int ra)
        {
            Data[Lifecycle.ThrottleInfo.RetryAfterDataKey] = ra;
        }
    }

    /// <summary>Convenience factory for a throttle (429) with an optional server Retry-After.</summary>
    public static TransferProviderException Throttled(string message, int? retryAfterSeconds = null, string? provider = null, Exception? inner = null)
        => new(message, isTransient: true, retryAfterSeconds, provider, inner);

    /// <summary>Convenience factory for a transient non-throttle blip (timeout, transient 5xx, I/O error).</summary>
    public static TransferProviderException Transient(string message, string? provider = null, Exception? inner = null)
        => new(message, isTransient: true, retryAfterSeconds: null, provider, inner);

    /// <summary>Convenience factory for a permanent failure that should fail the item terminally.</summary>
    public static TransferProviderException Permanent(string message, string? provider = null, Exception? inner = null)
        => new(message, isTransient: false, retryAfterSeconds: null, provider, inner);
}
