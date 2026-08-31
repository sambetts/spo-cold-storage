using Azure.Storage.Blobs;
using Entities;
using Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Migration.Engine.Utils;

/// <summary>
/// Resolves the storage account that actually holds a given cold-storage container.
///
/// <para>
/// A <c>ColdStorageContainer</c> can name its own <c>StorageAccountUri</c>, so containers
/// may live in different storage accounts. Resolving blob clients straight from
/// <c>Config.ConnectionStrings.Storage</c> silently targets the DEFAULT account instead
/// (issue #62): a restore then reports the archive as missing, or — worse — reads and
/// deletes a same-keyed blob in the wrong account.
/// </para>
///
/// <para>
/// Cached briefly (like the other DB-backed sources) so a per-file restore doesn't hit
/// SQL for every blob operation. Falls back to the default account when the container is
/// unknown or has no explicit URI, which is the single-account default deployment.
/// </para>
/// </summary>
public static class ColdStorageContainerClientFactory
{
    private static readonly object _lock = new();
    private static Dictionary<string, string?>? _cache;
    private static DateTime _cacheExpiresUtc;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Returns a container client bound to the storage account that owns
    /// <paramref name="containerName"/>.
    /// </summary>
    public static async Task<BlobContainerClient> GetContainerAsync(
        Config config, string containerName, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var connection = await ResolveConnectionAsync(config, containerName, logger, cancellationToken).ConfigureAwait(false);
        return BlobServiceClientFactory.Create(connection, config).GetBlobContainerClient(containerName);
    }

    private static async Task<string> ResolveConnectionAsync(
        Config config, string containerName, ILogger? logger, CancellationToken cancellationToken)
    {
        var fallback = config.ConnectionStrings.Storage;
        if (string.IsNullOrWhiteSpace(containerName))
        {
            return fallback;
        }

        lock (_lock)
        {
            if (_cache is not null && _cacheExpiresUtc > DateTime.UtcNow)
            {
                return Pick(_cache, containerName, fallback);
            }
        }

        try
        {
            using var db = new SPOColdStorageDbContext(config);
            var rows = await db.ColdStorageContainers
                .AsNoTracking()
                .Select(c => new { c.BlobContainerName, c.StorageAccountUri })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var snapshot = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(row.BlobContainerName))
                {
                    snapshot[row.BlobContainerName] = row.StorageAccountUri;
                }
            }

            lock (_lock)
            {
                _cache = snapshot;
                _cacheExpiresUtc = DateTime.UtcNow + Ttl;
            }
            return Pick(snapshot, containerName, fallback);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_lock)
            {
                if (_cache is not null)
                {
                    logger?.LogWarning(ex, "Failed to refresh cold-storage container accounts; using the cached mapping.");
                    return Pick(_cache, containerName, fallback);
                }
            }
            logger?.LogWarning(ex, "Failed to resolve the storage account for container '{Container}'; using the default account.", containerName);
            return fallback;
        }
    }

    private static string Pick(Dictionary<string, string?> map, string containerName, string fallback)
        => map.TryGetValue(containerName, out var uri) && !string.IsNullOrWhiteSpace(uri) ? uri : fallback;

    /// <summary>Clears the cache so a container's storage account change is picked up immediately.</summary>
    public static void InvalidateCache()
    {
        lock (_lock)
        {
            _cache = null;
            _cacheExpiresUtc = DateTime.MinValue;
        }
    }
}
