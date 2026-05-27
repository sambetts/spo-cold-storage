using Models;
using Xunit;

namespace Tests;

public class ModelTests
{
    [Fact]
    public void SharePointFileInfoTests()
    {
        var emptyMsg1 = new BaseSharePointFileInfo { };
        Assert.False(emptyMsg1.IsValidInfo);

        var halfEmptyMsg = new BaseSharePointFileInfo { ServerRelativeFilePath = "/subweb1/whatever.txt" };
        Assert.False(halfEmptyMsg.IsValidInfo);

        // File path doesn't contain web
        var invalidMsg1 = new BaseSharePointFileInfo
        {
            ServerRelativeFilePath = "/whatever",
            SiteUrl = "https://m365x352268.sharepoint.com",
            WebUrl = "https://m365x352268.sharepoint.com/subweb1",
            LastModified = DateTime.Now
        };
        Assert.False(invalidMsg1.IsValidInfo);

        // Trailing slashes
        var invalidMsg2 = new BaseSharePointFileInfo
        {
            ServerRelativeFilePath = "/whatever",
            SiteUrl = "https://m365x352268.sharepoint.com/",
            WebUrl = "https://m365x352268.sharepoint.com/subweb1/",
            LastModified = DateTime.Now
        };
        Assert.False(invalidMsg2.IsValidInfo);

        // Missing start slash on file path
        var invalidMsg3 = new BaseSharePointFileInfo
        {
            ServerRelativeFilePath = "subweb1/whatever",
            SiteUrl = "https://m365x352268.sharepoint.com",
            WebUrl = "https://m365x352268.sharepoint.com/subweb1",
            LastModified = DateTime.Now
        };
        Assert.False(invalidMsg3.IsValidInfo);

        // Valid test; no folders
        var validMsg1 = new BaseSharePointFileInfo
        {
            ServerRelativeFilePath = "/subweb1/whatever",
            SiteUrl = "https://m365x352268.sharepoint.com",
            WebUrl = "https://m365x352268.sharepoint.com/subweb1",
            LastModified = DateTime.Now
        };
        Assert.True(validMsg1.IsValidInfo);
        Assert.Equal("https://m365x352268.sharepoint.com/subweb1/whatever", validMsg1.FullSharePointUrl);

        // Invalid folder - has leading/trailing slashes
        var invalidMsg4 = new BaseSharePointFileInfo
        {
            ServerRelativeFilePath = "/subweb1/whatever",
            Subfolder = "/sub1/sub2",
            SiteUrl = "https://m365x352268.sharepoint.com",
            WebUrl = "https://m365x352268.sharepoint.com/subweb1",
            LastModified = DateTime.Now
        };
        Assert.False(invalidMsg4.IsValidInfo);
    }

    [Fact]
    public void SiteFolderConfigTests()
    {
        var cfg = new SiteListFilterConfig()
        {
            ListFilterConfig =
                        [
                            new ListFolderConfig{ ListTitle = "Documents" },
                            new ListFolderConfig{ ListTitle = "Custom List",
                                FolderWhiteList = ["Subfolder", "Subfolder/Another subfolder"] }
                        ]
        };
        Assert.True(cfg.IncludeListInMigration("Documents"));
        Assert.False(cfg.IncludeListInMigration("Docs"));

        Assert.True(cfg.IncludeFolderInMigration("Custom List", "Subfolder"));
        Assert.True(cfg.IncludeFolderInMigration("Custom List", "Subfolder/Another subfolder"));
        Assert.False(cfg.IncludeFolderInMigration("Custom List", "Some other folder"));

        // Root folder not included if whitelist has items (without root in list)
        Assert.False(cfg.IncludeFolderInMigration("Custom List", ""));

        // Root folder is included if whitelist has no items
        Assert.True(cfg.IncludeFolderInMigration("Documents", ""));

        // No config set
        Assert.True(new SiteListFilterConfig().IncludeListInMigration("Documents"));
        Assert.True(new SiteListFilterConfig().IncludeFolderInMigration("Documents2", "whatever"));
    }
}
