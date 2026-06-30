using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

/// <summary>
/// Foundations for version-history archival (issue #18): the manifest JSON
/// round-trip and the deterministic per-version blob layout.
/// </summary>
public class VersionHistoryTests
{
    [Fact]
    public void Manifest_RoundTrips()
    {
        var manifest = new VersionManifest
        {
            Versions =
            {
                new ArchivedVersion { VersionId = "1.0", BlobPath = "host/x/a.docx.versions/1.0", Size = 100, LastModifiedUtc = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new ArchivedVersion { VersionId = "2.0", BlobPath = "host/x/a.docx.versions/2.0", Size = 200, LastModifiedUtc = new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
            },
        };

        var parsed = VersionManifest.TryParse(manifest.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Count);
        Assert.Equal("1.0", parsed.Versions[0].VersionId);
        Assert.Equal("host/x/a.docx.versions/2.0", parsed.Versions[1].BlobPath);
        Assert.Equal(200, parsed.Versions[1].Size);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData(null)]
    public void Manifest_TryParse_ReturnsNull_OnBadInput(string? json)
        => Assert.Null(VersionManifest.TryParse(json));

    [Fact]
    public void VersionBlobLayout_IsDeterministic_AndSibling()
    {
        const string baseKey = "contoso.sharepoint.com/sites/x/Shared Documents/a.docx";
        Assert.Equal($"{baseKey}.versions/1.0", VersionBlobLayout.ForVersion(baseKey, "1.0"));
        Assert.Equal($"{baseKey}.versions.json", VersionBlobLayout.ManifestKey(baseKey));
    }

    [Fact]
    public void VersionBlobLayout_SanitizesSlashesInVersionId()
    {
        var key = VersionBlobLayout.ForVersion("base", "_vti_history/512/x");
        Assert.DoesNotContain("/512/", key);
        Assert.StartsWith("base.versions/", key);
    }

    [Fact]
    public void Placeholder_RoundTrips_VersionCount()
    {
        var original = new PlaceholderFileMetadata
        {
            ContainerName = "c",
            BlobPath = "p",
            OriginalServerRelativeUrl = "/sites/x/a.docx",
            BlobUrl = "https://e/c/p",
            VersionCount = 7,
        };
        var parsed = PlaceholderFileMetadata.TryParse(original.BuildUrlFileContent());
        Assert.NotNull(parsed);
        Assert.Equal(7, parsed!.VersionCount);
    }
}
