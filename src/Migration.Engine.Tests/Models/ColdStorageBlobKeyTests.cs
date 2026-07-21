using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Models;

/// <summary>
/// Locks down the blob-key ↔ server-relative-URL mapping that blob-driven restore relies on to
/// recover a file's SharePoint destination straight from its cold-storage blob (independent of any
/// database row or surviving placeholder).
/// </summary>
public class ColdStorageBlobKeyTests
{
    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/finance", "/sites/finance/Shared Documents/report.docx")]
    [InlineData("https://m365x.sharepoint.com/sites/ColdStorage", "/sites/ColdStorage/Shared Documents/Microsoft365-Analytics-Insights/LICENSE")]
    [InlineData("https://m365x.sharepoint.com/sites/ColdStorage", "/sites/ColdStorage/Shared Documents/repo/.git/hooks/applypatch-msg.sample.url")]
    [InlineData("https://m365x.sharepoint.com/sites/ColdStorage", "/sites/ColdStorage/Shared Documents/repo/.vs/proj/CopilotIndices/18.0.927.32057/CodeChunks.db")]
    [InlineData("https://tenant.sharepoint.com", "/Shared Documents/root-site-file.txt")]
    public void Build_Then_Reverse_RoundTripsServerRelativeUrl(string siteUrl, string serverRelativeUrl)
    {
        var key = ColdStorageBlobKey.Build(siteUrl, serverRelativeUrl);

        // The key must carry the host as its first segment (the tenant discriminator).
        Assert.StartsWith(new System.Uri(siteUrl).Host.ToLowerInvariant() + "/", key);

        // ...and reverse back to the exact original server-relative URL (case + spaces preserved).
        Assert.Equal(serverRelativeUrl, ColdStorageBlobKey.ReverseServerRelativeUrl(key));
    }

    [Fact]
    public void Reverse_PreservesCaseAndSpaces()
    {
        const string blobKey = "host.sharepoint.com/sites/S/Shared Documents/Mixed Case Folder/File.TXT";
        Assert.Equal("/sites/S/Shared Documents/Mixed Case Folder/File.TXT", ColdStorageBlobKey.ReverseServerRelativeUrl(blobKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("host-only-no-path")]
    [InlineData("host/")]
    public void Reverse_ReturnsEmpty_WhenNoPathSegment(string blobKey)
    {
        Assert.Equal(string.Empty, ColdStorageBlobKey.ReverseServerRelativeUrl(blobKey));
    }
}
