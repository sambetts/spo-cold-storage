namespace Migration.Engine.Providers;

/// <summary>
/// Provider-neutral address of an object in a cold store. For Azure Blob this is
/// (container, blob path); a future cold store (S3 bucket/key, another blob service) maps its own
/// coordinates onto the same two fields.
/// </summary>
public sealed record ColdStorageKey(string Container, string Path);

/// <summary>
/// What is known about an object already in the cold store. Drives the conflict-by-date decision
/// (via <see cref="ArchivedSourceLastModifiedUtc"/>) and post-copy verification. When
/// <see cref="Exists"/> is false the other fields are unset.
/// </summary>
public sealed record ColdObjectInfo
{
    public bool Exists { get; init; }
    public long Length { get; init; }

    /// <summary>Base64 MD5 the cold store computed for the stored bytes, if it exposes one.</summary>
    public string? ContentMd5Base64 { get; init; }

    /// <summary>
    /// The source's last-modified time recorded on the archive when it was written. Used to decide
    /// whether an incoming copy is newer/older/same as what's already archived (conflict-by-date).
    /// </summary>
    public DateTime? ArchivedSourceLastModifiedUtc { get; init; }

    public static readonly ColdObjectInfo Missing = new() { Exists = false };
}

/// <summary>
/// The descriptive metadata to stamp on a cold-store object when it is written, so the archive is
/// self-describing (a restore, or an audit, can reconstruct where it came from) and so
/// conflict-by-date works on the next migrate. Provider-neutral; the adaptor maps these onto its
/// own metadata mechanism (Azure blob metadata headers, S3 user metadata, …).
/// </summary>
public sealed record ColdWriteMetadata
{
    public required string OriginalStoreUrl { get; init; }
    public required string OriginalWebUrl { get; init; }
    public required string OriginalItemPath { get; init; }
    public required string OriginalFileName { get; init; }
    public required DateTime SourceLastModifiedUtc { get; init; }
    public required string ContentMd5Base64 { get; init; }
    public Guid JobId { get; init; }
    public string? RequestedByUpn { get; init; }
}

/// <summary>
/// The archive that content is copied <b>to</b> on migrate and read <b>from</b> on restore. Azure
/// Blob Storage is the normal implementation; the in-memory implementation makes the copy/verify/
/// conflict/restore branches unit-testable, and any future cold store just implements this.
///
/// Error contract: mirrors <see cref="ISourceStore"/> — throttle/transient failures MUST surface
/// as a transient <see cref="TransferProviderException"/>; "not found" is data (a
/// <see cref="ColdObjectInfo.Missing"/> / an idempotent delete), not a hard error.
/// </summary>
public interface IColdStore
{
    /// <summary>Stable id of the provider, for diagnostics/metadata (e.g. "AzureBlob").</summary>
    string ProviderId { get; }

    /// <summary>
    /// The canonical URL of an object, embedded in the placeholder so a restore (or an audit) can
    /// locate the archive. For Azure Blob this is the blob URL; providers with no addressable URL
    /// may return a stable provider URI (e.g. "provider://container/path").
    /// </summary>
    string GetObjectUrl(ColdStorageKey key);

    /// <summary>Reads what's already archived at <paramref name="key"/>. Returns <see cref="ColdObjectInfo.Missing"/> if absent.</summary>
    Task<ColdObjectInfo> GetInfoAsync(ColdStorageKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="key"/> with <paramref name="metadata"/>.
    /// Idempotent: overwrites any existing object (a retry re-writes the same bytes safely). Where the
    /// provider supports it the content MD5 is sent so the service validates the bytes on receipt.
    /// </summary>
    Task WriteAsync(
        ColdStorageKey key,
        ITransferContent content,
        ColdWriteMetadata metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the stored object matches the expected length and MD5. Throws (permanent
    /// <see cref="TransferProviderException"/> / integrity error) on a mismatch — the pipeline treats
    /// a failed verification as a copy failure and never deletes the source.
    /// </summary>
    Task VerifyAsync(ColdStorageKey key, long expectedLength, string expectedMd5Base64, CancellationToken cancellationToken = default);

    /// <summary>Opens a readable stream over the archived object (restore download). Throws if absent.</summary>
    Task<Stream> OpenReadAsync(ColdStorageKey key, CancellationToken cancellationToken = default);

    /// <summary>Removes the archived object. Idempotent: a missing object is a success. Returns whether it existed.</summary>
    Task<bool> DeleteIfExistsAsync(ColdStorageKey key, CancellationToken cancellationToken = default);
}
