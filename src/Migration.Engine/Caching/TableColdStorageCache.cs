using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Entities.Configuration;
using Microsoft.Extensions.Logging;
using Migration.Engine.Utils;
using System.Text.Json;

namespace Migration.Engine.Caching;

/// <summary>
/// Shared cache (L2) backed by Azure Table Storage — issue #68.
///
/// <para>
/// Table Storage is used rather than Redis because it needs <b>no new infrastructure</b>:
/// the deployment already has a storage account, already reaches it over a private
/// endpoint, and both hosts' managed identities already have data access. A cache entry is
/// a single point read by partition+row key, which is what Table Storage is fastest at, and
/// the cost is negligible. Redis would mean a new resource, a new private endpoint, a new
/// secret and real monthly cost for a workload that is tiny and latency-tolerant.
/// </para>
///
/// <para>
/// Expiry is enforced on read against a stored timestamp: Table Storage has no native TTL,
/// and a background purge isn't worth a timer — an expired row is simply a miss, and it is
/// overwritten the next time that key is written.
/// </para>
///
/// <para>
/// Fail-open throughout: every failure is a cache miss. If the table can't be reached the
/// product behaves exactly as it did before this cache existed.
/// </para>
/// </summary>
public sealed class TableColdStorageCache : IColdStorageCache
{
    private const string PartitionKey = "cs";

    private readonly ILogger? _logger;
    private readonly Lazy<TableClient?> _table;

    /// <summary>
    /// Set when the backing table is known to be unusable (bad config, no permission), so
    /// we stop paying the latency of retrying it on every single lookup.
    /// </summary>
    private volatile bool _disabled;

    public TableColdStorageCache(Config config, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _logger = logger;
        _table = new Lazy<TableClient?>(() => CreateTable(config, logger), isThreadSafe: true);
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        var table = Table();
        if (table is null)
        {
            return null;
        }
        try
        {
            var response = await table.GetEntityIfExistsAsync<CacheEntity>(
                PartitionKey, Sanitize(key), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!response.HasValue || response.Value is null)
            {
                return null;
            }
            if (response.Value.ExpiresUtc <= DateTime.UtcNow)
            {
                return null; // Expired: a miss. Overwritten on the next write of this key.
            }
            return string.IsNullOrEmpty(response.Value.Payload)
                ? null
                : JsonSerializer.Deserialize<T>(response.Value.Payload, CacheJson.Options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Degrade(ex, "read");
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
    {
        var table = Table();
        if (table is null)
        {
            return;
        }
        try
        {
            var entity = new CacheEntity
            {
                PartitionKey = PartitionKey,
                RowKey = Sanitize(key),
                Payload = JsonSerializer.Serialize(value, CacheJson.Options),
                ExpiresUtc = DateTime.UtcNow + ttl,
            };
            await table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Degrade(ex, "write");
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var table = Table();
        if (table is null)
        {
            return;
        }
        try
        {
            await table.DeleteEntityAsync(PartitionKey, Sanitize(key), ETag.All, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Degrade(ex, "remove");
        }
    }

    private TableClient? Table() => _disabled ? null : _table.Value;

    private void Degrade(Exception ex, string operation)
    {
        // One warning, then quiet: a broken cache must not flood the logs, and it must not
        // slow the product down by being retried on every call.
        if (!_disabled)
        {
            _disabled = true;
            _logger?.LogWarning(ex,
                "Shared cache {Operation} failed; continuing without a shared cache (falling back to per-process caching only).",
                operation);
        }
    }

    private static TableClient? CreateTable(Config config, ILogger? logger)
    {
        try
        {
            var tableName = string.IsNullOrWhiteSpace(config.ColdStorageCacheTableName)
                ? "coldstoragecache"
                : config.ColdStorageCacheTableName.Trim();

            var connection = config.ConnectionStrings.Storage;
            TableServiceClient service;
            if (connection.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                service = new TableServiceClient(new Uri(TableEndpoint(connection)), new DefaultAzureCredential());
            }
            else if (connection.Contains("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase)
                     || connection.Contains("AccountName=devstoreaccount1", StringComparison.OrdinalIgnoreCase))
            {
                service = new TableServiceClient(connection);
            }
            else
            {
                // Production: the storage account has shared-key access disabled, so use the
                // managed identity and take only the account name from the connection string
                // (mirrors BlobServiceClientFactory).
                service = new TableServiceClient(new Uri(TableEndpoint(connection)), new DefaultAzureCredential());
            }

            var table = service.GetTableClient(tableName);
            table.CreateIfNotExists();
            return table;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not open the shared cache table; per-process caching only.");
            return null;
        }
    }

    /// <summary>
    /// Derives the table endpoint from whatever form of storage connection we were given.
    /// </summary>
    private static string TableEndpoint(string connectionString)
    {
        if (connectionString.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString.Replace(".blob.", ".table.", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        }

        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);

        if (parts.TryGetValue("TableEndpoint", out var explicitEndpoint))
        {
            return explicitEndpoint.TrimEnd('/');
        }
        if (parts.TryGetValue("BlobEndpoint", out var blobEndpoint))
        {
            return blobEndpoint.Replace(".blob.", ".table.", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        }
        if (parts.TryGetValue("AccountName", out var accountName))
        {
            return $"https://{accountName}.table.core.windows.net";
        }
        throw new ArgumentException("Storage connection string has no TableEndpoint, BlobEndpoint or AccountName.", nameof(connectionString));
    }

    /// <summary>
    /// Table row keys can't contain <c>/ \ # ?</c> or control characters, and our keys are
    /// built from URLs. Hash anything awkward so the key is always legal and bounded.
    /// Public so the key rules are covered by a test rather than discovered in production.
    /// </summary>
    public static string Sanitize(string key)
    {
        if (key.Length <= 512 && !key.Any(c => c is '/' or '\\' or '#' or '?' || char.IsControl(c)))
        {
            return key;
        }
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash);
    }

    private sealed class CacheEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = string.Empty;
        public string RowKey { get; set; } = string.Empty;
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        public string Payload { get; set; } = string.Empty;
        public DateTime ExpiresUtc { get; set; }
    }
}
