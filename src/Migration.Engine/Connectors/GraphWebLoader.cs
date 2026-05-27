using Microsoft.Graph;
using Microsoft.Graph.Models;
using Models;
using Microsoft.Extensions.Logging;

namespace Migration.Engine.Connectors;

/// <summary>
/// Graph API-based web loader
/// Replaces SPOWebLoader to eliminate SharePoint API permission requirement
/// </summary>
public class GraphWebLoader : BaseGraphChildLoader, IWebLoader<string>
{
    private readonly string _siteId;
    private readonly string _webUrl;

    public GraphWebLoader(string siteId, string webUrl, BaseGraphConnector parent) 
        : base(parent)
    {
        _siteId = siteId ?? throw new ArgumentNullException(nameof(siteId));
        _webUrl = webUrl ?? throw new ArgumentNullException(nameof(webUrl));
    }

    public string SiteId => _siteId;
    public string WebUrl => _webUrl;

    /// <summary>
    /// Gets all lists in the web
    /// Uses Graph API: GET /sites/{site-id}/lists
    /// </summary>
    public async Task<List<IListLoader<string>>> GetLists()
    {
        var lists = new List<IListLoader<string>>();
        var client = await Parent.GraphClientManager.GetOrRefreshClient();

        try
        {
            Parent.Logger.LogInformation($"Fetching lists for site: {_siteId}");

            // Get all lists, filtering out hidden and system lists
            var listsResponse = await client.Sites[_siteId].Lists.GetAsync(requestConfig =>
            {
                requestConfig.QueryParameters.Select = new[] { "id", "displayName", "name", "list", "webUrl" };
                requestConfig.QueryParameters.Filter = "hidden eq false";
                requestConfig.QueryParameters.Expand = new[] { "drive" };
            });

            if (listsResponse?.Value != null)
            {
                foreach (var list in listsResponse.Value)
                {
                    // Additional filtering - skip system lists
                    // System lists typically have names starting with underscore or specific patterns
                    if (list.DisplayName != null && 
                        !list.DisplayName.StartsWith("_") &&
                        list.Name != "catalogsMasterPage" &&
                        list.Name != "webPageLibrary")
                    {
                        Parent.Logger.LogInformation($"Found list: '{list.DisplayName}' (ID: {list.Id})");
                        
                        if (list.Id != null)
                        {
                            lists.Add(new GraphListLoader(
                                _siteId, 
                                list.Id, 
                                list.DisplayName, 
                                _webUrl,
                                (BaseGraphConnector)Parent
                            ));
                        }
                    }
                    else
                    {
                        Parent.Logger.LogDebug($"Skipping system/hidden list: {list.DisplayName}");
                    }
                }
            }

            Parent.Logger.LogInformation($"Found {lists.Count} list(s) in site {_siteId}");
        }
        catch (Exception ex)
        {
            Parent.Logger.LogError(ex, $"Error loading lists for site {_siteId}");
            throw;
        }

        return lists;
    }
}
