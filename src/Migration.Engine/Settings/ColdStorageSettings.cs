using Entities;
using Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Migration.Engine.Settings;

/// <summary>
/// Runtime product settings an admin can change from the portal without a redeploy
/// (issue #66). Both hosts — the API and the queue worker — resolve settings through
/// this, so a change applies everywhere.
/// </summary>
public interface IColdStorageSettingsSource
{
    /// <summary>
    /// Resolves an integer setting. Precedence: the DB row (portal) → the caller-supplied
    /// <paramref name="appSettingFallback"/> (this host's deployed app setting) → the code
    /// default carried by that fallback.
    /// </summary>
    Task<int> GetIntAsync(string key, int appSettingFallback, CancellationToken cancellationToken = default);
}

/// <summary>
/// Well-known runtime setting keys. Only these are readable/writable through the admin
/// API — the settings table is deliberately not a general-purpose config escape hatch.
/// </summary>
public static class ColdStorageSettingKeys
{
    /// <summary>
    /// 0 = archive only the current version (default); &gt; 0 = also capture and replay
    /// SharePoint version history. Mirrors <c>Config.ColdStorageCaptureVersionHistory</c>.
    /// </summary>
    public const string CaptureVersionHistory = "CaptureVersionHistory";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CaptureVersionHistory };

    public static bool IsKnown(string? key) => key is not null && All.Contains(key);
}

/// <summary>
/// DB-backed <see cref="IColdStorageSettingsSource"/> with a short process-wide cache so a
/// per-file lookup doesn't hit SQL on every item. Mirrors
/// <c>DbArchiveExtensionPolicySource</c>: degrade to the last known-good snapshot (then to
/// the app setting) rather than block work on a transient DB blip.
/// </summary>
public sealed class DbColdStorageSettingsSource : IColdStorageSettingsSource
{
    private readonly Config _config;
    private readonly ILogger? _logger;

    private static readonly object _lock = new();
    private static Dictionary<string, string?>? _cache;
    private static DateTime _cacheExpiresUtc;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public DbColdStorageSettingsSource(Config config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    public async Task<int> GetIntAsync(string key, int appSettingFallback, CancellationToken cancellationToken = default)
    {
        var raw = await GetRawAsync(key, cancellationToken).ConfigureAwait(false);
        if (raw is null)
        {
            return appSettingFallback;
        }
        return int.TryParse(raw.Trim(), out var value) ? value : appSettingFallback;
    }

    private async Task<string?> GetRawAsync(string key, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_cache is not null && _cacheExpiresUtc > DateTime.UtcNow)
            {
                return _cache.TryGetValue(key, out var hit) ? hit : null;
            }
        }

        try
        {
            using var db = new SPOColdStorageDbContext(_config);
            var rows = await db.ColdStorageSettings
                .AsNoTracking()
                .Select(s => new { s.SettingKey, s.SettingValue })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var snapshot = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                snapshot[row.SettingKey] = row.SettingValue;
            }

            lock (_lock)
            {
                _cache = snapshot;
                _cacheExpiresUtc = DateTime.UtcNow + Ttl;
            }
            return snapshot.TryGetValue(key, out var value) ? value : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_lock)
            {
                if (_cache is not null)
                {
                    _logger?.LogWarning(ex, "Failed to refresh cold-storage settings; using the cached snapshot.");
                    return _cache.TryGetValue(key, out var cached) ? cached : null;
                }
            }
            _logger?.LogWarning(ex, "Failed to load cold-storage settings and no cache is available; falling back to app settings.");
            return null;
        }
    }

    /// <summary>
    /// Clears the cache so a just-saved change is visible immediately in this process
    /// (other processes pick it up on the next TTL refresh).
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
