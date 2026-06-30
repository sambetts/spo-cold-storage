using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

public class PlaceholderFileTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = new PlaceholderFileMetadata
        {
            JobId = Guid.NewGuid(),
            ContainerName = "archive-2024",
            BlobPath = "sites/contoso/Shared Documents/Q1 Plan.pptx",
            BlobUrl = "https://stcontoso.blob.core.windows.net/archive-2024/sites/contoso/Shared%20Documents/Q1%20Plan.pptx",
            OriginalSiteUrl = "https://contoso.sharepoint.com/sites/finance",
            OriginalWebUrl = "https://contoso.sharepoint.com/sites/finance",
            OriginalServerRelativeUrl = "/sites/finance/Shared Documents/Q1 Plan.pptx",
            OriginalFileName = "Q1 Plan.pptx",
            OriginalFileSize = 24_700_419L,
            OriginalLastModified = new DateTime(2024, 3, 11, 9, 22, 5, DateTimeKind.Utc),
            OriginalCreatedBy = "Ada Lovelace",
            OriginalModifiedBy = "Alan Turing",
            OriginalCreated = new DateTime(2023, 1, 2, 8, 0, 0, DateTimeKind.Utc),
            ContentMd5Base64 = "1B2M2Y8AsgTpgAmY7PhCfg==",
            MigratedAt = new DateTime(2024, 6, 15, 17, 4, 18, DateTimeKind.Utc),
        };

        var serialised = original.BuildUrlFileContent();
        var parsed = PlaceholderFileMetadata.TryParse(serialised);

        Assert.NotNull(parsed);
        Assert.Equal(original.JobId, parsed!.JobId);
        Assert.Equal(original.ContainerName, parsed.ContainerName);
        Assert.Equal(original.BlobPath, parsed.BlobPath);
        Assert.Equal(original.BlobUrl, parsed.BlobUrl);
        Assert.Equal(original.OriginalSiteUrl, parsed.OriginalSiteUrl);
        Assert.Equal(original.OriginalWebUrl, parsed.OriginalWebUrl);
        Assert.Equal(original.OriginalServerRelativeUrl, parsed.OriginalServerRelativeUrl);
        Assert.Equal(original.OriginalFileName, parsed.OriginalFileName);
        Assert.Equal(original.OriginalFileSize, parsed.OriginalFileSize);
        Assert.Equal(original.OriginalLastModified, parsed.OriginalLastModified);
        Assert.Equal(original.OriginalCreatedBy, parsed.OriginalCreatedBy);
        Assert.Equal(original.OriginalModifiedBy, parsed.OriginalModifiedBy);
        Assert.Equal(original.OriginalCreated, parsed.OriginalCreated);
        Assert.Equal(original.ContentMd5Base64, parsed.ContentMd5Base64);
        Assert.Equal(original.MigratedAt, parsed.MigratedAt);
    }

    [Fact]
    public void BuildUrlFileContent_StartsWithInternetShortcut()
    {
        var content = new PlaceholderFileMetadata
        {
            BlobUrl = "https://example.blob.core.windows.net/c/path",
            ContainerName = "c",
            BlobPath = "path",
            OriginalServerRelativeUrl = "/sites/x/Y.docx",
        }.BuildUrlFileContent();

        Assert.StartsWith("[InternetShortcut]", content);
        Assert.Contains("URL=https://example.blob.core.windows.net/c/path", content);
        Assert.Contains("[ColdStorage]", content);
    }

    [Fact]
    public void TryParse_ReturnsNull_WhenContentIsEmpty()
    {
        Assert.Null(PlaceholderFileMetadata.TryParse(string.Empty));
        Assert.Null(PlaceholderFileMetadata.TryParse("   \r\n\t"));
        Assert.Null(PlaceholderFileMetadata.TryParse(null!));
    }

    [Fact]
    public void TryParse_ReturnsNull_WhenColdStorageSectionMissing()
    {
        const string content = "[InternetShortcut]\r\nURL=https://example.com/x\r\n";
        Assert.Null(PlaceholderFileMetadata.TryParse(content));
    }

    [Fact]
    public void TryParse_ReturnsNull_WhenRequiredFieldsMissing()
    {
        // Per requirements: incomplete/corrupted metadata should "fail safely",
        // i.e. TryParse returns null so the restore worker refuses to act.
        const string content = "[ColdStorage]\r\nContainerName=archive\r\nOriginalServerRelativeUrl=/sites/x/y.docx\r\n";
        Assert.Null(PlaceholderFileMetadata.TryParse(content));
    }

    [Fact]
    public void TryParse_IgnoresCommentsAndBlankLines()
    {
        var content =
            "; this is a comment\r\n" +
            "\r\n" +
            "[InternetShortcut]\r\n" +
            "URL=https://example.blob.core.windows.net/c/p\r\n" +
            "\r\n" +
            "[ColdStorage]\r\n" +
            "; another comment\r\n" +
            "ContainerName=c\r\n" +
            "BlobPath=p\r\n" +
            "OriginalServerRelativeUrl=/sites/x/y.docx\r\n";
        var parsed = PlaceholderFileMetadata.TryParse(content);
        Assert.NotNull(parsed);
        Assert.Equal("c", parsed!.ContainerName);
        Assert.Equal("p", parsed.BlobPath);
        Assert.Equal("/sites/x/y.docx", parsed.OriginalServerRelativeUrl);
    }

    [Fact]
    public void TryParse_LegacyPlaceholderWithoutAuthorFields_StillParses()
    {
        // Placeholders written before issue #1 have no author/editor/created keys.
        // They must still parse (fields default to empty) so existing archives stay restorable.
        const string content =
            "[InternetShortcut]\r\n" +
            "URL=https://example.blob.core.windows.net/c/p\r\n" +
            "[ColdStorage]\r\n" +
            "ContainerName=c\r\n" +
            "BlobPath=p\r\n" +
            "OriginalServerRelativeUrl=/sites/x/y.docx\r\n";
        var parsed = PlaceholderFileMetadata.TryParse(content);

        Assert.NotNull(parsed);
        Assert.Equal(string.Empty, parsed!.OriginalCreatedBy);
        Assert.Equal(string.Empty, parsed.OriginalModifiedBy);
        Assert.Equal(DateTime.MinValue, parsed.OriginalCreated);
    }
}

