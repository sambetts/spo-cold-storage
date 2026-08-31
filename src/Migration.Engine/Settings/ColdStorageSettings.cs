using Entities;
using Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Migration.Engine.Settings;

/// <summary>
/// Runtime product settings an admin can change from the portal without a redeploy
/// (issues #66, #21, #20, #29, #33). Both hosts — the API and the queue worker —
/// resolve settings through this, so a change applies everywhere.
/// </summary>
public interface IColdStorageSettingsSource
{
    /// <summary>
    /// Resolves an integer setting. Precedence: the DB row (portal) → the caller-supplied
    /// <paramref name="appSettingFallback"/> (this host's deployed app setting) → the code
    /// default carried by that fallback.
    /// </summary>
    Task<int> GetIntAsync(string key, int appSettingFallback, CancellationToken cancellationToken = default);

    /// <summary>Same precedence as <see cref="GetIntAsync"/>, for free-text/choice settings.</summary>
    Task<string> GetStringAsync(string key, string appSettingFallback, CancellationToken cancellationToken = default);
}

/// <summary>How the portal should render a setting.</summary>
public enum RuntimeSettingKind
{
    /// <summary>0/1 flag shown as an on/off button.</summary>
    Toggle = 0,

    /// <summary>Integer shown as a number box. 0 conventionally disables the rule.</summary>
    Number = 1,

    /// <summary>One of <see cref="RuntimeSettingDefinition.Choices"/>, shown as a dropdown.</summary>
    Choice = 2,
}

/// <summary>
/// Metadata for one runtime-configurable setting: what it's called, how to render it,
/// and what it does (including any risk), so the portal can explain itself.
/// </summary>
public sealed record RuntimeSettingDefinition(
    string Key,
    string Label,
    RuntimeSettingKind Kind,
    string Description,
    string[]? Choices = null);

/// <summary>
/// The allow-list of runtime-configurable settings. Only these are readable/writable
/// through the admin API — the settings table is deliberately not a general-purpose
/// config escape hatch.
/// </summary>
public static class ColdStorageSettingKeys
{
    /// <summary>
    /// 0 = archive only the current version (default); 1 = also capture and replay
    /// SharePoint version history. Mirrors <c>Config.ColdStorageCaptureVersionHistory</c>.
    /// </summary>
    public const string CaptureVersionHistory = "CaptureVersionHistory";

    /// <summary>Mirrors <c>Config.ColdStorageSkipRetentionLabeled</c>.</summary>
    public const string SkipRetentionLabeled = "SkipRetentionLabeled";

    /// <summary>Mirrors <c>Config.ColdStorageDeleteBlobAfterRestore</c>.</summary>
    public const string DeleteBlobAfterRestore = "DeleteBlobAfterRestore";

    /// <summary>Mirrors <c>Config.ColdStorageMinFileSizeBytes</c>.</summary>
    public const string MinFileSizeBytes = "MinFileSizeBytes";

    /// <summary>Mirrors <c>Config.ColdStorageMaxAccessCount</c>.</summary>
    public const string MaxAccessCount = "MaxAccessCount";

    /// <summary>Mirrors <c>Config.ColdStorageReconcileIntervalHours</c>.</summary>
    public const string ReconcileIntervalHours = "ReconcileIntervalHours";

    /// <summary>Mirrors <c>Config.ColdStorageOrphanPolicy</c>.</summary>
    public const string OrphanPolicy = "OrphanPolicy";

    public static readonly IReadOnlyList<RuntimeSettingDefinition> Definitions =
    [
        new(CaptureVersionHistory, "Preserve version history", RuntimeSettingKind.Toggle,
            "Copy every prior SharePoint version to cold storage and replay it on restore. Each version is validated "
            + "(length + MD5) BEFORE the source file is deleted, so a capture failure fails the item and leaves the "
            + "file untouched. Uses more storage and makes archiving slower. Replayed versions are re-authored by the "
            + "service account with new timestamps — SharePoint does not allow setting a version's author or date."),

        new(SkipRetentionLabeled, "Skip files with a retention label", RuntimeSettingKind.Toggle,
            "Refuse to archive any file carrying a retention label, checked before anything is copied or deleted. "
            + "Note this detects item retention LABELS only — content under an eDiscovery hold with no label on the "
            + "item is not detected."),

        new(DeleteBlobAfterRestore, "Delete the archive after a verified restore", RuntimeSettingKind.Toggle,
            "On by default. Once a restore is verified, the archived blob (and its version sidecars) are removed so "
            + "the file isn't duplicated across SharePoint and cold storage. Turn off to keep a second copy. The "
            + "inferred 'already restored' skip path never deletes."),

        new(MinFileSizeBytes, "Minimum file size to archive (bytes)", RuntimeSettingKind.Number,
            "Files smaller than this are skipped — archiving tiny files costs more in placeholders and requests than "
            + "it saves. 0 disables the floor. Only applied when the file's size is known."),

        new(MaxAccessCount, "Maximum read count to archive", RuntimeSettingKind.Number,
            "Skip files read more often than this, so heavily-used documents stay in SharePoint even if rarely "
            + "edited. 0 disables the rule. A file with no recorded activity is never blocked."),

        new(ReconcileIntervalHours, "Orphan reconciliation interval (hours)", RuntimeSettingKind.Number,
            "How often the worker sweeps for orphaned cold-storage blobs — archives whose .url placeholder or whole "
            + "site has been deleted. 0 disables the scheduled sweep (an admin can still run it on demand). Takes "
            + "effect when the worker next restarts."),

        new(OrphanPolicy, "What to do with an orphaned archive", RuntimeSettingKind.Choice,
            "'report' (default) audits only. 'quarantine' tags the blob and keeps it for review. 'delete' removes it "
            + "— PERMANENT, because the SharePoint source was already deleted at archive time.",
            ["report", "quarantine", "delete"]),
    ];

    public static readonly IReadOnlySet<string> All =
        Definitions.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string? key) => key is not null && All.Contains(key);

    public static RuntimeSettingDefinition? Find(string? key)
        => key is null ? null : Definitions.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
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

    public async Task<string> GetStringAsync(string key, string appSettingFallback, CancellationToken cancellationToken = default)
    {
        var raw = await GetRawAsync(key, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(raw) ? appSettingFallback : raw.Trim();
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
