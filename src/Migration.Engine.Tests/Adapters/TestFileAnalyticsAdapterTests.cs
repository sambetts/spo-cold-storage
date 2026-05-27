using AwesomeAssertions;
using Migration.Engine.SnapshotBuilder;
using Migration.Engine.Tests.Adapters;
using Models;
using Xunit;

namespace Migration.Engine.Tests.Adapters;

public class TestFileAnalyticsAdapterTests
{
    [Fact]
    public async Task GetFileAnalyticsAsync_WithConfiguredData_ReturnsExpectedResults()
    {
        // Arrange
        var adapter = new TestFileAnalyticsAdapter();
        var expectedStats = new ItemAnalyticsResponse.AnalyticsItemActionStat
        {
            ActionCount = 100,
            ActorCount = 50
        };
        adapter.SetAnalyticsData("test-id", expectedStats);

        var file = new DocumentSiteWithMetadata(new DriveItemSharePointFileInfo
        {
            GraphItemId = "test-id",
            DriveId = "drive-1",
            SiteUrl = "https://test.com",
            WebUrl = "https://test.com",
            ServerRelativeFilePath = "/file.docx"
        });

        var files = new List<DocumentSiteWithMetadata> { file };

        // Act
        var result = await adapter.GetFileAnalyticsAsync(files);

        // Assert
        result.Should().NotBeNull();
        result.UpdateResults.Should().ContainKey(file);
        var response = result.UpdateResults[file] as ItemAnalyticsResponse;
        response.Should().NotBeNull();
        response!.AccessStats.Should().NotBeNull();
        response.AccessStats!.ActionCount.Should().Be(100);
        response.AccessStats.ActorCount.Should().Be(50);
    }

    [Fact]
    public async Task GetFileAnalyticsAsync_WithoutConfiguredData_ReturnsDefaultResults()
    {
        // Arrange
        var adapter = new TestFileAnalyticsAdapter();
        var file = new DocumentSiteWithMetadata(new DriveItemSharePointFileInfo
        {
            GraphItemId = "unknown-id",
            DriveId = "drive-1",
            SiteUrl = "https://test.com",
            WebUrl = "https://test.com",
            ServerRelativeFilePath = "/file.docx"
        });

        var files = new List<DocumentSiteWithMetadata> { file };

        // Act
        var result = await adapter.GetFileAnalyticsAsync(files);

        // Assert
        result.Should().NotBeNull();
        result.UpdateResults.Should().ContainKey(file);
        file.State.Should().Be(SiteFileAnalysisState.Complete);
    }

    [Fact]
    public async Task GetFileVersionHistoryAsync_WithConfiguredData_ReturnsExpectedVersions()
    {
        // Arrange
        var adapter = new TestFileAnalyticsAdapter();
        var versionInfo = new DriveItemVersionInfo
        {
            Versions = 
            [
                new DriveItemVersion { Id = "1.0", Size = 1024 },
                new DriveItemVersion { Id = "2.0", Size = 2048 },
                new DriveItemVersion { Id = "3.0", Size = 3072 }
            ]
        };
        adapter.SetVersionData("test-id", versionInfo);

        var file = new DocumentSiteWithMetadata(new DriveItemSharePointFileInfo
        {
            GraphItemId = "test-id",
            DriveId = "drive-1",
            SiteUrl = "https://test.com",
            WebUrl = "https://test.com",
            ServerRelativeFilePath = "/file.docx"
        });

        var files = new List<DocumentSiteWithMetadata> { file };

        // Act
        var result = await adapter.GetFileVersionHistoryAsync(files);

        // Assert
        result.Should().NotBeNull();
        result.UpdateResults.Should().ContainKey(file);
        var versions = result.UpdateResults[file] as DriveItemVersionInfo;
        versions.Should().NotBeNull();
        versions!.Versions.Should().HaveCount(3);
        versions.Versions[0].Id.Should().Be("1.0");
        versions.Versions[2].Size.Should().Be(3072);
    }

