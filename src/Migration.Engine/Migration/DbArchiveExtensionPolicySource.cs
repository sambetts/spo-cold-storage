using Entities;
using Entities.Configuration;
using Entities.DBEntities.ColdStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Migration.Engine.Migration;

/// <summary>
/// DB-backed <see cref="IArchiveExtensionPolicySource"/> with a short process-wide
/// cache so the per-item eligibility check doesn't hit SQL on every file. Reads the
/// enabled rows from <c>cold_storage_extension_rules</c> and buckets them into the
/// exclude (deny) and include (allow) sets by <see cref="ArchiveExtensionRuleMode"/>.
/// </summary>
public sealed class DbArchiveExtensionPolicySource : IArchiveExtensionPolicySource
{
    private readonly Config _config;
    private readonly ILogger? _logger;

    private static readonly object _lock = new();
    private static ArchiveExtensionPolicy? _cache;
    private static DateTime _cacheExpiresUtc;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public DbArchiveExtensionPolicySource(Config config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    public async Task<ArchiveExtensionPolicy> GetPolicyAsync(CancellationToken cancellationToken = default)
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
            var rows = await db.ColdStorageExtensionRules
                .Where(r => r.Enabled)
                .Select(r => new { r.Extension, r.Mode })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var ext = Normalize(row.Extension);
                if (ext.Length == 0)
                {
                    continue;
                }
                if (row.Mode == ArchiveExtensionRuleMode.Include)
                {
                    included.Add(ext);
                }
                else
                {
                    excluded.Add(ext);
                }
            }

            var policy = new ArchiveExtensionPolicy(excluded, included);
            lock (_lock)
            {
                _cache = policy;
                _cacheExpiresUtc = DateTime.UtcNow + Ttl;
            }
            return policy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Degrade to the last known-good policy (or empty) rather than block
            // archiving on a transient DB blip. The submit-time controller check is
            // authoritative; this source backs the worker's defense-in-depth re-check.
            lock (_lock)
            {
                if (_cache is not null)
                {
                    _logger?.LogWarning(ex, "Failed to refresh archive extension rules; using cached policy.");
                    return _cache;
                }
            }
            _logger?.LogWarning(ex, "Failed to load archive extension rules and no cache available; treating as none.");
            return ArchiveExtensionPolicy.Empty;
        }
    }

    private static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }
        var e = raw.Trim();
        if (!e.StartsWith('.'))
        {
            e = "." + e;
        }
        return e.ToLowerInvariant();
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
