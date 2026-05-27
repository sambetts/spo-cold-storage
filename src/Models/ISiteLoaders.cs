using System.Text;

namespace Models;

public interface ISiteCollectionLoader<T>
{
    public Task<List<IWebLoader<T>>> GetWebs();
}

public interface IWebLoader<T>
{
    public Task<List<IListLoader<T>>> GetLists();
}

public interface IListLoader<T>
{
    public Task<PageResponse<T>> GetListItems(T? token);

    public string Title { get; set; }
    public Guid ListId { get; set; }
}

public class PageResponse<T> : BaseSiteCrawlContents
{
    public T? NextPageToken { get; set; } = default;
}

public class BaseSiteCrawlContents
{
    public List<SharePointFileInfoWithList> FilesFound { get; set; } = [];

    public List<string> FoldersFound { get; set; } = [];
}

public class SiteCrawlContentsAndStats : BaseSiteCrawlContents
{
    public int IgnoredFiles { get; set; } = 0;
}
