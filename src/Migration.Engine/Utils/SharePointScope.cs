namespace Migration.Engine.Utils;

/// <summary>
/// Path-scope guard for caller-supplied SharePoint URLs.
/// <para>
/// Migrate/restore requests are authorized against a single <c>SiteUrl</c>, but the selected file,
/// folder and placeholder paths arrive as separate caller-controlled strings. Nothing in a
/// server-relative URL forces it to belong to the authorized site, and the worker acts <b>app-only</b>
/// with <c>Sites.FullControl.All</c> — so without this guard a contributor on <c>/sites/A</c> could
/// submit <c>/sites/B/...</c> and have the worker read, delete or overwrite files on a site they have
/// no rights to. Every caller-supplied path must therefore be proven to sit inside the authorized
/// site collection before it is persisted or queued.
/// </para>
/// </summary>
public static class SharePointScope
{
    /// <summary>
    /// Managed-path roots that start a *different* site collection. They matter only when the
    /// authorized site is the root site collection ("/"), which would otherwise appear to contain
    /// every site in the tenant. Stored without a trailing slash so both the exact root
    /// (<c>/sites</c>) and its descendants (<c>/sites/...</c>) are rejected.
    /// </summary>
    private static readonly string[] SiteCollectionManagedPaths =
        ["/sites", "/teams", "/personal", "/portals", "/search"];

    /// <summary>
    /// True when <paramref name="serverRelativeUrl"/> is a well-formed server-relative path that sits
    /// inside the site collection identified by <paramref name="siteUrl"/>. Rejects traversal
    /// (<c>..</c>, including percent-encoded forms), non-rooted paths, and paths belonging to another
    /// site collection.
    /// </summary>
    public static bool IsWithinSite(string siteUrl, string? serverRelativeUrl)
    {
        var path = NormalizePath(serverRelativeUrl);
        if (path is null)
        {
            return false;
        }
        var root = SiteRoot(siteUrl);
        if (root is null)
        {
            return false;
        }
        if (root.Length == 0)
        {
            // Root site collection: it owns "/" but NOT the managed paths that host other site
            // collections. Reject both the exact managed root and anything beneath it.
            return !SiteCollectionManagedPaths.Any(p =>
                path.Equals(p, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));
        }
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="otherSiteUrl"/> is the same host and the same site collection as the
    /// authorized <paramref name="siteUrl"/>. Used to reject a destination that blob metadata claims
    /// belongs to a different site than the one the caller was authorized for.
    /// </summary>
    public static bool IsSameSite(string siteUrl, string? otherSiteUrl)
    {
        if (string.IsNullOrWhiteSpace(otherSiteUrl))
        {
            return false;
        }
        if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var a) || !Uri.TryCreate(otherSiteUrl, UriKind.Absolute, out var b))
        {
            return false;
        }
        return string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(SiteRoot(siteUrl), SiteRoot(otherSiteUrl), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="webUrl"/> is a web — the site itself or any **sub-web** — inside the
    /// authorized site collection. Use this for a `WebUrl`, not <see cref="IsSameSite"/>: a sub-web
    /// (<c>/sites/A/team</c>) is legitimately part of site collection <c>/sites/A</c> but is not the
    /// same path, so <see cref="IsSameSite"/> would reject it.
    /// </summary>
    public static bool IsWebWithinSite(string siteUrl, string? webUrl)
    {
        if (string.IsNullOrWhiteSpace(webUrl)
            || !Uri.TryCreate(siteUrl, UriKind.Absolute, out var site)
            || !Uri.TryCreate(webUrl, UriKind.Absolute, out var web)
            || !string.Equals(site.Host, web.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var path = Uri.UnescapeDataString(web.AbsolutePath);
        return IsWithinSite(siteUrl, path.Length == 0 ? "/" : path);
    }

    /// <summary>
    /// True when a cold-storage blob key (<c>"{host}/{server-relative path}"</c>, see
    /// <c>ColdStorageBlobKey.Build</c>) names an archive that originated inside the authorized site.
    /// <para>
    /// The host segment is part of the security boundary, not decoration: it is what distinguishes
    /// <c>contoso.sharepoint.com</c> from <c>contoso-my.sharepoint.com</c> or another tenant with an
    /// identical server-relative path. Checking only the path would let
    /// <c>other-host.sharepoint.com/sites/A/secret.docx</c> pass for a job authorized against
    /// <c>contoso.sharepoint.com/sites/A</c>. Fails closed on a malformed/host-less key.
    /// </para>
    /// </summary>
    public static bool IsBlobKeyWithinSite(string siteUrl, string? blobKey)
    {
        if (string.IsNullOrWhiteSpace(blobKey) || !Uri.TryCreate(siteUrl, UriKind.Absolute, out var site))
        {
            return false;
        }
        var normalised = blobKey.Replace('\\', '/').TrimStart('/');
        var slash = normalised.IndexOf('/');
        if (slash <= 0 || slash == normalised.Length - 1)
        {
            return false;
        }
        return string.Equals(normalised[..slash], site.Host, StringComparison.OrdinalIgnoreCase)
            && IsWithinSite(siteUrl, "/" + normalised[(slash + 1)..]);
    }

    /// <summary>
    /// The site collection's server-relative root: "" for a root site collection,
    /// "/sites/finance" for a managed-path site. Null when the URL can't be parsed.
    /// </summary>
    private static string? SiteRoot(string siteUrl)
    {
        if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }
        return Uri.UnescapeDataString(uri.AbsolutePath).Replace('\\', '/').TrimEnd('/');
    }

    /// <summary>
    /// Canonicalises a server-relative URL for prefix comparison, or returns null when it is not a
    /// safe rooted path. Decoding is repeated until it reaches a fixed point, so neither a single
    /// (<c>%2e%2e</c>) nor a double (<c>%252e%252e</c>) encoding can hide a traversal segment from
    /// the check while still decoding to <c>..</c> somewhere downstream.
    /// </summary>
    private static string? NormalizePath(string? serverRelativeUrl)
    {
        if (string.IsNullOrWhiteSpace(serverRelativeUrl))
        {
            return null;
        }
        var decoded = serverRelativeUrl.Trim();
        // Bounded fixed-point decode. Anything that needs more rounds than this is hostile.
        for (var i = 0; i < 5; i++)
        {
            string next;
            try
            {
                next = Uri.UnescapeDataString(decoded);
            }
            catch (UriFormatException)
            {
                return null;
            }
            if (string.Equals(next, decoded, StringComparison.Ordinal))
            {
                break;
            }
            decoded = next;
            if (i == 4)
            {
                return null; // still changing after 5 rounds — refuse it
            }
        }
        decoded = decoded.Replace('\\', '/');
        if (decoded.Length == 0 || decoded[0] != '/' || decoded.Contains("//", StringComparison.Ordinal))
        {
            return null;
        }
        if (decoded.Any(char.IsControl))
        {
            return null;
        }
        foreach (var segment in decoded.Split('/'))
        {
            if (segment is ".." or ".")
            {
                return null;
            }
        }
        var trimmed = decoded.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }
}
