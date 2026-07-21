using Migration.Engine.Migration;
using Migration.Engine.Providers;
using Models.ColdStorage;

namespace Migration.Engine.Tests.Providers.InMemory;

/// <summary>
/// In-memory <see cref="ISourceStore"/> for unit tests. Models live items and placeholder pointers
/// in dictionaries, and faithfully reproduces the contract the pipelines depend on: idempotent
/// delete (already-gone is a success), conflict handling + response-lost-but-landed idempotency on
/// write, and the hold gate. Fault injection via <see cref="Faults"/> drives throttling and
/// transient/permanent errors.
///
/// Operation names for <see cref="FaultQueue"/>: "GetItem", "CheckHold", "ReadContent", "Delete",
/// "WriteContent", "WriteContentAfter" (thrown AFTER the write lands — the "response lost but bytes
/// landed" case), "WritePointer", "ReadPointer", "RemovePointer".
/// </summary>
public sealed class InMemorySourceStore : ISourceStore
{
    public sealed class Item
    {
        public required byte[] Content { get; set; }
        public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string CreatedBy { get; set; } = "Alice";
        public string ModifiedBy { get; set; } = "Bob";
        public bool Locked { get; set; }
        public string? ComplianceTag { get; set; }
    }

    private readonly Dictionary<string, Item> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlaceholderFileMetadata> _pointers = new(StringComparer.OrdinalIgnoreCase);

    public FaultQueue Faults { get; } = new();

    public string ProviderId => "InMemory";

    /// <summary>Test helper: seed a live source item.</summary>
    public Item Seed(string path, byte[] content, DateTime? lastModifiedUtc = null, string createdBy = "Alice", string modifiedBy = "Bob")
    {
        var item = new Item
        {
            Content = content,
            LastModifiedUtc = lastModifiedUtc ?? DateTime.UtcNow,
            CreatedBy = createdBy,
            ModifiedBy = modifiedBy,
        };
        _items[path] = item;
        return item;
    }

    /// <summary>Test helper: seed a placeholder pointer (as archival would have left behind).</summary>
    public void SeedPointer(string path, PlaceholderFileMetadata metadata) => _pointers[path] = metadata;

    /// <summary>Test helper: does a live item exist at <paramref name="path"/>?</summary>
    public bool Exists(string path) => _items.ContainsKey(path);

    /// <summary>Test helper: does a placeholder pointer exist at <paramref name="path"/>?</summary>
    public bool HasPointer(string path) => _pointers.ContainsKey(path);

    /// <summary>Test helper: the bytes at <paramref name="path"/> (or null).</summary>
    public byte[]? BytesAt(string path) => _items.TryGetValue(path, out var i) ? i.Content : null;

    public Task<SourceItemInfo> GetItemAsync(SourceItemRef item, CancellationToken cancellationToken = default)
    {
        Faults.MaybeThrow("GetItem");
        if (!_items.TryGetValue(item.ItemPath, out var found))
        {
            return Task.FromResult(SourceItemInfo.Missing);
        }
        return Task.FromResult(new SourceItemInfo
        {
            Exists = true,
            Length = found.Content.LongLength,
            LastModifiedUtc = found.LastModifiedUtc,
            CreatedUtc = found.CreatedUtc,
            CreatedBy = found.CreatedBy,
            ModifiedBy = found.ModifiedBy,
            IsLocked = found.Locked,
            LockReason = found.Locked ? "checked out" : null,
        });
    }

    public Task<HoldStatus> CheckHoldAsync(SourceItemRef item, CancellationToken cancellationToken = default)
    {
        Faults.MaybeThrow("CheckHold");
        if (_items.TryGetValue(item.ItemPath, out var found) && RetentionLabelHoldDetector.ShouldTreatAsHold(found.ComplianceTag))
        {
            return Task.FromResult(new HoldStatus(true, $"under retention label '{found.ComplianceTag}'"));
        }
        return Task.FromResult(HoldStatus.NotOnHold);
    }

    public Task<ITransferContent> ReadContentAsync(SourceItemRef item, CancellationToken cancellationToken = default)
    {
        Faults.MaybeThrow("ReadContent");
        if (!_items.TryGetValue(item.ItemPath, out var found))
        {
            throw TransferProviderException.Permanent($"Source item '{item.ItemPath}' not found.", ProviderId);
        }
        return Task.FromResult<ITransferContent>(new InMemoryTransferContent(found.Content));
    }

    public Task DeleteAsync(SourceItemRef item, CancellationToken cancellationToken = default)
    {
        Faults.MaybeThrow("Delete");
        _items.Remove(item.ItemPath); // idempotent: already gone is fine
        return Task.CompletedTask;
    }

    public async Task<string> WriteContentAsync(SourceItemRef item, ITransferContent content, ConflictBehavior conflict, CancellationToken cancellationToken = default)
    {
        Faults.MaybeThrow("WriteContent");

        var targetPath = item.ItemPath;
        if (_items.TryGetValue(targetPath, out var existing))
        {
            // Response-lost-but-landed: a prior attempt's write already put the same-length content
            // here. Treat as done (idempotent) rather than conflicting — mirrors the real adaptor.
            if (existing.Content.LongLength == content.Length)
            {
                return targetPath;
            }
            switch (conflict)
            {
                case ConflictBehavior.Overwrite:
                    break;
                case ConflictBehavior.Rename:
                    targetPath = targetPath + $".restored-{Guid.NewGuid():N}";
                    break;
                default: // Fail
                    throw TransferProviderException.Permanent($"Conflict at '{targetPath}' and conflict behaviour = Fail.", ProviderId);
            }
        }

        await using var stream = await content.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        _items[targetPath] = new Item { Content = ms.ToArray(), LastModifiedUtc = DateTime.UtcNow };

        // Optional: simulate the write landing but the response being lost (transient), so the
        // pipeline retries and the retry above short-circuits on the same-length match.
        Faults.MaybeThrow("WriteContentAfter");

        return targetPath;
    }

    public Task<string> WritePointerAsync(SourceItemRef item, PlaceholderFileMetadata pointer, string? userFacingUrl = null, CancellationToken cancellationToken = default)
    {
        Faults.MaybeThrow("WritePointer");
        var pointerPath = item.ItemPath + ".url";
        _pointers[pointerPath] = pointer;
        return Task.FromResult(pointerPath);
    }

    public Task<PlaceholderFileMetadata?> ReadPointerAsync(SourceItemRef pointer, CancellationToken cancellationToken = default)
    {
        Faults.MaybeThrow("ReadPointer");
        _pointers.TryGetValue(pointer.ItemPath, out var found);
        return Task.FromResult(found);
    }

    public Task RemovePointerAsync(SourceItemRef pointer, CancellationToken cancellationToken = default)
    {
        Faults.MaybeThrow("RemovePointer");
        _pointers.Remove(pointer.ItemPath); // idempotent
        return Task.CompletedTask;
    }
}
