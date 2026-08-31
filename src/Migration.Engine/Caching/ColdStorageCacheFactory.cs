using Entities.Configuration;
using Microsoft.Extensions.Logging;

namespace Migration.Engine.Caching;

/// <summary>
/// Builds the cache stack for a host and hands out the canonical key shapes.
///
/// <para>
/// One instance per process is enough — the L1 dictionary and the Table client are both
/// designed to be shared — so callers use <see cref="Shared"/> rather than threading an
/// instance through every constructor.
/// </para>
/// </summary>
public static class ColdStorageCacheFactory
{
    private static readonly object _lock = new();
    private static IColdStorageCache? _shared;

    /// <summary>
    /// The process-wide cache. Falls back to memory-only if the shared backend is turned
    /// off or unavailable, so callers never need a null check or a try/catch.
    /// </summary>
    public static IColdStorageCache Shared => _shared ?? new InMemoryColdStorageCache();

    /// <summary>
    /// Initialises the shared cache from config. Call once at host start-up; calling again
    /// replaces the stack (used by tests).
    /// </summary>
    public static IColdStorageCache Initialise(Config config, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var local = new InMemoryColdStorageCache();

        var backend = (config.ColdStorageCacheBackend ?? string.Empty).Trim();
        IColdStorageCache cache;
        if (string.Equals(backend, "memory", StringComparison.OrdinalIgnoreCase))
        {
            // Explicitly opted out of sharing — e.g. a single-instance dev box.
            logger?.LogInformation("Cold-storage cache: per-process (memory) only.");
            cache = local;
        }
        else
        {
            logger?.LogInformation("Cold-storage cache: shared via Table Storage, with a per-process front cache.");
            cache = new TieredColdStorageCache(local, new TableColdStorageCache(config, logger), logger);
        }

        lock (_lock)
        {
            _shared = cache;
        }
        return cache;
    }

    /// <summary>Replaces the shared cache (tests only).</summary>
    internal static void OverrideForTests(IColdStorageCache cache)
    {
        lock (_lock)
        {
            _shared = cache;
        }
    }
}

/// <summary>
/// Canonical cache keys. Centralised so a key's shape — and therefore what invalidates it
/// — is defined in exactly one place, and so no two features can collide.
/// </summary>
public static class ColdStorageCacheKeys
{
    /// <summary>Whether a user may trigger archive/restore on a site.</summary>
    public static string SiteContributor(string siteUrl, string upn)
        => $"authz|contrib|{Normalise(siteUrl)}|{upn.ToLowerInvariant()}";

    /// <summary>A web's role definitions (name → id), used when replaying permissions.</summary>
    public static string RoleDefinitions(string webUrl)
        => $"sp|roledefs|{Normalise(webUrl)}";

    /// <summary>Whether a library already carries the cold-storage columns.</summary>
    public static string LibraryProvisioned(string host, string libraryRootUrl)
        => $"sp|libcols|{host.ToLowerInvariant()}|{libraryRootUrl.ToLowerInvariant()}";

    /// <summary>All cached state for a site, for bulk invalidation after a change.</summary>
    public static string SitePrefix(string siteUrl) => $"sp|{Normalise(siteUrl)}";

    private static string Normalise(string url) => url.TrimEnd('/').ToLowerInvariant();
}
