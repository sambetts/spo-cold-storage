using Azure.Identity;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.SharePoint.Client;
using Entities;
using Entities.Configuration;
using Migration.Engine.Connectors;
using Migration.Engine.Migration;
using Migration.Engine.Utils;
using Models;

using Microsoft.Extensions.Logging;
namespace Migration.Engine;
/// <summary>
/// Finds files to migrate in a SharePoint site-collection
/// </summary>
public class SharePointContentIndexer : BaseComponent
{
    #region Constructors & Privates

    private readonly BlobServiceClient _blobServiceClient;
    private BlobContainerClient? _containerClient;
    private readonly SharePointFileMigrator _sharePointFileMigrator;

    public SharePointContentIndexer(Config config, ILogger ILogger) : base(config, ILogger)
    {
        var sbConnectionProps = ServiceBusConnectionStringProperties.Parse(_config.ConnectionStrings.ServiceBus);
        _logger.LogWarning($"Sending new SharePoint files to migrate to service-bus '{sbConnectionProps.Endpoint}'.");

        // Create BlobServiceClient with appropriate authentication based on connection string type
        _blobServiceClient = BlobServiceClientFactory.Create(_config.ConnectionStrings.Storage, _config);
        _sharePointFileMigrator = new SharePointFileMigrator(config, _logger);
    }

    #endregion

    public async Task StartMigrateAllSites()
    {
        // Create the container and return a container client object
        this._containerClient = _blobServiceClient.GetBlobContainerClient(_config.BlobContainerName);

        // Try to create container with no access to public
        // If container already exists or we don't have permission to create, continue anyway
        try
        {
            await _containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 403 || ex.Status == 409)
        {
            // 403: No permission to create container (assume it exists)
            // 409: Container already exists
            _logger.LogInformation($"Container '{_config.BlobContainerName}' not created (may already exist or insufficient permissions): {ex.Message}");

            // Verify we can at least access the container
            // Note: ExistsAsync also requires permissions, so handle 403 here too
            try
            {
                if (!await _containerClient.ExistsAsync())
                {
                    throw new InvalidOperationException($"Container '{_config.BlobContainerName}' does not exist and service principal does not have permission to create it. Please create the container manually or assign Storage Blob Data Contributor role.", ex);
                }
            }
            catch (Azure.RequestFailedException existsEx) when (existsEx.Status == 403)
            {
                // Service principal lacks RBAC permissions to check container existence
                // Assume container exists and log warning
                _logger.LogWarning($"Cannot verify container '{_config.BlobContainerName}' existence due to insufficient permissions. Assuming container exists. Please ensure service principal has 'Storage Blob Data Contributor' role assigned.");
            }
        }

        using var db = new SPOColdStorageDbContext(this._config);
        var sitesToMigrate = await db.TargetSharePointSites.ToListAsync();
        _logger.LogWarning($"Found {sitesToMigrate.Count} site-collections to migrate.");
        foreach (var s in sitesToMigrate)
        {
            SiteListFilterConfig? siteFilterConfig = null;
            if (!string.IsNullOrEmpty(s.FilterConfigJson))
            {
                try
                {
                    siteFilterConfig = SiteListFilterConfig.FromJson(s.FilterConfigJson);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation($"Couldn't deserialise filter JSon for site '{s.RootURL}': {ex.Message}");
                }
            }

            // Instantiate "allow all" config if none can be found in the DB
            siteFilterConfig ??= new SiteListFilterConfig();

            await StartSiteMigration(s.RootURL, siteFilterConfig);
        }
    }

    async Task StartSiteMigration(string siteUrl, SiteListFilterConfig siteFolderConfig)
    {
        var ctx = await AuthUtils.GetClientContext(_config, siteUrl, _logger, null);

        _logger.LogInformation($"Scanning site-collection '{siteUrl}'...");

        var spConnector = new SPOSiteCollectionLoader(_config, siteUrl, _logger);

        var crawler = new SiteListsAndLibrariesCrawler<ListItemCollectionPosition>(spConnector, _logger);
        await crawler.StartSiteCrawl(siteFolderConfig, Crawler_SharePointFileFound, null);
    }

    /// <summary>
    /// Crawler found a relevant file
    /// </summary>
    private async Task Crawler_SharePointFileFound(BaseSharePointFileInfo foundFileInfo)
    {
        await _sharePointFileMigrator.QueueSharePointFileMigrationIfNeeded(foundFileInfo, _containerClient!);
    }
}
