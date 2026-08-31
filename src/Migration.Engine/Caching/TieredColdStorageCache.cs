using Microsoft.Extensions.Logging;

namespace Migration.Engine.Caching;

/// <summary>
/// Two-level cache: a process-local L1 in front of a shared L2 (issue #68).
///
/// <para>
/// L1 absorbs the repeat reads inside a single item's processing (free, microseconds);
/// L2 is what actually reduces SharePoint load, because a value warmed by <i>any</i>
/// instance is then available to <i>every</i> instance and survives a cold start.
/// </para>
///
/// <para>
/// L1's TTL is capped well below L2's so a value invalidated centrally can't linger in a
/// process for long. That's the trade this class exists to make explicit: a shared cache
/// is only safe if the local copy is short-lived.
/// </para>
/// </summary>
public sealed class TieredColdStorageCache(IColdStorageCache level1, IColdStorageCache level2, ILogger? logger = null) : IColdStorageCache
{
    private readonly IColdStorageCache _l1 = level1 ?? throw new ArgumentNullException(nameof(level1));
    private readonly IColdStorageCache _l2 = level2 ?? throw new ArgumentNullException(nameof(level2));
    private readonly ILogger? _logger = logger;

    /// <summary>
    /// Ceiling on how long a value may be trusted process-locally, regardless of the
    /// caller's TTL. Short enough that an admin change or an invalidation propagates
    /// promptly; long enough to absorb a burst.
    /// </summary>
    private static readonly TimeSpan MaxLocalTtl = TimeSpan.FromSeconds(30);

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        var local = await _l1.GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        if (local is not null)
        {
            return local;
        }

        var shared = await _l2.GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        if (shared is not null)
        {
            // Populate L1 so the next read in this process is free. We don't know the
            // remaining L2 lifetime, so use the local ceiling.
            await _l1.SetAsync(key, shared, MaxLocalTtl, cancellationToken).ConfigureAwait(false);
        }
        return shared;
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
    {
        var localTtl = ttl < MaxLocalTtl ? ttl : MaxLocalTtl;
        await _l1.SetAsync(key, value, localTtl, cancellationToken).ConfigureAwait(false);
        await _l2.SetAsync(key, value, ttl, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        // Remove locally first so this process stops serving it immediately. Other
        // instances drop it when their own L1 entry expires (<= MaxLocalTtl).
        await _l1.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        await _l2.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        _logger?.LogDebug("Cache entry '{Key}' invalidated.", key);
    }
}
