using Models;

namespace Tests;

public abstract class BaseMockLoader(int itemsPerPage, int pages)
{
    public int ItemsPerPage { get; set; } = itemsPerPage;
    public int Pages { get; set; } = pages;
}
public class MockSiteLoader(int itemsPerPage, int pages) : BaseMockLoader(itemsPerPage, pages), ISiteCollectionLoader<int?>
{
    public Task<List<IWebLoader<int?>>> GetWebs()
    {
        var list = new List<IWebLoader<int?>>
        {
            new MockWebLoader(ItemsPerPage, Pages)
        };
        return Task.FromResult(list);
    }
}

public class MockWebLoader(int itemsPerPage, int pages) : BaseMockLoader(itemsPerPage, pages), IWebLoader<int?>
{
    public Task<List<IListLoader<int?>>> GetLists()
    {
        return Task.FromResult(new List<IListLoader<int?>>() { new MockListLoader(ItemsPerPage, Pages) });
    }
}

public class MockListLoader(int itemsPerPage, int pages) : BaseMockLoader(itemsPerPage, pages), IListLoader<int?>
{
    public string Title { get; set; } = "Mock list";
    public Guid ListId { get; set; } = Guid.Empty;

    public Task<PageResponse<int?>> GetListItems(int? page)
    {
        // Generate fake data depending on mock config
        var result = new PageResponse<int?>();
        int wantedPage = 1;
        if (page.HasValue)
        {
            wantedPage = page.Value;
        }

        for (int i = 0; i < ItemsPerPage; i++)
        {
            var siteRoot = $"https://m365x352268.sharepoint.com";
            var siteRelativeUrl = $"{siteRoot}/page{wantedPage}";
            var siteFQDN = $"{siteRoot}{siteRelativeUrl}";
            result.FilesFound.Add(new SharePointFileInfoWithList
            {
                ServerRelativeFilePath = $"{siteRelativeUrl}/subweb1/file" + i,
                SiteUrl = siteFQDN,
                WebUrl = $"{siteFQDN}/subweb1",
                LastModified = DateTime.Now
            });

            result.FoldersFound.Add($"Folder {i}, page {page}");
        }
        if (wantedPage < Pages)
        {
            result.NextPageToken = ++wantedPage;
        }
        else
        {
            result.NextPageToken = null;
        }

        return Task.FromResult(result);
    }
}
