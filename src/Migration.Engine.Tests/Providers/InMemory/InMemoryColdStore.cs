using Migration.Engine.Providers;

namespace Migration.Engine.Tests.Providers.InMemory;

/// <summary>
/// In-memory <see cref="IColdStore"/> for unit tests. Stores objects in a dictionary keyed by
/// (container, path), records the metadata a real cold store would (crucially the source
/// last-modified, which drives conflict-by-date), and honours the same integrity/verify contract
/// as the Azure implementation. Fault injection via <see cref="Faults"/> lets a test simulate
/// throttling and transient/permanent errors on any operation.
/// </summary>
public sealed class InMemoryColdStore : IColdStore
{
    private sealed record StoredObject(byte[] Content, string Md5Base64, DateTime? ArchivedSourceLastModifiedUtc, ColdWriteMetadata Metadata);

    private readonly Dictionary<ColdStorageKey, StoredObject> _objects = new();

    public FaultQueue Faults { get; } = new();

    public string ProviderId => "InMemory";

    public string GetObjectUrl(ColdStorageKey key) => $"inmemory://{key.Container}/{key.Path}";

    /// <summary>Test helper: seed an already-archived object (e.g. to set up a conflict-by-date scenario).</summary>
    public void Seed(ColdStorageKey key, byte[] content, DateTime sourceLastModifiedUtc, string? md5Base64 = null)
        => _objects[key] = new StoredObject(
            content,
            md5Base64 ?? Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(content)),
            sourceLastModifiedUtc,
            new ColdWriteMetadata
            {
                OriginalStoreUrl = "seed", OriginalWebUrl = "seed", OriginalItemPath = key.Path,
                OriginalFileName = key.Path, SourceLastModifiedUtc = sourceLastModifiedUtc,
                ContentMd5Base64 = md5Base64 ?? Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(content)),
            });

    /// <summary>Test helper: does an object exist?</summary>
    public bool Contains(ColdStorageKey key) => _objects.ContainsKey(key);

    /// <summary>Test helper: the stored bytes (or null).</summary>
    public byte[]? BytesAt(ColdStorageKey key) => _objects.TryGetValue(key, out var o) ? o.Content : null;

    public Task<ColdObjectInfo> GetInfoAsync(ColdStorageKey key, CancellationToken cancellationToken = default)
    {
        Faults.MaybeThrow("GetInfo");
        if (!_objects.TryGetValue(key, out var obj))
        {
            return Task.FromResult(ColdObjectInfo.Missing);
        }
        return Task.FromResult(new ColdObjectInfo
        {
            Exists = true,
            Length = obj.Content.LongLength,
            ContentMd5Base64 = obj.Md5Base64,
            ArchivedSourceLastModifiedUtc = obj.ArchivedSourceLastModifiedUtc,
        });
    }

    public async Task WriteAsync(ColdStorageKey key, ITransferContent content, ColdWriteMetadata metadata, CancellationToken cancellationToken = default)
    {
        Faults.MaybeThrow("Write");
        await using var stream = await content.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        var bytes = ms.ToArray();
        // Idempotent overwrite, exactly like a blob upload with no If-None-Match.
        _objects[key] = new StoredObject(bytes, content.ContentMd5Base64, metadata.SourceLastModifiedUtc, metadata);
    }

    public Task VerifyAsync(ColdStorageKey key, long expectedLength, string expectedMd5Base64, CancellationToken cancellationToken = default)
    {
        Faults.MaybeThrow("Verify");
        if (!_objects.TryGetValue(key, out var obj))
        {
            throw TransferProviderException.Permanent($"Verify: object '{key.Container}/{key.Path}' does not exist.", ProviderId);
        }
        if (obj.Content.LongLength != expectedLength)
        {
            throw TransferProviderException.Permanent($"Verify: length {obj.Content.LongLength} != expected {expectedLength}.", ProviderId);
        }
        if (!string.Equals(obj.Md5Base64, expectedMd5Base64, StringComparison.Ordinal))
        {
            throw TransferProviderException.Permanent($"Verify: MD5 mismatch (expected {expectedMd5Base64}, got {obj.Md5Base64}).", ProviderId);
        }
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(ColdStorageKey key, CancellationToken cancellationToken = default)
    {
        Faults.MaybeThrow("OpenRead");
        if (!_objects.TryGetValue(key, out var obj))
        {
            throw TransferProviderException.Permanent($"Cold object '{key.Container}/{key.Path}' not found.", ProviderId);
        }
        return Task.FromResult<Stream>(new MemoryStream(obj.Content, writable: false));
    }

    public Task<bool> DeleteIfExistsAsync(ColdStorageKey key, CancellationToken cancellationToken = default)
    {
        Faults.MaybeThrow("Delete");
        return Task.FromResult(_objects.Remove(key));
    }
}
