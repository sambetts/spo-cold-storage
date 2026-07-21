namespace Models.ColdStorage;

/// <summary>
/// Builds the globally-unique Azure blob key for a migrated SharePoint file.
///
/// The key encodes the SharePoint host (the tenant discriminator) followed by
/// the file's server-relative URL — which already encodes the site collection,
/// web and folder path within that host. As a result two files that share a
/// name and server-relative path but live in different site collections, or in
/// different tenants/host-named site collections, never collide in cold
/// storage.
///
/// The key is read back from the placeholder/<c>migration_job_items</c> row at
/// restore time, so the derivation only needs to be deterministic and unique;
/// it is never recomputed from the server-relative URL during a restore.
/// </summary>
public static class ColdStorageBlobKey
{
    /// <summary>
    /// Derives the blob key for <paramref name="serverRelativeUrl"/> within the
    /// tenant identified by <paramref name="siteUrl"/>.
    /// </summary>
    /// <param name="siteUrl">
    /// Absolute SharePoint site URL, e.g.
    /// <c>https://contoso.sharepoint.com/sites/finance</c>. Only the host is
    /// used. When it cannot be parsed the key falls back to the bare path.
    /// </param>
    /// <param name="serverRelativeUrl">
    /// Server-relative URL of the file, e.g.
    /// <c>/sites/finance/Shared Documents/report.docx</c>.
    /// </param>
    public static string Build(string siteUrl, string serverRelativeUrl)
    {
        if (string.IsNullOrWhiteSpace(serverRelativeUrl))
        {
            throw new ArgumentException("serverRelativeUrl must be provided", nameof(serverRelativeUrl));
        }

        var host = ExtractHost(siteUrl);
        var path = NormalizePath(serverRelativeUrl);

        return string.IsNullOrEmpty(host) ? path : host + "/" + path;
    }

    /// <summary>
    /// Reverses <see cref="Build"/>: given a blob key ("{host}/{server-relative path without leading
    /// slash}"), returns the file's SharePoint server-relative URL ("/sites/.../file"). Used by a
    /// blob-driven restore to recover the destination path when a blob is missing its metadata.
    /// Returns empty when the key has no path segment after the host.
    /// </summary>
    public static string ReverseServerRelativeUrl(string blobKey)
    {
        if (string.IsNullOrEmpty(blobKey))
        {
            return string.Empty;
        }
        var slash = blobKey.IndexOf('/');
        return slash < 0 || slash == blobKey.Length - 1 ? string.Empty : "/" + blobKey[(slash + 1)..];
    }

    /// <summary>
    /// Lower-cased DNS host of the site, used as the tenant discriminator. DNS
    /// hosts are case-insensitive, so lower-casing keeps a single tenant from
    /// producing two keys for the same file. Returns empty when the URL cannot
    /// be parsed.
    /// </summary>
    private static string ExtractHost(string siteUrl)
    {
        if (!string.IsNullOrWhiteSpace(siteUrl)
            && Uri.TryCreate(siteUrl, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.Host.ToLowerInvariant();
        }
        return string.Empty;
    }

    /// <summary>
    /// Strips the leading slash (so the host becomes the first virtual-directory
    /// segment) and normalises separators. File-name/path case is preserved
    /// because SharePoint paths can be case-sensitive.
    /// </summary>
    private static string NormalizePath(string serverRelativeUrl)
        => serverRelativeUrl.Replace('\\', '/').TrimStart('/');
}
