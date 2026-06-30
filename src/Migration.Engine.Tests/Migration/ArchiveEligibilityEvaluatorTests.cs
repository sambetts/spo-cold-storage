using Migration.Engine.Migration;
using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Migration;

/// <summary>
/// Covers the configurable archive-eligibility rules from issue #2: minimum
/// file size plus include/exclude file-type lists, with a clear skip reason.
/// </summary>
public class ArchiveEligibilityEvaluatorTests
{
    private static ArchiveCandidate File(string url, long size = 1_000_000)
        => new() { ServerRelativeUrl = url, FileSizeBytes = size, ItemKind = ColdStorageItemKind.File };

    [Fact]
    public async Task NoRulesConfigured_EverythingEligible()
    {
        var sut = new ArchiveEligibilityEvaluator(0, null, null);
        var result = await sut.EvaluateAsync(File("/sites/x/Shared Documents/a.docx"));
        Assert.True(result.IsEligible);
        Assert.Null(result.SkipReason);
    }

    [Fact]
    public async Task BelowMinimumSize_IsSkipped_WithReason()
    {
        var sut = new ArchiveEligibilityEvaluator(minSizeBytes: 10_240, null, null);
        var result = await sut.EvaluateAsync(File("/sites/x/tiny.docx", size: 500));
        Assert.False(result.IsEligible);
        Assert.Contains("minimum archive size", result.SkipReason);
    }

    [Fact]
    public async Task AtOrAboveMinimumSize_IsEligible()
    {
        var sut = new ArchiveEligibilityEvaluator(minSizeBytes: 10_240, null, null);
        Assert.True((await sut.EvaluateAsync(File("/x/a.docx", size: 10_240))).IsEligible);
        Assert.True((await sut.EvaluateAsync(File("/x/a.docx", size: 999_999))).IsEligible);
    }

    [Fact]
    public async Task UnknownSize_NotSkippedOnSizeRule()
    {
        var sut = new ArchiveEligibilityEvaluator(minSizeBytes: 10_240, null, null);
        var result = await sut.EvaluateAsync(File("/x/a.docx", size: 0));
        Assert.True(result.IsEligible);
    }

    [Theory]
    [InlineData(".tmp,.ds_store", "/x/scratch.tmp", false)]
    [InlineData("tmp; ds_store", "/x/scratch.TMP", false)]
    [InlineData(".tmp", "/x/keep.docx", true)]
    public async Task ExcludeList_SkipsMatchingExtensions(string excluded, string url, bool eligible)
    {
        var sut = new ArchiveEligibilityEvaluator(0, excluded, null);
        var result = await sut.EvaluateAsync(File(url));
        Assert.Equal(eligible, result.IsEligible);
    }

    [Theory]
    [InlineData(".docx,.pptx", "/x/a.docx", true)]
    [InlineData(".docx,.pptx", "/x/a.zip", false)]
    [InlineData(".docx", "/x/noext", false)]
    public async Task IncludeList_OnlyAllowsListedExtensions(string included, string url, bool eligible)
    {
        var sut = new ArchiveEligibilityEvaluator(0, null, included);
        var result = await sut.EvaluateAsync(File(url));
        Assert.Equal(eligible, result.IsEligible);
    }

    [Fact]
    public async Task Folders_AreAlwaysEligible_RulesApplyToExpandedFiles()
    {
        var sut = new ArchiveEligibilityEvaluator(minSizeBytes: 10_240, ".tmp", null);
        var folder = new ArchiveCandidate { ServerRelativeUrl = "/x/Folder", ItemKind = ColdStorageItemKind.Folder, FileSizeBytes = 0 };
        Assert.True((await sut.EvaluateAsync(folder)).IsEligible);
    }
}
