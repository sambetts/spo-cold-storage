using Entities;
using Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Migration.Engine.Migration;

/// <summary>
/// DB-backed <see cref="IArchiveExclusionSource"/> with a short process-wide
/// cache so the per-item eligibility check doesn't hit SQL on every file. Reads
/// the enabled rows from <c>cold_storage_exclusions</c>.
/// </summary>
public sealed class DbArchiveExclusionSource : IArchiveExclusionSource
{
    private readonly Config _config;
    private readonly ILogger? _logger;

    private static readonly object _lock = new();
    private static IReadOnlyList<ArchiveExclusion>? _cache;
    private static DateTime _cacheExpiresUtc;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public DbArchiveExclusionSource(Config config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    public async Task<IReadOnlyList<ArchiveExclusion>> GetActiveExclusionsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_cache is not null && _cacheExpiresUtc > DateTime.UtcNow)
            {
                return _cache;
            }
        }

        try
        {
            using var db = new SPOColdStorageDbContext(_config);
            var list = await db.ColdStorageExclusions
                .Where(e => e.Enabled)
                .Select(e => new ArchiveExclusion(e.SiteUrl, e.ServerRelativePrefix))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            lock (_lock)
            {
                _cache = list;
                _cacheExpiresUtc = DateTime.UtcNow + Ttl;
            }
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Degrade to the last known-good set rather than block archiving on a
            // transient DB blip. The submit-time controller check is authoritative
            // (schema guaranteed there); this source backs the worker's
            // defense-in-depth re-check.
            lock (_lock)
            {
                if (_cache is not null)
                {
                    _logger?.LogWarning(ex, "Failed to refresh archive exclusions; using cached set of {Count}.", _cache.Count);
                    return _cache;
                }
            }
            _logger?.LogWarning(ex, "Failed to load archive exclusions and no cache available; treating as none.");
            return [];
        }
    }

    /// <summary>
    /// Clears the cache so a just-saved change is visible immediately within the
    /// same process (other processes pick it up on the next TTL refresh).
    /// </summary>
    public static void InvalidateCache()
    {
        lock (_lock)
        {
            _cache = null;
            _cacheExpiresUtc = DateTime.MinValue;
        }
    }
}
