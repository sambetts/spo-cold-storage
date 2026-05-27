using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migration.Engine;
using Models;
using Xunit;

namespace Tests;

public class MockCrawlTests
{
    private readonly ILogger _logger = NullLogger.Instance;
    private readonly List<SharePointFileInfoWithList> _foundFiles = [];

    [Fact]
    public async Task MockCrawl()
    {
        const int PAGE_COUNT = 100;
        const int PAGES = 5;
        var loader = new MockSiteLoader(PAGE_COUNT, PAGES);

        var crawl = new SiteListsAndLibrariesCrawler<int?>(loader, _logger);
        await crawl.StartSiteCrawl(new SiteListFilterConfig(),
            (SharePointFileInfoWithList foundFile) => Crawler_SharePointFileFound(foundFile),
            () => CrawlComplete());

        Assert.Equal(PAGE_COUNT * PAGES, _foundFiles.Count);
        foreach (var ff in _foundFiles)
        {
            Assert.Single(_foundFiles, f => f.FullSharePointUrl == ff.FullSharePointUrl);
        }
    }

    private void CrawlComplete()
    {
        // Nowt
    }

    private Task Crawler_SharePointFileFound(SharePointFileInfoWithList foundFile)
    {
        _foundFiles.Add(foundFile);
        return Task.CompletedTask;
    }
}
