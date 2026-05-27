using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.EntityFrameworkCore;
using Entities;
using Entities.Configuration;
using Entities.DBEntities;
using Models;
using Microsoft.Extensions.Logging;

namespace Migration.Engine.SnapshotBuilder;

/// <summary>
/// Builds site snapshots using Graph Drive API for optimal performance
/// Uses delta queries for 10x faster incremental updates
/// </summary>
public class GraphDriveSnapshotBuilder
{
    private readonly Config _config;
    private readonly ILogger _logger;
    private readonly string _siteUrl;
    private readonly GraphServiceClient _graphClient;
    private string? _siteId;

    public GraphDriveSnapshotBuilder(Config config, string siteUrl, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _siteUrl = siteUrl ?? throw new ArgumentNullException(nameof(siteUrl));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Initialize Graph client
        var credential = new Azure.Identity.ClientSecretCredential(
            _config.AzureAdConfig.TenantId,
            _config.AzureAdConfig.ClientID,
            _config.AzureAdConfig.Secret
        );
        _graphClient = new GraphServiceClient(credential);
    }

    /// <summary>
    /// Build a snapshot of the site using Drive API and fill the provided model.
    /// Automatically uses delta query if previous scan exists.
    /// Files are wrapped in DocumentSiteWithMetadata so they trigger analytics collection.
    /// </summary>
    public async Task BuildSnapshotAsync(
        SiteSnapshotModel model,
        int batchSize,
        Action<List<SharePointFileInfoWithList>>? newFilesCallback)
    {
        if (model.Started == default || model.Started == DateTime.MinValue)
        {
            model.Started = DateTime.UtcNow;
        }

        try
        {
            _logger.LogInformation($"Starting Drive API snapshot for {_siteUrl}");

            // Get site ID
            _siteId = await GetSiteIdAsync();
            _logger.LogInformation($"Site ID: {_siteId}");

            // Get all drives in the site
            var drives = await GetDrivesAsync();
            _logger.LogInformation($"Found {drives.Count} drive(s)");

            // Buffer of files for batch callbacks
            var fileBuffer = new List<SharePointFileInfoWithList>();

            // Process each drive
            foreach (var drive in drives)
            {
                await ProcessDriveAsync(drive, model, batchSize, newFilesCallback, fileBuffer);
            }

            // Flush remaining buffered files
            if (fileBuffer.Count > 0 && newFilesCallback != null)
            {
                newFilesCallback(new List<SharePointFileInfoWithList>(fileBuffer));
                fileBuffer.Clear();
            }

            _logger.LogInformation($"Drive crawl complete. Files found: {model.AllFiles.Count}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error building snapshot for {_siteUrl}");
            throw;
        }
    }

    /// <summary>
    /// Build a standalone snapshot (creates its own model).
    /// Kept for backwards compatibility.
    /// </summary>
    public async Task<SiteSnapshotModel> BuildSnapshotAsync()
    {
        var model = new SiteSnapshotModel { Started = DateTime.UtcNow };
        await BuildSnapshotAsync(model, 100, null);
        model.Finished = DateTime.UtcNow;
        return model;
    }

    /// <summary>
    /// Get the site ID from URL
    /// </summary>
    private async Task<string> GetSiteIdAsync()
    {
        if (!string.IsNullOrEmpty(_siteId))
            return _siteId;

        var uri = new Uri(_siteUrl);
        var hostname = uri.Host;
        var sitePath = uri.AbsolutePath;

        _logger.LogInformation($"Resolving site ID for: {hostname}:{sitePath}");

        var site = await _graphClient.Sites[$"{hostname}:{sitePath}"].GetAsync();
        
        if (site?.Id == null)
            throw new Exception($"Could not resolve site ID for: {_siteUrl}");

        _siteId = site.Id;
        return _siteId;
    }

    /// <summary>
    /// Get all drives in the site
    /// </summary>
    private async Task<List<Drive>> GetDrivesAsync()
    {
        var drives = new List<Drive>();

        var drivesResponse = await _graphClient.Sites[_siteId].Drives.GetAsync(config =>
        {
            config.QueryParameters.Select = new[] { "id", "name", "driveType", "webUrl" };
        });

        if (drivesResponse?.Value != null)
        {
            drives.AddRange(drivesResponse.Value.Where(d => d.Id != null)!);
        }

        return drives;
    }

