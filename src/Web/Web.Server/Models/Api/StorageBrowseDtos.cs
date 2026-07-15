namespace Web.Models.Api;

/// <summary>
/// One file entry in a cold-storage container listing returned by
/// <c>GET /api/storage/blobs</c>.
/// </summary>
public class StorageBlobEntry
{
    /// <summary>Full blob path (key) within the container.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Content length in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Last-modified timestamp, when known.</summary>
    public DateTimeOffset? LastModified { get; set; }
}

/// <summary>
/// Hierarchical listing of a cold-storage container under a given prefix, returned
/// by the server-side browse proxy (<c>GET /api/storage/blobs</c>).
/// </summary>
public class StorageListingResponse
{
    public string Container { get; set; } = string.Empty;

    public string Prefix { get; set; } = string.Empty;

    /// <summary>Virtual sub-folders (blob prefixes) directly under <see cref="Prefix"/>.</summary>
    public List<string> Folders { get; set; } = [];

    /// <summary>Files directly under <see cref="Prefix"/>.</summary>
    public List<StorageBlobEntry> Files { get; set; } = [];
}
