using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

/// <summary>
/// Locks the collision-safe blob-key contract from issue #12: same-name files
/// from different sites/tenants must map to distinct, deterministic blob keys
/// so they neither overwrite each other in cold storage nor restore to the
/// wrong place.
/// </summary>
public class ColdStorageBlobKeyTests
{
    [Fact]
    public void SameNameAndPath_DifferentSiteCollections_ProduceDistinctKeys()
    {
        var a = ColdStorageBlobKey.Build(
            "https://contoso.sharepoint.com/sites/marketing",
            "/sites/marketing/Shared Documents/report.docx");
        var b = ColdStorageBlobKey.Build(
            "https://contoso.sharepoint.com/sites/finance",
            "/sites/finance/Shared Documents/report.docx");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void IdenticalServerRelativePath_DifferentHosts_ProduceDistinctKeys()
    {
        // Host-named site collections can share an identical server-relative path,
        // so the host must be part of the key.
        var a = ColdStorageBlobKey.Build("https://team.contoso.com", "/Shared Documents/report.docx");
        var b = ColdStorageBlobKey.Build("https://team.fabrikam.com", "/Shared Documents/report.docx");

        Assert.NotEqual(a, b);
        Assert.StartsWith("team.contoso.com/", a);
        Assert.StartsWith("team.fabrikam.com/", b);
    }

    [Fact]
    public void Build_IsDeterministic_AndStripsLeadingSlash()
    {
        const string site = "https://contoso.sharepoint.com/sites/x";
        const string url = "/sites/x/Shared Documents/a.docx";

        var first = ColdStorageBlobKey.Build(site, url);
        var second = ColdStorageBlobKey.Build(site, url);

        Assert.Equal(first, second);
        Assert.Equal("contoso.sharepoint.com/sites/x/Shared Documents/a.docx", first);
    }

    [Fact]
    public void Build_LowercasesHost_ButPreservesPathCase()
    {
        var key = ColdStorageBlobKey.Build(
            "https://CONTOSO.SharePoint.com/sites/X",
            "/sites/X/Shared Documents/Report.DOCX");

        Assert.Equal("contoso.sharepoint.com/sites/X/Shared Documents/Report.DOCX", key);
    }

    [Fact]
    public void Build_FallsBackToBarePath_WhenSiteUrlIsNotAbsolute()
    {
        var key = ColdStorageBlobKey.Build("not-a-url", "/sites/x/y.docx");
        Assert.Equal("sites/x/y.docx", key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Build_Throws_WhenServerRelativeUrlMissing(string? serverRelativeUrl)
        => Assert.Throws<ArgumentException>(
            () => ColdStorageBlobKey.Build("https://contoso.sharepoint.com", serverRelativeUrl!));
}
