using Migration.Engine.Migration;
using Models.ColdStorage;

namespace Migration.Engine.Providers;

/// <summary>
/// Provider-neutral address of one item in a source store. For SharePoint this is
/// (site collection URL, web URL, server-relative path); a future provider (a file share, S3,
/// Google Drive…) maps its own coordinates onto the same three fields. Kept as plain strings so
/// no provider SDK type leaks across the abstraction.
/// </summary>
public sealed record SourceItemRef(string StoreUrl, string WebUrl, string ItemPath)
{
    /// <summary>Human-readable path for logging.</summary>
    public string DisplayPath => ItemPath;
}

/// <summary>
/// Point-in-time metadata about a source item, read just before a destructive step so the
/// pipeline can enforce the delete-safety checks (length still matches the copy; not locked) and
/// capture authorship onto the placeholder. <see cref="Exists"/> == false means the item is gone
/// (already migrated / externally deleted) — a non-error, resumable condition.
/// </summary>
public sealed record SourceItemInfo
{
    public bool Exists { get; init; }
    public long Length { get; init; }
    public DateTime? LastModifiedUtc { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public string? CreatedBy { get; init; }
    public string? ModifiedBy { get; init; }

    /// <summary>True when the item is checked out / locked and cannot be safely deleted.</summary>
    public bool IsLocked { get; init; }
    public string? LockReason { get; init; }

    /// <summary>Sentinel for an item that no longer exists in the source.</summary>
    public static readonly SourceItemInfo Missing = new() { Exists = false };
}

/// <summary>
/// The live/primary store that content is archived <b>from</b> and restored <b>to</b>, and that
/// holds the placeholder pointer left behind after archival. SharePoint Online is the normal
/// implementation; the in-memory implementation makes every branch below unit-testable, and any
/// future primary store (file share, another cloud drive) just implements this contract.
///
/// Error contract: every method either succeeds or throws. Throttle/timeout/transient failures
/// MUST be surfaced as a <see cref="TransferProviderException"/> with <c>IsTransient == true</c>
/// (carrying Retry-After when known) so the pipeline parks the item for an automatic retry;
/// permanent failures as <c>IsTransient == false</c>. "Not found" is modelled as data
/// (<see cref="SourceItemInfo.Exists"/> false / a null pointer / an idempotent delete), never as a
/// hard error, because the pipelines rely on already-gone being a success (the File-Not-Found
/// lesson).
/// </summary>
public interface ISourceStore
{
    /// <summary>Stable id of the provider, for diagnostics/metadata (e.g. "SharePointOnline").</summary>
    string ProviderId { get; }

    /// <summary>Reads current metadata. Returns <see cref="SourceItemInfo.Missing"/> if the item is gone.</summary>
    Task<SourceItemInfo> GetItemAsync(SourceItemRef item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compliance-hold gate: content under a legal/retention hold must never leave the source.
    /// Implementations that don't support holds return <see cref="HoldStatus.NotOnHold"/>.
    /// </summary>
    Task<HoldStatus> CheckHoldAsync(SourceItemRef item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the item's content as re-readable <see cref="ITransferContent"/> (length + MD5
    /// computed once). Implementations MUST guard against a truncated read (the bytes returned
    /// must match the source's declared length) so a partial download can never be treated as a
    /// good copy and lead to a source delete.
    /// </summary>
    Task<ITransferContent> ReadContentAsync(SourceItemRef item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the item. Idempotent: if the item is already gone this completes successfully
    /// (never throws not-found) — the archival goal is met either way. Only ever called by the
    /// pipeline after a verified cold-storage copy.
    /// </summary>
    Task DeleteAsync(SourceItemRef item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes content back to the source at <paramref name="item"/> (the restore upload), honouring
    /// <paramref name="conflict"/>. Idempotent under retry: if a prior attempt's write actually
    /// landed (the destination already holds content of the same <see cref="ITransferContent.Length"/>)
    /// but its response was lost, this returns success rather than failing on a conflict. Returns the
    /// final server-relative path written (may differ from <paramref name="item"/> for Rename).
    /// </summary>
    Task<string> WriteContentAsync(
        SourceItemRef item,
        ITransferContent content,
        ConflictBehavior conflict,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the placeholder pointer that replaces the archived item, returning its location.
    /// <paramref name="userFacingUrl"/> optionally overrides the URL embedded in the pointer
    /// (e.g. an app download route) instead of the raw cold-storage URL.
    /// <paramref name="stampMetadataColumns"/> (default false) additionally copies the original
    /// authorship onto visible "Original *" columns on the pointer's container; when false the
    /// pointer is left as just the shortcut file.
    /// </summary>
    Task<string> WritePointerAsync(
        SourceItemRef item,
        PlaceholderFileMetadata pointer,
        string? userFacingUrl = null,
        bool stampMetadataColumns = false,
        CancellationToken cancellationToken = default);

    /// <summary>Reads + parses the placeholder pointer at <paramref name="pointer"/>. Null if absent/corrupt.</summary>
    Task<PlaceholderFileMetadata?> ReadPointerAsync(SourceItemRef pointer, CancellationToken cancellationToken = default);

    /// <summary>Removes the placeholder pointer. Idempotent: a missing pointer is a success.</summary>
    Task RemovePointerAsync(SourceItemRef pointer, CancellationToken cancellationToken = default);
}