    [Fact]
    public async Task ShouldSkipFileAnalysisAsync_WithConfiguredSkipFile_ReturnsTrue()
    {
        // Arrange
        var adapter = new TestFileAnalyticsAdapter();
        var fileInfo = new DriveItemSharePointFileInfo
        {
            GraphItemId = "test-id",
            DriveId = "drive-1",
            SiteUrl = "https://test.sharepoint.com/sites/test",
            WebUrl = "https://test.sharepoint.com/sites/test",
            ServerRelativeFilePath = "/sites/test/file.docx"
        };
        
        // FullSharePointUrl will be calculated from these properties
        adapter.SetFileToSkip(fileInfo.FullSharePointUrl);

        // Act
        var result = await adapter.ShouldSkipFileAnalysisAsync(fileInfo, 24);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ShouldSkipFileAnalysisAsync_WithoutConfiguredSkipFile_ReturnsFalse()
    {
        // Arrange
        var adapter = new TestFileAnalyticsAdapter();
        var fileInfo = new DriveItemSharePointFileInfo
        {
            GraphItemId = "test-id",
            DriveId = "drive-1",
            SiteUrl = "https://test.com",
            WebUrl = "https://test.com",
            ServerRelativeFilePath = "/file.docx"
        };

        // Act
        var result = await adapter.ShouldSkipFileAnalysisAsync(fileInfo, 24);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Adapter_TracksCallCounts_Correctly()
    {
        // Arrange
        var adapter = new TestFileAnalyticsAdapter();
        var file = new DocumentSiteWithMetadata(new DriveItemSharePointFileInfo
        {
            GraphItemId = "test-id",
            DriveId = "drive-1",
            SiteUrl = "https://test.com",
            WebUrl = "https://test.com",
            ServerRelativeFilePath = "/file.docx"
        });
        var files = new List<DocumentSiteWithMetadata> { file };

        var fileInfo = new DriveItemSharePointFileInfo
        {
            GraphItemId = "test-id",
            DriveId = "drive-1",
            SiteUrl = "https://test.com",
            WebUrl = "https://test.com",
            ServerRelativeFilePath = "/file.docx"
        };

        // Act
        await adapter.GetFileAnalyticsAsync(files);
        await adapter.GetFileAnalyticsAsync(files);
        await adapter.GetFileVersionHistoryAsync(files);
        await adapter.ShouldSkipFileAnalysisAsync(fileInfo, 24);
        await adapter.ShouldSkipFileAnalysisAsync(fileInfo, 24);
        await adapter.ShouldSkipFileAnalysisAsync(fileInfo, 24);

        // Assert
        adapter.AnalyticsCallCount.Should().Be(2);
        adapter.VersionCallCount.Should().Be(1);
        adapter.SkipCheckCount.Should().Be(3);
    }

    [Fact]
    public async Task ResetCounters_ResetsAllCounters_ToZero()
    {
        // Arrange
        var adapter = new TestFileAnalyticsAdapter();
        var file = new DocumentSiteWithMetadata(new DriveItemSharePointFileInfo
        {
            GraphItemId = "test-id",
            DriveId = "drive-1",
            SiteUrl = "https://test.com",
            WebUrl = "https://test.com",
            ServerRelativeFilePath = "/file.docx"
        });
        var files = new List<DocumentSiteWithMetadata> { file };

        // Act
        await adapter.GetFileAnalyticsAsync(files);
        await adapter.GetFileVersionHistoryAsync(files);
        adapter.ResetCounters();

        // Assert
        adapter.AnalyticsCallCount.Should().Be(0);
        adapter.VersionCallCount.Should().Be(0);
        adapter.SkipCheckCount.Should().Be(0);
    }

    [Fact]
    public async Task GetFileAnalyticsAsync_WithMultipleFiles_ProcessesAllFiles()
    {
        // Arrange
        var adapter = new TestFileAnalyticsAdapter();
        adapter.SetAnalyticsData("id-1", new ItemAnalyticsResponse.AnalyticsItemActionStat { ActionCount = 10 });
        adapter.SetAnalyticsData("id-2", new ItemAnalyticsResponse.AnalyticsItemActionStat { ActionCount = 20 });
        adapter.SetAnalyticsData("id-3", new ItemAnalyticsResponse.AnalyticsItemActionStat { ActionCount = 30 });

        var files = new List<DocumentSiteWithMetadata>
        {
            new(new DriveItemSharePointFileInfo { GraphItemId = "id-1", DriveId = "d1", SiteUrl = "https://test.com", WebUrl = "https://test.com", ServerRelativeFilePath = "/file1.docx" }),
            new(new DriveItemSharePointFileInfo { GraphItemId = "id-2", DriveId = "d2", SiteUrl = "https://test.com", WebUrl = "https://test.com", ServerRelativeFilePath = "/file2.docx" }),
            new(new DriveItemSharePointFileInfo { GraphItemId = "id-3", DriveId = "d3", SiteUrl = "https://test.com", WebUrl = "https://test.com", ServerRelativeFilePath = "/file3.docx" })
        };

        // Act
        var result = await adapter.GetFileAnalyticsAsync(files);

        // Assert
        result.UpdateResults.Should().HaveCount(3);
        files.Should().OnlyContain(f => f.State == SiteFileAnalysisState.Complete);
    }
}
