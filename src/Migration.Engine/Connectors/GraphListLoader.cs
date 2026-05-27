using Microsoft.Graph;
using Microsoft.Graph.Models;
using Models;
using Microsoft.Extensions.Logging;

namespace Migration.Engine.Connectors;

/// <summary>
/// Graph API-based list loader with pagination support
/// Replaces SPOListLoader to eliminate SharePoint API permission requirement
/// Uses @odata.nextLink for pagination instead of ListItemCollectionPosition
/// </summary>
public class GraphListLoader : BaseGraphChildLoader, IListLoader<string>
{
    private readonly string _siteId;
    private readonly string _listId;
    private readonly string _webUrl;
    private SiteList? _listModel;

    public GraphListLoader(string siteId, string listId, string listTitle, string webUrl, BaseGraphConnector parent)
        : base(parent)
    {
        _siteId = siteId ?? throw new ArgumentNullException(nameof(siteId));
        _listId = listId ?? throw new ArgumentNullException(nameof(listId));
        Title = listTitle ?? throw new ArgumentNullException(nameof(listTitle));
        _webUrl = webUrl ?? throw new ArgumentNullException(nameof(webUrl));
        
        // Parse GUID from string ID if needed
        if (Guid.TryParse(_listId, out var guid))
        {
            ListId = guid;
        }
    }

    public string Title { get; set; }
    public Guid ListId { get; set; }

    /// <summary>
    /// Gets list items with pagination support
    /// Uses Graph API: GET /sites/{site-id}/lists/{list-id}/items
    /// Token parameter is the @odata.nextLink URL for pagination
    /// </summary>
    public async Task<PageResponse<string>> GetListItems(string? nextLink)
    {
        var pageResults = new PageResponse<string>();
        var client = await Parent.GraphClientManager.GetOrRefreshClient();

        try
        {
            ListItemCollectionResponse? itemsResponse;

            if (string.IsNullOrEmpty(nextLink))
            {
                // First page - use standard query
                Parent.Logger.LogInformation($"Fetching first page of items from list '{Title}' (ID: {_listId})");
                
                itemsResponse = await client.Sites[_siteId].Lists[_listId].Items.GetAsync(requestConfig =>
                {
                    // Expand fields to get all metadata and driveItem for file info
                    requestConfig.QueryParameters.Expand = new[] { "fields", "driveItem" };
                    requestConfig.QueryParameters.Top = 5000;  // Match CSOM's 5000 row limit
                });
            }
            else
            {
                // Subsequent pages - use nextLink
                Parent.Logger.LogInformation($"Fetching next page of items from list '{Title}'");
                
                // Graph SDK can follow nextLink automatically
                itemsResponse = await client.Sites[_siteId].Lists[_listId].Items.WithUrl(nextLink).GetAsync();
            }

            if (itemsResponse?.Value != null)
            {
                // Initialize list model on first page
                if (_listModel == null)
                {
                    await InitializeListModel(itemsResponse.Value.FirstOrDefault());
                }

                // Process each item
                foreach (var item in itemsResponse.Value)
                {
                    ProcessListItem(item, pageResults);
                }

                // Set next page token
                pageResults.NextPageToken = itemsResponse.OdataNextLink;

                Parent.Logger.LogInformation(
                    $"Processed {itemsResponse.Value.Count} items from list '{Title}'. " +
                    $"Has more pages: {!string.IsNullOrEmpty(pageResults.NextPageToken)}");
            }
        }
        catch (Exception ex)
        {
            Parent.Logger.LogError(ex, $"Error loading items from list '{Title}' (ID: {_listId})");
            throw;
        }

        return pageResults;
    }

    /// <summary>
    /// Initializes the list model based on the first item's metadata
    /// </summary>
    private async Task InitializeListModel(ListItem? firstItem)
    {
        try
        {
            var client = await Parent.GraphClientManager.GetOrRefreshClient();
            
            // Get list metadata to determine if it's a document library
            var list = await client.Sites[_siteId].Lists[_listId].GetAsync(requestConfig =>
            {
                requestConfig.QueryParameters.Expand = new[] { "drive" };
            });

            if (list?.Drive != null)
            {
                // It's a document library
                _listModel = new DocLib
                {
                    Title = Title,
                    DriveId = list.Drive.Id ?? string.Empty,
                    ServerRelativeUrl = ExtractServerRelativeUrl(_webUrl, Title)
                };
                Parent.Logger.LogInformation($"List '{Title}' is a document library (Drive ID: {list.Drive.Id})");
            }
            else
            {
                // Generic list
                _listModel = new SiteList
                {
                    Title = Title,
                    ServerRelativeUrl = ExtractServerRelativeUrl(_webUrl, Title)
                };
                Parent.Logger.LogInformation($"List '{Title}' is a generic list");
            }
        }
        catch (Exception ex)
        {
            Parent.Logger.LogWarning(ex, $"Could not determine list type for '{Title}', defaulting to SiteList");
            _listModel = new SiteList
            {
                Title = Title,
                ServerRelativeUrl = ExtractServerRelativeUrl(_webUrl, Title)
            };
        }
    }

