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

    private sealed class FakeExclusionSource(params ArchiveExclusion[] exclusions) : IArchiveExclusionSource
    {
        private readonly IReadOnlyList<ArchiveExclusion> _exclusions = exclusions;
        public Task<IReadOnlyList<ArchiveExclusion>> GetActiveExclusionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_exclusions);
    }

    [Fact]
    public async Task SiteExclusion_SkipsItemsInThatSite_WithReason()
    {
        var source = new FakeExclusionSource(new ArchiveExclusion("https://contoso.sharepoint.com/sites/Direction", null));
        var sut = new ArchiveEligibilityEvaluator(0, null, null, source);

        var candidate = new ArchiveCandidate
        {
            SiteUrl = "https://contoso.sharepoint.com/sites/Direction",
            ServerRelativeUrl = "/sites/Direction/Shared Documents/plan.docx",
            FileSizeBytes = 5_000_000,
        };
        var result = await sut.EvaluateAsync(candidate);
        Assert.False(result.IsEligible);
        Assert.Contains("excluded scope", result.SkipReason);
    }

    [Theory]
    [InlineData("/sites/Contoso/Legal Documents/case.docx", false)]
    [InlineData("/sites/Contoso/Legal Documents", false)]
    [InlineData("/sites/Contoso/Legal Documents Archive/old.docx", true)] // sibling, segment-aware
    [InlineData("/sites/Contoso/Shared Documents/ok.docx", true)]
    public async Task PrefixExclusion_IsSegmentAware(string url, bool eligible)
    {
        var source = new FakeExclusionSource(new ArchiveExclusion(null, "/sites/Contoso/Legal Documents"));
        var sut = new ArchiveEligibilityEvaluator(0, null, null, source);

        var candidate = new ArchiveCandidate { ServerRelativeUrl = url, FileSizeBytes = 1000 };
        Assert.Equal(eligible, (await sut.EvaluateAsync(candidate)).IsEligible);
    }

    [Fact]
    public async Task FolderUnderExcludedScope_IsSkipped()
    {
        var source = new FakeExclusionSource(new ArchiveExclusion(null, "/sites/Contoso/Legal Documents"));
        var sut = new ArchiveEligibilityEvaluator(0, null, null, source);

        var folder = new ArchiveCandidate
        {
            ServerRelativeUrl = "/sites/Contoso/Legal Documents/2024",
            ItemKind = ColdStorageItemKind.Folder,
        };
        Assert.False((await sut.EvaluateAsync(folder)).IsEligible);
    }

    private sealed class FakeReadActivitySource(int? accessCount) : IFileReadActivitySource
    {
        public Task<int?> GetAccessCountAsync(ArchiveCandidate candidate, CancellationToken cancellationToken = default)
            => Task.FromResult(accessCount);
    }

    [Fact]
    public async Task HighReadActivity_AboveThreshold_IsSkipped()
    {
        var sut = new ArchiveEligibilityEvaluator(0, null, null, readActivitySource: new FakeReadActivitySource(500), maxAccessCount: 100);
        var result = await sut.EvaluateAsync(File("/x/popular.docx"));
        Assert.False(result.IsEligible);
        Assert.Contains("read activity", result.SkipReason);
    }

    [Fact]
    public async Task ReadActivity_AtOrBelowThreshold_IsEligible()
    {
        var sut = new ArchiveEligibilityEvaluator(0, null, null, readActivitySource: new FakeReadActivitySource(100), maxAccessCount: 100);
        Assert.True((await sut.EvaluateAsync(File("/x/a.docx"))).IsEligible);
    }

    [Fact]
    public async Task NoReadActivitySignal_DoesNotBlock()
    {
        var sut = new ArchiveEligibilityEvaluator(0, null, null, readActivitySource: new FakeReadActivitySource(null), maxAccessCount: 100);
        Assert.True((await sut.EvaluateAsync(File("/x/a.docx"))).IsEligible);
    }

    [Fact]
    public async Task ReadActivityRule_DisabledWhenThresholdZero()
    {
        var sut = new ArchiveEligibilityEvaluator(0, null, null, readActivitySource: new FakeReadActivitySource(99_999), maxAccessCount: 0);
        Assert.True((await sut.EvaluateAsync(File("/x/a.docx"))).IsEligible);
    }
}
