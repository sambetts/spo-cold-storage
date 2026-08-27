using Migration.Engine.Utils;
using Xunit;

namespace Migration.Engine.Tests.Utils;

/// <summary>
/// Authorization happens against one site URL, but the selected paths are separate caller-supplied
/// strings and the worker acts app-only with Sites.FullControl.All. These tests lock down the guard
/// that stops a contributor on site A reaching site B.
/// </summary>
public class SharePointScopeTests
{
    private const string SiteA = "https://contoso.sharepoint.com/sites/A";
    private const string RootSite = "https://contoso.sharepoint.com";

    [Theory]
    [InlineData("/sites/A")]
    [InlineData("/sites/A/")]
    [InlineData("/sites/A/Shared Documents/report.docx")]
    [InlineData("/SITES/a/Shared Documents/report.docx")]
    [InlineData("/sites/A/Shared%20Documents/report.docx")]
    public void IsWithinSite_accepts_paths_inside_the_authorized_site(string path)
    {
        Assert.True(SharePointScope.IsWithinSite(SiteA, path));
    }

    [Theory]
    // A different site collection entirely.
    [InlineData("/sites/B/Shared Documents/report.docx")]
    [InlineData("/teams/B/Shared Documents/report.docx")]
    [InlineData("/personal/bob_contoso_com/Documents/x.docx")]
    // Prefix-without-segment-boundary: must NOT match /sites/A.
    [InlineData("/sites/A-evil/Shared Documents/x.docx")]
    [InlineData("/sites/AB/Shared Documents/x.docx")]
    // Traversal, raw and single/double encoded.
    [InlineData("/sites/A/../B/x.docx")]
    [InlineData("/sites/A/%2e%2e/B/x.docx")]
    [InlineData("/sites/A/%252e%252e/B/x.docx")]
    [InlineData("/sites/A/./x.docx")]
    // Backslash and doubled separators.
    [InlineData("/sites/A\\..\\B\\x.docx")]
    [InlineData("//sites/A/x.docx")]
    // Not rooted / empty.
    [InlineData("sites/A/x.docx")]
    [InlineData("")]
    [InlineData(null)]
    public void IsWithinSite_rejects_anything_outside_or_malformed(string? path)
    {
        Assert.False(SharePointScope.IsWithinSite(SiteA, path));
    }

    [Theory]
    [InlineData("/Shared Documents/report.docx", true)]
    [InlineData("/Lists/Tasks/1_.000", true)]
    // The root site collection does NOT own other site collections' managed paths — neither the
    // managed root itself nor anything beneath it.
    [InlineData("/sites/B/x.docx", false)]
    [InlineData("/sites", false)]
    [InlineData("/sites/", false)]
    [InlineData("/teams/B/x.docx", false)]
    [InlineData("/personal/bob_contoso_com/x.docx", false)]
    [InlineData("/portals/hub/x.docx", false)]
    [InlineData("/portals/community/x.docx", false)]
    [InlineData("/search/x.docx", false)]
    // A folder that merely starts with a managed-path name is still root-site content.
    [InlineData("/sitesmap/x.docx", true)]
    public void IsWithinSite_root_site_excludes_other_site_collections(string path, bool expected)
    {
        Assert.Equal(expected, SharePointScope.IsWithinSite(RootSite, path));
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/A", true)]
    [InlineData("https://contoso.sharepoint.com/sites/A/", true)]
    [InlineData("https://CONTOSO.sharepoint.com/sites/a", true)]
    [InlineData("https://contoso.sharepoint.com/sites/B", false)]
    [InlineData("https://contoso.sharepoint.com/sites/A-evil", false)]
    [InlineData("https://evil.example/sites/A", false)]
    [InlineData("https://contoso-my.sharepoint.com/sites/A", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSameSite_requires_same_host_and_same_site_collection(string? other, bool expected)
    {
        Assert.Equal(expected, SharePointScope.IsSameSite(SiteA, other));
    }

    [Theory]
    // Same host + inside the site → allowed.
    [InlineData("contoso.sharepoint.com/sites/A/Shared Documents/a.docx", true)]
    // Same host but a different site collection.
    [InlineData("contoso.sharepoint.com/sites/B/Shared Documents/a.docx", false)]
    // DIFFERENT host with an identical path — the host is part of the boundary.
    [InlineData("other-host.sharepoint.com/sites/A/Shared Documents/a.docx", false)]
    [InlineData("contoso-my.sharepoint.com/sites/A/Shared Documents/a.docx", false)]
    // Prefix look-alike site.
    [InlineData("contoso.sharepoint.com/sites/A-evil/a.docx", false)]
    // Traversal inside the key.
    [InlineData("contoso.sharepoint.com/sites/A/../B/a.docx", false)]
    // Malformed / host-less keys fail closed.
    [InlineData("sites/A/a.docx", false)]
    [InlineData("contoso.sharepoint.com/", false)]
    [InlineData("contoso.sharepoint.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsBlobKeyWithinSite_enforces_host_and_path(string? blobKey, bool expected)
    {
        Assert.Equal(expected, SharePointScope.IsBlobKeyWithinSite(SiteA, blobKey));
    }

    [Fact]
    public void IsWithinSite_rejects_when_the_site_url_is_unparseable()
    {
        Assert.False(SharePointScope.IsWithinSite("not a url", "/sites/A/x.docx"));
    }

    [Theory]
    [InlineData("/sites/A/%00x.docx")]
    [InlineData("/sites/A/x\u0000.docx")]
    [InlineData("/sites/A/x\u000b.docx")]
    public void IsWithinSite_rejects_control_characters(string path)
    {
        Assert.False(SharePointScope.IsWithinSite(SiteA, path));
    }

    [Theory]
    // The site itself and any sub-web are inside the collection.
    [InlineData("https://contoso.sharepoint.com/sites/A", true)]
    [InlineData("https://contoso.sharepoint.com/sites/A/", true)]
    [InlineData("https://contoso.sharepoint.com/sites/A/team", true)]
    [InlineData("https://contoso.sharepoint.com/sites/A/team/sub", true)]
    // A different collection, a prefix look-alike, or another host is not.
    [InlineData("https://contoso.sharepoint.com/sites/B", false)]
    [InlineData("https://contoso.sharepoint.com/sites/A-evil", false)]
    [InlineData("https://evil.example/sites/A/team", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWebWithinSite_accepts_sub_webs_but_not_other_collections(string? webUrl, bool expected)
    {
        Assert.Equal(expected, SharePointScope.IsWebWithinSite(SiteA, webUrl));
    }

    [Fact]
    public void IsWebWithinSite_root_site_accepts_its_own_root_web()
    {
        Assert.True(SharePointScope.IsWebWithinSite(RootSite, RootSite));
        Assert.False(SharePointScope.IsWebWithinSite(RootSite, "https://contoso.sharepoint.com/sites/B"));
    }

    /// <summary>
    /// IsSameSite is path-identity, so a sub-web is deliberately NOT the same site — this is why a
    /// WebUrl must be checked with IsWebWithinSite instead.
    /// </summary>
    [Fact]
    public void IsSameSite_treats_a_sub_web_as_a_different_path()
    {
        Assert.False(SharePointScope.IsSameSite(SiteA, "https://contoso.sharepoint.com/sites/A/team"));
    }
}