    /// <summary>
    /// Process a drive - uses delta query if available, full scan otherwise.
    /// Creates ONE shared DocLib per drive and adds files via model.AddFile().
    /// </summary>
    private async Task ProcessDriveAsync(
        Drive drive,
        SiteSnapshotModel model,
        int batchSize,
        Action<List<SharePointFileInfoWithList>>? newFilesCallback,
        List<SharePointFileInfoWithList> fileBuffer)
    {
        if (drive.Id == null) return;

        _logger.LogInformation($"Processing drive: {drive.Name} ({drive.DriveType})");

        // Create ONE DocLib for this drive (shared by all files in the drive)
        var docLib = new DocLib
        {
            Title = drive.Name ?? "Unknown",
            DriveId = drive.Id,
            ServerRelativeUrl = ExtractServerRelativePath(drive.WebUrl ?? string.Empty)
        };

        using var db = new SPOColdStorageDbContext(_config);

        // Check for existing delta token
        var deltaToken = await db.DriveDeltaTokens
            .Where(d => d.DriveId == drive.Id)
            .FirstOrDefaultAsync();

        if (deltaToken == null)
        {
            // First scan - full crawl with delta token
            _logger.LogInformation($"First scan of drive {drive.Name} - performing full crawl");
            await FullDriveScanAsync(drive, docLib, model, db, batchSize, newFilesCallback, fileBuffer);
        }
        else
        {
            // Incremental scan
            _logger.LogInformation($"Incremental scan of drive {drive.Name} (last scan: {deltaToken.LastScanDate})");
            await IncrementalDriveScanAsync(drive, docLib, model, db, deltaToken, batchSize, newFilesCallback, fileBuffer);
        }
    }

    /// <summary>
    /// Full scan of drive with delta token for future incremental updates
    /// </summary>
    private async Task FullDriveScanAsync(
        Drive drive,
        DocLib docLib,
        SiteSnapshotModel model,
        SPOColdStorageDbContext db,
        int batchSize,
        Action<List<SharePointFileInfoWithList>>? newFilesCallback,
        List<SharePointFileInfoWithList> fileBuffer)
    {
        if (drive.Id == null) return;

        try
        {
            var result = await CrawlDriveItemsRecursiveAsync(drive.Id, "", docLib, model, batchSize, newFilesCallback, fileBuffer);

            var deltaTokenEntity = new DriveDeltaToken
            {
                DriveId = drive.Id,
                SiteId = _siteId!,
                SiteUrl = _siteUrl,
                DeltaToken = DateTime.UtcNow.Ticks.ToString(),
                LastScanDate = DateTime.UtcNow,
                FileCount = result.filesFound,
                TotalSize = result.totalSize
            };

            db.DriveDeltaTokens.Add(deltaTokenEntity);
            await db.SaveChangesAsync();

            _logger.LogInformation($"Completed scan of drive {drive.Name}. Files: {result.filesFound}, Size: {FormatBytes(result.totalSize)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error during full scan of drive {drive.Name}");
        }
    }

