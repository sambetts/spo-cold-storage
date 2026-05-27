using Microsoft.Graph;
using Microsoft.Graph.Models;
using Entities.Configuration;
using Models;
using Microsoft.Extensions.Logging;

namespace Migration.Engine.Connectors;

/// <summary>
/// Graph API-based site collection loader
/// Replaces SPOSiteCollectionLoader to eliminate SharePoint API permission requirement
/// </summary>
public class GraphSiteCollectionLoader : BaseGraphConnector, ISiteCollectionLoader<string>
{
    private readonly string _siteUrl;
    private readonly Config _config;
    private string? _siteId;

    public GraphSiteCollectionLoader(Config config, string siteUrl, ILogger logger) 
        : base(new GraphClientManager(config, logger), logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _siteUrl = siteUrl ?? throw new ArgumentNullException(nameof(siteUrl));
    }

    /// <summary>
    /// Gets the site ID from the site URL
    /// </summary>
    private async Task<string> GetSiteIdAsync()
    {
        if (string.IsNullOrEmpty(_siteId))
        {
            var client = await GraphClientManager.GetOrRefreshClient();
            
            try
            {
                // Extract hostname and site path from URL
                var uri = new Uri(_siteUrl);
                var hostname = uri.Host;
                var sitePath = uri.AbsolutePath;

                Logger.LogInformation($"Resolving site ID for: {hostname}:{sitePath}");

                // Get site by URL
                var site = await client.Sites[$"{hostname}:{sitePath}"].GetAsync();
                
                if (site?.Id != null)
                {
                    _siteId = site.Id;
                    Logger.LogInformation($"Resolved site ID: {_siteId}");
                }
                else
                {
                    throw new Exception($"Could not resolve site ID for URL: {_siteUrl}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Failed to resolve site ID for {_siteUrl}");
                throw;
            }
        }

        return _siteId;
    }

    /// <summary>
    /// Gets all webs (subsites) in the site collection
    /// Uses Graph API: GET /sites/{site-id}/sites
    /// </summary>
    public async Task<List<IWebLoader<string>>> GetWebs()
    {
        var webs = new List<IWebLoader<string>>();
        var client = await GraphClientManager.GetOrRefreshClient();
        var siteId = await GetSiteIdAsync();

        try
        {
            // Get the root site
            var rootSite = await client.Sites[siteId].GetAsync();
            if (rootSite != null)
            {
                Logger.LogInformation($"Processing root site: {rootSite.DisplayName}");
                webs.Add(new GraphWebLoader(rootSite.Id!, rootSite.WebUrl!, this));
            }

            // Get subsites
            Logger.LogInformation($"Fetching subsites for site: {siteId}");
            var subsites = await client.Sites[siteId].Sites.GetAsync(requestConfig =>
            {
                requestConfig.QueryParameters.Select = new[] { "id", "displayName", "webUrl", "name" };
            });

            if (subsites?.Value != null)
            {
                foreach (var subsite in subsites.Value)
                {
                    if (subsite.Id != null && subsite.WebUrl != null)
                    {
                        Logger.LogInformation($"Found subsite: {subsite.DisplayName} ({subsite.WebUrl})");
                        webs.Add(new GraphWebLoader(subsite.Id, subsite.WebUrl, this));
                    }
                }
            }

            Logger.LogInformation($"Found {webs.Count} web(s) total");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"Error loading webs for site {siteId}");
            throw;
        }

        return webs;
    }
}