    /// <summary>
    /// Processes a single list item and adds files to the results
    /// </summary>
    private void ProcessListItem(ListItem item, PageResponse<string> pageResults)
    {
        try
        {
            // Check if it's a folder
            var contentType = item.Fields?.AdditionalData.ContainsKey("ContentType") == true 
                ? item.Fields.AdditionalData["ContentType"]?.ToString() 
                : null;
            var isFolder = contentType?.Contains("Folder") ?? false;

            if (isFolder)
            {
                // Track folder
                var folderUrl = item.Fields?.AdditionalData.ContainsKey("FileRef") == true 
                    ? item.Fields.AdditionalData["FileRef"]?.ToString() 
                    : null;
                if (!string.IsNullOrEmpty(folderUrl))
                {
                    pageResults.FoldersFound.Add(folderUrl);
                }
                return;
            }

            // Process as file
            if (_listModel is DocLib docLib)
            {
                // Document library item
                var fileInfo = ProcessDocLibItem(item, docLib);
                if (fileInfo != null)
                {
                    pageResults.FilesFound.Add(fileInfo);
                }
            }
            else
            {
                // Generic list item with attachments
                var attachments = ProcessListItemAttachments(item, _listModel!);
                pageResults.FilesFound.AddRange(attachments);
            }
        }
        catch (Exception ex)
        {
            Parent.Logger.LogWarning(ex, $"Error processing item in list '{Title}'");
        }
    }

    /// <summary>
    /// Process a document library item
    /// </summary>
    private DriveItemSharePointFileInfo? ProcessDocLibItem(ListItem item, DocLib docLib)
    {
        if (item.DriveItem == null || item.Fields?.AdditionalData == null)
        {
            return null;
        }

        try
        {
            var fields = item.Fields.AdditionalData;
            
            // Extract file metadata
            var fileName = item.DriveItem.Name;
            var fileUrl = fields.ContainsKey("FileRef") ? fields["FileRef"]?.ToString() : null;
            var fileSize = item.DriveItem.Size ?? 0;
            var modified = item.DriveItem.LastModifiedDateTime?.UtcDateTime ?? DateTime.UtcNow;
            var created = item.DriveItem.CreatedDateTime?.UtcDateTime;
            
            // Get author info
            var editorField = fields.ContainsKey("Editor") ? fields["Editor"] : null;
            var author = ExtractAuthorEmail(editorField);

            // Get directory info
            var dirPath = fields.ContainsKey("FileDirRef") ? fields["FileDirRef"]?.ToString() ?? string.Empty : string.Empty;
            var subfolder = ExtractSubfolder(dirPath, docLib.ServerRelativeUrl);

            if (string.IsNullOrEmpty(fileUrl))
            {
                return null;
            }

            return new DriveItemSharePointFileInfo
            {
                Author = author,
                ServerRelativeFilePath = fileUrl,
                LastModified = modified,
                CreatedDate = created,
                DirectoryPath = dirPath,
                WebUrl = _webUrl,
                SiteUrl = ExtractSiteUrl(_webUrl),
                Subfolder = subfolder,
                GraphItemId = item.DriveItem.Id ?? string.Empty,
                DriveId = docLib.DriveId ?? string.Empty,
                List = docLib,
                FileSize = fileSize
            };
        }
        catch (Exception ex)
        {
            Parent.Logger.LogWarning(ex, $"Error processing document library item in list '{Title}'");
            return null;
        }
    }

    /// <summary>
    /// Process list item attachments (for generic lists)
    /// </summary>
    private List<SharePointFileInfoWithList> ProcessListItemAttachments(ListItem item, SiteList listModel)
    {
        var attachments = new List<SharePointFileInfoWithList>();

        // TODO: Implement attachment processing if needed
        // Graph API has different attachment handling than CSOM
        // May need to call /items/{item-id}/attachments endpoint

        return attachments;
    }

    #region Helper Methods

    private string ExtractServerRelativeUrl(string webUrl, string listTitle)
    {
        try
        {
            var uri = new Uri(webUrl);
            return $"{uri.AbsolutePath}/{listTitle}".Replace("//", "/");
        }
        catch
        {
            return $"/{listTitle}";
        }
    }

    private string ExtractSiteUrl(string webUrl)
    {
        try
        {
            var uri = new Uri(webUrl);
            // Get site collection URL (up to /sites/sitename)
            var pathParts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Length >= 2 && pathParts[0] == "sites")
            {
                return $"{uri.Scheme}://{uri.Host}/sites/{pathParts[1]}";
            }
            return $"{uri.Scheme}://{uri.Host}";
        }
        catch
        {
            return webUrl;
        }
    }

    private string ExtractSubfolder(string dirPath, string listPath)
    {
        if (string.IsNullOrEmpty(dirPath) || string.IsNullOrEmpty(listPath))
        {
            return string.Empty;
        }

        if (dirPath.StartsWith(listPath))
        {
            return dirPath[listPath.Length..].TrimStart('/');
        }

        return dirPath.TrimEnd('/');
    }

    private string ExtractAuthorEmail(object? editorField)
    {
        if (editorField == null)
        {
            return "Unknown";
        }

        try
        {
            // Graph API returns editor as a complex object or string
            // Try to extract email or name
            var editorStr = editorField.ToString();
            if (!string.IsNullOrEmpty(editorStr))
            {
                // May need to parse JSON object if it's a complex type
                return editorStr;
            }
        }
        catch
        {
            // Ignore
        }

        return "Unknown";
    }

    #endregion
}
