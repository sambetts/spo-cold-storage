using System.Text.Json;

namespace Migration.Engine.Caching;

/// <summary>
/// A cache that is shared <b>between processes</b> — issue #68.
///
/// <para>
/// The product's expensive calls are to SharePoint, and SharePoint throttles. Until now
/// every cache in the codebase was a <c>static</c> dictionary, which means each API
/// instance and each Function instance warmed its own copy: scaling out multiplied the
/// SharePoint load rather than amortising it, and a cold start threw the lot away. A
/// shared cache turns "once per process per TTL" into "once per tenant per TTL".
/// </para>
///
/// <para>
/// Everything here is <b>best-effort and fail-open</b>. A cache is an optimisation, never
/// a source of truth: if the backing store is unavailable the caller must behave exactly as
/// if the entry were missing and go do the real work. No cache failure may ever surface as
/// a user-visible error or block a migration.
/// </para>
/// </summary>
public interface IColdStorageCache
{
    /// <summary>
    /// Returns the cached value, or <c>null</c> when absent, expired or unreadable.
    /// Never throws.
    /// </summary>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Stores a value with an absolute expiry. Never throws.</summary>
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class;

    /// <summary>Removes an entry (e.g. after a change that invalidates it). Never throws.</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Boxed boolean so flags can go through the class-constrained cache API.</summary>
public sealed record CachedFlag(bool Value);

/// <summary>
/// Convenience helpers over <see cref="IColdStorageCache"/>.
/// </summary>
public static class ColdStorageCacheExtensions
{
    /// <summary>
    /// Returns the cached value, or computes it via <paramref name="factory"/>, caches it and
    /// returns it. A cache failure simply means <paramref name="factory"/> runs — the caller
    /// cannot tell the difference apart from the cost.
    /// </summary>
    public static async Task<T?> GetOrCreateAsync<T>(
        this IColdStorageCache cache,
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken cancellationToken = default) where T : class
    {
        var hit = await cache.GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        if (hit is not null)
        {
            return hit;
        }
        var created = await factory(cancellationToken).ConfigureAwait(false);
        if (created is not null)
        {
            await cache.SetAsync(key, created, ttl, cancellationToken).ConfigureAwait(false);
        }
        return created;
    }

    /// <summary>
    /// Boolean flavour of <see cref="GetOrCreateAsync{T}"/>. Booleans can't satisfy a class
    /// type constraint, so they are boxed into <see cref="CachedFlag"/> for storage.
    /// </summary>
    public static async Task<bool> GetOrCreateBoolAsync(
        this IColdStorageCache cache,
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<bool>> factory,
        CancellationToken cancellationToken = default)
    {
        var hit = await cache.GetAsync<CachedFlag>(key, cancellationToken).ConfigureAwait(false);
        if (hit is not null)
        {
            return hit.Value;
        }
        var created = await factory(cancellationToken).ConfigureAwait(false);
        await cache.SetAsync(key, new CachedFlag(created), ttl, cancellationToken).ConfigureAwait(false);
        return created;
    }
}

/// <summary>
/// Shared JSON settings for cache payloads. Kept deliberately plain so an entry written by
/// one version can still be read by the next; anything unreadable is treated as a miss.
/// </summary>
internal static class CacheJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