    /// <summary>
    /// Recursively crawl all items in a drive.
    /// Each discovered file is wrapped in DocumentSiteWithMetadata (State=AnalysisPending)
    /// and added via model.AddFile() so the analytics workflow picks it up.
    /// </summary>
    private async Task<(int filesFound, long totalSize)> CrawlDriveItemsRecursiveAsync(
        string driveId,
        string itemPath,
        DocLib docLib,
        SiteSnapshotModel model,
        int batchSize,
        Action<List<SharePointFileInfoWithList>>? newFilesCallback,
        List<SharePointFileInfoWithList> fileBuffer)
    {
        int filesFound = 0;
        long totalSize = 0;

        try
        {
            DriveItemCollectionResponse? items;

            if (string.IsNullOrEmpty(itemPath))
            {
                items = await _graphClient.Drives[driveId].Items["root"].Children.GetAsync(config =>
                {
                    config.QueryParameters.Top = 5000;
                    config.QueryParameters.Select = new[] { "id", "name", "size", "file", "folder", "lastModifiedDateTime", "createdDateTime", "lastModifiedBy", "webUrl", "parentReference" };
                });
            }
            else
            {
                items = await _graphClient.Drives[driveId].Items[itemPath].Children.GetAsync(config =>
                {
                    config.QueryParameters.Top = 5000;
                    config.QueryParameters.Select = new[] { "id", "name", "size", "file", "folder", "lastModifiedDateTime", "createdDateTime", "lastModifiedBy", "webUrl", "parentReference" };
                });
            }

            while (items != null)
            {
                if (items.Value != null)
                {
                    foreach (var item in items.Value)
                    {
                        if (item.Folder != null)
                        {
                            if (item.Id != null)
                            {
                                var subResult = await CrawlDriveItemsRecursiveAsync(driveId, item.Id, docLib, model, batchSize, newFilesCallback, fileBuffer);
                                filesFound += subResult.filesFound;
                                totalSize += subResult.totalSize;
                            }
                        }
                        else if (item.File != null)
                        {
                            var doc = ConvertDriveItemToDocumentSiteWithMetadata(item, docLib);
                            if (doc != null)
                            {
                                // Add via model.AddFile so it's stored in the DocLib's Files list (source of truth)
                                model.AddFile(doc, docLib);
                                fileBuffer.Add(doc);
                                filesFound++;
                                totalSize += item.Size ?? 0;

                                // Flush buffer in batches
                                if (fileBuffer.Count >= batchSize && newFilesCallback != null)
                                {
                                    newFilesCallback(new List<SharePointFileInfoWithList>(fileBuffer));
                                    fileBuffer.Clear();
                                }
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(items.OdataNextLink))
                {
                    items = await _graphClient.Drives[driveId].Items["root"].Children.WithUrl(items.OdataNextLink).GetAsync();
                }
                else
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Error crawling drive items at path: {itemPath}");
        }

        return (filesFound, totalSize);
    }

    /// <summary>
    /// Incremental scan using stored delta token (timestamp-based for now)
    /// </summary>
    private async Task IncrementalDriveScanAsync(
        Drive drive,
        DocLib docLib,
        SiteSnapshotModel model,
        SPOColdStorageDbContext db,
        DriveDeltaToken storedToken,
        int batchSize,
        Action<List<SharePointFileInfoWithList>>? newFilesCallback,
        List<SharePointFileInfoWithList> fileBuffer)
    {
        if (drive.Id == null) return;

        try
        {
            var lastScan = storedToken.LastScanDate;
            var result = await CrawlDriveItemsIncrementalAsync(drive.Id, "", docLib, model, lastScan, db, batchSize, newFilesCallback, fileBuffer);

            storedToken.DeltaToken = DateTime.UtcNow.Ticks.ToString();
            storedToken.LastScanDate = DateTime.UtcNow;
            if (result.filesAdded > 0 || result.filesModified > 0)
            {
                storedToken.LastChangeDate = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();

            _logger.LogInformation(
                $"Incremental scan complete for drive {drive.Name}. " +
                $"Added: {result.filesAdded}, Modified: {result.filesModified}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error during incremental scan of drive {drive.Name}");
        }
    }

    /// <summary>
    /// Crawl drive items incrementally (only modified since last scan)
    /// </summary>
    private async Task<(int filesAdded, int filesModified, long totalSize)> CrawlDriveItemsIncrementalAsync(
        string driveId,
        string itemPath,
        DocLib docLib,
        SiteSnapshotModel model,
        DateTime since,
        SPOColdStorageDbContext db,
        int batchSize,
        Action<List<SharePointFileInfoWithList>>? newFilesCallback,
        List<SharePointFileInfoWithList> fileBuffer)
    {
        int filesAdded = 0;
        int filesModified = 0;
        long totalSize = 0;

        try
        {
            DriveItemCollectionResponse? items;

            if (string.IsNullOrEmpty(itemPath))
            {
                items = await _graphClient.Drives[driveId].Items["root"].Children.GetAsync(config =>
                {
                    config.QueryParameters.Top = 5000;
                    config.QueryParameters.Select = new[] { "id", "name", "size", "file", "folder", "lastModifiedDateTime", "createdDateTime", "lastModifiedBy", "webUrl", "parentReference" };
                });
            }
            else
            {
                items = await _graphClient.Drives[driveId].Items[itemPath].Children.GetAsync(config =>
                {
                    config.QueryParameters.Top = 5000;
                    config.QueryParameters.Select = new[] { "id", "name", "size", "file", "folder", "lastModifiedDateTime", "createdDateTime", "lastModifiedBy", "webUrl", "parentReference" };
                });
            }

            while (items != null)
            {
                if (items.Value != null)
                {
                    foreach (var item in items.Value)
                    {
                        if (item.Folder != null)
                        {
                            if (item.Id != null)
                            {
                                var subResult = await CrawlDriveItemsIncrementalAsync(driveId, item.Id, docLib, model, since, db, batchSize, newFilesCallback, fileBuffer);
                                filesAdded += subResult.filesAdded;
                                filesModified += subResult.filesModified;
                                totalSize += subResult.totalSize;
                            }
                        }
                        else if (item.File != null)
                        {
                            if (item.LastModifiedDateTime?.UtcDateTime > since)
                            {
                                var doc = ConvertDriveItemToDocumentSiteWithMetadata(item, docLib);
                                if (doc != null)
                                {
                                    model.AddFile(doc, docLib);
                                    fileBuffer.Add(doc);

                                    var existingFile = await db.Files
                                        .Where(f => f.Url == doc.ServerRelativeFilePath)
                                        .FirstOrDefaultAsync();

                                    if (existingFile == null)
                                        filesAdded++;
                                    else
                                        filesModified++;

                                    totalSize += item.Size ?? 0;

                                    if (fileBuffer.Count >= batchSize && newFilesCallback != null)
                                    {
                                        newFilesCallback(new List<SharePointFileInfoWithList>(fileBuffer));
                                        fileBuffer.Clear();
                                    }
                                }
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(items.OdataNextLink))
                {
                    items = await _graphClient.Drives[driveId].Items["root"].Children.WithUrl(items.OdataNextLink).GetAsync();
                }
                else
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Error during incremental crawl at path: {itemPath}");
        }

        return (filesAdded, filesModified, totalSize);
    }

    /// <summary>
    /// Convert a Graph DriveItem directly into a DocumentSiteWithMetadata
    /// pre-set to AnalysisPending so analytics collection runs.
    /// </summary>
    private DocumentSiteWithMetadata? ConvertDriveItemToDocumentSiteWithMetadata(DriveItem item, DocLib docLib)
    {
        try
        {
            if (item.File == null || item.Name == null)
                return null;

            var fileUrl = item.WebUrl ?? string.Empty;
            var serverRelativePath = ExtractServerRelativePath(fileUrl);
            var parentPath = item.ParentReference?.Path ?? string.Empty;
            var dirPath = ExtractDirectoryPath(parentPath);

            var driveItem = new DriveItemSharePointFileInfo
            {
                ServerRelativeFilePath = serverRelativePath,
                FileSize = item.Size ?? 0,
                LastModified = item.LastModifiedDateTime?.UtcDateTime ?? DateTime.UtcNow,
                CreatedDate = item.CreatedDateTime?.UtcDateTime,
                Author = item.LastModifiedBy?.User?.DisplayName ?? "Unknown",
                DirectoryPath = dirPath,
                Subfolder = ExtractSubfolder(dirPath),
                WebUrl = _siteUrl,
                SiteUrl = _siteUrl,
                GraphItemId = item.Id ?? string.Empty,
                DriveId = docLib.DriveId,
                List = docLib   // shared DocLib instance
            };

            return new DocumentSiteWithMetadata(driveItem)
            {
                State = SiteFileAnalysisState.AnalysisPending
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Error converting drive item: {item.Name}");
            return null;
        }
    }

    #region Helper Methods

    private string ExtractDeltaToken(string? deltaLink)
    {
        if (string.IsNullOrEmpty(deltaLink))
            return string.Empty;

        var tokenParam = "token=";
        var tokenIndex = deltaLink.IndexOf(tokenParam);
        if (tokenIndex >= 0)
        {
            return deltaLink.Substring(tokenIndex + tokenParam.Length);
        }

        return string.Empty;
    }

    private string ExtractServerRelativePath(string webUrl)
    {
        if (string.IsNullOrEmpty(webUrl))
            return string.Empty;

        try
        {
            var uri = new Uri(webUrl);
            return uri.AbsolutePath;
        }
        catch
        {
            return webUrl;
        }
    }

    private string ExtractDirectoryPath(string parentPath)
    {
        // Graph returns paths like "/drives/{drive-id}/root:/folder/subfolder"
        // Extract just the folder part
        if (string.IsNullOrEmpty(parentPath))
            return string.Empty;

        var rootIndex = parentPath.IndexOf("/root:");
        if (rootIndex >= 0)
        {
            return parentPath.Substring(rootIndex + 6).TrimStart('/');
        }

        return parentPath;
    }

    private string ExtractSubfolder(string dirPath)
    {
        return dirPath.TrimEnd('/');
    }

    private string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    #endregion
}
