using System.Collections.Concurrent;
using System.Text.Json;

namespace Migration.Engine.Caching;

/// <summary>
/// Process-local cache (L1). Fast and free, but each instance has its own copy — on its
/// own it does nothing to stop a scaled-out worker hammering SharePoint, which is why it
/// is normally the front half of <see cref="TieredColdStorageCache"/> rather than the
/// whole story.
/// </summary>
public sealed class InMemoryColdStorageCache : IColdStorageCache
{
    private readonly ConcurrentDictionary<string, (DateTime ExpiresUtc, string Payload)> _entries =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Entries are only evicted when touched, so a long-lived process with many distinct
    /// keys would grow unbounded. Sweep when we cross this many entries.
    /// </summary>
    private const int SweepThreshold = 2_000;

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresUtc > DateTime.UtcNow)
            {
                try
                {
                    return Task.FromResult(JsonSerializer.Deserialize<T>(entry.Payload, CacheJson.Options));
                }
                catch (JsonException)
                {
                    // Shape changed between versions — treat as a miss.
                }
            }
            _entries.TryRemove(key, out _);
        }
        return Task.FromResult<T?>(null);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            _entries[key] = (DateTime.UtcNow + ttl, JsonSerializer.Serialize(value, CacheJson.Options));
        }
        catch (NotSupportedException)
        {
            // Unserialisable value: skip caching rather than fail the caller.
        }
        if (_entries.Count > SweepThreshold)
        {
            Sweep();
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresUtc <= now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }
}
