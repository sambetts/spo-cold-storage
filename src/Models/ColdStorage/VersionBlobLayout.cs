namespace Models.ColdStorage;

/// <summary>
/// Deterministic blob layout for archived version history (issue #18). Prior
/// versions live under a sibling virtual folder of the current-version key, and
/// the manifest is a sidecar next to it, so version blobs are easy to enumerate
/// and never collide with the current-version blob.
/// </summary>
public static class VersionBlobLayout
{
    /// <summary>Blob key for a specific prior version's content.</summary>
    public static string ForVersion(string baseKey, string versionId)
    {
        if (string.IsNullOrEmpty(baseKey))
        {
            throw new ArgumentException("baseKey must be provided", nameof(baseKey));
        }
        if (string.IsNullOrEmpty(versionId))
        {
            throw new ArgumentException("versionId must be provided", nameof(versionId));
        }
        return $"{baseKey}.versions/{Sanitize(versionId)}";
    }

    /// <summary>Blob key for the version manifest sidecar.</summary>
    public static string ManifestKey(string baseKey)
    {
        if (string.IsNullOrEmpty(baseKey))
        {
            throw new ArgumentException("baseKey must be provided", nameof(baseKey));
        }
        return $"{baseKey}.versions.json";
    }

    private static string Sanitize(string versionId)
        => versionId.Replace('\\', '_').Replace('/', '_').Trim();
}
