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
    private static readonly TimeSpan _progressHeartbeat = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Mutable per-drive scan progress, shared across the recursive crawl
    /// so we can emit heartbeat logs and accurate completion summaries.
    /// </summary>
    private sealed class ScanProgress
    {
        public string DriveName { get; init; } = string.Empty;
        public DateTime Started { get; } = DateTime.UtcNow;
        public DateTime LastLogged { get; set; } = DateTime.UtcNow;
        public int FilesFound { get; set; }
        public int ItemsSeen { get; set; }
        public int FoldersVisited { get; set; }
        public long TotalSize { get; set; }
        public string? CurrentPath { get; set; }
        public string? CurrentOperation { get; set; }
    }

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
    /// Process a drive using Graph's delta API.
    /// First run: returns the full snapshot (in flat pages) plus a deltaLink.
    /// Subsequent runs: returns only items changed since the previous deltaLink.
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

        // Look up the stored delta token (URL) for this drive, if any.
        var deltaToken = await db.DriveDeltaTokens
            .Where(d => d.DriveId == drive.Id)
            .FirstOrDefaultAsync();

        await DeltaDriveScanAsync(drive, docLib, model, db, deltaToken, batchSize, newFilesCallback, fileBuffer);
    }

    /// <summary>
    /// Single delta-based scan for a drive. Uses /drives/{id}/root/delta which
    /// returns a flat, paged list of items — either the full snapshot (first
    /// run) or only the changes since the stored deltaLink (subsequent runs).
    /// This replaces the previous folder-by-folder recursive crawl, which on
    /// large libraries was O(folders) even when nothing had changed.
    /// </summary>
    private async Task DeltaDriveScanAsync(
        Drive drive,
        DocLib docLib,
        SiteSnapshotModel model,
        SPOColdStorageDbContext db,
        DriveDeltaToken? storedToken,
        int batchSize,
        Action<List<SharePointFileInfoWithList>>? newFilesCallback,
        List<SharePointFileInfoWithList> fileBuffer)
    {
        if (drive.Id == null) return;

        var progress = new ScanProgress { DriveName = drive.Name ?? "Unknown" };

        // A real delta token is the full deltaLink URL returned by Graph. Any
        // legacy/garbage value (e.g. a tick count from a previous version)
        // means "treat this as a first-time delta call" — Graph will then
        // return everything plus a fresh deltaLink we can save.
        var hasUsableToken = storedToken != null
            && !string.IsNullOrWhiteSpace(storedToken.DeltaToken)
            && storedToken.DeltaToken.StartsWith("http", StringComparison.OrdinalIgnoreCase);

        _logger.LogInformation(
            hasUsableToken
                ? $"Starting delta scan of drive '{progress.DriveName}' (changes since previous scan at {storedToken!.LastScanDate:u}). Heartbeat every {(int)_progressHeartbeat.TotalSeconds}s."
                : $"Starting delta scan of drive '{progress.DriveName}' (first run – returns full snapshot). Heartbeat every {(int)_progressHeartbeat.TotalSeconds}s.");

        using var heartbeatCts = new CancellationTokenSource();
        var heartbeatTask = StartHeartbeatTask(progress, heartbeatCts.Token);

        int filesAdded = 0;
        int filesModified = 0;
        int filesDeleted = 0;
        string? newDeltaLink = null;
        bool succeeded = false;

        try
        {
            progress.CurrentOperation = "Calling Graph delta endpoint (page 1)";
            var callStart = DateTime.UtcNow;

            Microsoft.Graph.Drives.Item.Items.Item.Delta.DeltaGetResponse? page;
            if (hasUsableToken)
            {
                page = await _graphClient.Drives[drive.Id].Items["root"].Delta
                    .WithUrl(storedToken!.DeltaToken)
                    .GetAsDeltaGetResponseAsync();
            }
            else
            {
                page = await _graphClient.Drives[drive.Id].Items["root"].Delta
                    .GetAsDeltaGetResponseAsync();
            }

            _logger.LogInformation(
                $"Delta page 1 returned in {FormatElapsed(DateTime.UtcNow - callStart)} " +
                $"({page?.Value?.Count ?? 0} items).");

            int pageNum = 1;
            while (page != null)
            {
                var batch = page.Value ?? new List<DriveItem>();
                progress.CurrentOperation = $"Processing delta page {pageNum} ({batch.Count} items)";

                foreach (var item in batch)
                {
                    progress.ItemsSeen++;

                    if (item.Deleted != null)
                    {
                        filesDeleted++;
                        progress.CurrentPath = item.Name ?? item.Id ?? "<deleted>";
                        LogHeartbeatIfDue(progress);
                        continue;
                    }

                    if (item.Folder != null)
                    {
                        progress.FoldersVisited++;
                        progress.CurrentPath = ExtractDirectoryPath(item.ParentReference?.Path ?? string.Empty);
                        LogHeartbeatIfDue(progress);
                        continue;
                    }

                    if (item.File == null) continue;

                    var doc = ConvertDriveItemToDocumentSiteWithMetadata(item, docLib);
                    if (doc == null) continue;

                    model.AddFile(doc, docLib);
                    fileBuffer.Add(doc);
                    progress.FilesFound++;
                    progress.TotalSize += item.Size ?? 0;
                    progress.CurrentPath = doc.ServerRelativeFilePath;

                    if (hasUsableToken)
                    {
                        // Cheap existence probe – Url is uniquely indexed (IX_files_url)
                        // so this hits the index and returns a bool, not a row payload.
                        progress.CurrentOperation = $"Classifying file (add vs modify): {doc.ServerRelativeFilePath}";
                        var exists = await db.Files
                            .AsNoTracking()
                            .AnyAsync(f => f.Url == doc.ServerRelativeFilePath);
                        if (exists) filesModified++; else filesAdded++;
                        progress.CurrentOperation = $"Processing delta page {pageNum}";
                    }
                    else
                    {
                        filesAdded++;
                    }

                    LogHeartbeatIfDue(progress);

                    if (fileBuffer.Count >= batchSize && newFilesCallback != null)
                    {
                        _logger.LogInformation(
                            $"Flushing batch of {fileBuffer.Count} files to staging (drive total: {progress.FilesFound}).");
                        newFilesCallback(new List<SharePointFileInfoWithList>(fileBuffer));
                        fileBuffer.Clear();
                    }
                }

                if (!string.IsNullOrEmpty(page.OdataNextLink))
                {
                    pageNum++;
                    progress.CurrentOperation = $"Fetching delta page {pageNum}";
                    callStart = DateTime.UtcNow;
                    page = await _graphClient.Drives[drive.Id].Items["root"].Delta
                        .WithUrl(page.OdataNextLink)
                        .GetAsDeltaGetResponseAsync();
                    _logger.LogInformation(
                        $"Delta page {pageNum} returned in {FormatElapsed(DateTime.UtcNow - callStart)} " +
                        $"({page?.Value?.Count ?? 0} items).");
                }
                else
                {
                    // OdataDeltaLink only arrives on the final page; capture it for next run.
                    newDeltaLink = page.OdataDeltaLink;
                    break;
                }
            }

            succeeded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error during delta scan of drive {drive.Name}");
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { /* swallow heartbeat shutdown noise */ }
        }

        if (!succeeded) return;

        // Persist new delta link (only if we got one – paranoia for partial runs).
        if (storedToken == null)
        {
            storedToken = new DriveDeltaToken
            {
                DriveId = drive.Id,
                SiteId = _siteId!,
                SiteUrl = _siteUrl,
                DeltaToken = newDeltaLink ?? string.Empty,
                LastScanDate = DateTime.UtcNow,
                FileCount = progress.FilesFound,
                TotalSize = progress.TotalSize,
                LastChangeDate = (filesAdded + filesModified + filesDeleted) > 0 ? DateTime.UtcNow : null
            };
            db.DriveDeltaTokens.Add(storedToken);
        }
        else
        {
            if (!string.IsNullOrEmpty(newDeltaLink))
                storedToken.DeltaToken = newDeltaLink;
            storedToken.LastScanDate = DateTime.UtcNow;
            if ((filesAdded + filesModified + filesDeleted) > 0)
                storedToken.LastChangeDate = DateTime.UtcNow;
            if (!hasUsableToken)
            {
                // First real delta run – record the snapshot totals.
                storedToken.FileCount = progress.FilesFound;
                storedToken.TotalSize = progress.TotalSize;
            }
        }

        await db.SaveChangesAsync();

        var elapsed = DateTime.UtcNow - progress.Started;
        _logger.LogInformation(
            $"Delta scan complete for '{progress.DriveName}'. " +
            $"Items returned: {progress.ItemsSeen} " +
            $"({progress.FilesFound} files, {progress.FoldersVisited} folders, {filesDeleted} deletes). " +
            $"Added: {filesAdded}, Modified: {filesModified}, " +
            $"Size: {FormatBytes(progress.TotalSize)}, Elapsed: {FormatElapsed(elapsed)}.");
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

            // Build the server-relative path from authoritative pieces (drive root +
            // parent folder + item name). DO NOT derive it from item.WebUrl: for many
            // SharePoint items (Office Online docs, shortcuts, OneNote pages, etc.)
            // webUrl points to /_layouts/15/Doc.aspx?sourcedoc=..., which collapses
            // to a single path after stripping the query string and causes duplicate
            // key collisions on IX_files_url.
            var driveRoot = (docLib.ServerRelativeUrl ?? string.Empty).TrimEnd('/');
            var parentRel = ExtractDirectoryPath(item.ParentReference?.Path ?? string.Empty).Trim('/');
            var serverRelativePath = string.IsNullOrEmpty(parentRel)
                ? $"{driveRoot}/{item.Name}"
                : $"{driveRoot}/{parentRel}/{item.Name}";

            // Full server-relative directory path (mirrors legacy FileDirRef format).
            var dirPath = string.IsNullOrEmpty(parentRel)
                ? driveRoot
                : $"{driveRoot}/{parentRel}";

            var driveItem = new DriveItemSharePointFileInfo
            {
                ServerRelativeFilePath = serverRelativePath,
                FileSize = item.Size ?? 0,
                LastModified = item.LastModifiedDateTime?.UtcDateTime ?? DateTime.UtcNow,
                CreatedDate = item.CreatedDateTime?.UtcDateTime,
                Author = item.LastModifiedBy?.User?.DisplayName ?? "Unknown",
                DirectoryPath = dirPath,
                Subfolder = ExtractSubfolder(parentRel),
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

    private void LogHeartbeatIfDue(ScanProgress progress)
    {
        var now = DateTime.UtcNow;
        if (now - progress.LastLogged < _progressHeartbeat) return;

        progress.LastLogged = now;
        LogProgress(progress);
    }

    /// <summary>
    /// Always-on heartbeat that fires on a wall-clock interval independent of
    /// crawl progress, so we still see output if a Graph call hangs for minutes
    /// or if an incremental scan walks thousands of unchanged items.
    /// </summary>
    private Task StartHeartbeatTask(ScanProgress progress, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_progressHeartbeat, ct);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                progress.LastLogged = DateTime.UtcNow;
                LogProgress(progress);
            }
        }, ct);
    }

    private void LogProgress(ScanProgress progress)
    {
        var elapsed = DateTime.UtcNow - progress.Started;
        var rate = elapsed.TotalSeconds > 0 ? progress.ItemsSeen / elapsed.TotalSeconds : 0;
        _logger.LogInformation(
            $"Progress [{progress.DriveName}]: scanned {progress.ItemsSeen} items " +
            $"({progress.FilesFound} files matched, {progress.FoldersVisited} folders, " +
            $"{FormatBytes(progress.TotalSize)}) in {FormatElapsed(elapsed)} " +
            $"({rate:0.0} items/s). " +
            $"At: {progress.CurrentPath ?? "-"} [{progress.CurrentOperation ?? "scanning"}]");
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1) return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m {elapsed.Seconds}s";
        if (elapsed.TotalMinutes >= 1) return $"{elapsed.Minutes}m {elapsed.Seconds}s";
        return $"{elapsed.TotalSeconds:0.0}s";
    }

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
            // Return the decoded path so it matches the FileRef-style format used by
            // SharePoint and the legacy GraphListLoader (e.g. "Documentos compartidos"
            // instead of "Documentos%20compartidos"). This keeps file/directory URLs
            // consistent across code paths.
            return Uri.UnescapeDataString(uri.AbsolutePath);
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
            // Decode so spaces/special chars match the FileRef-style format used
            // elsewhere (e.g. "Documentos compartidos" instead of "Documentos%20compartidos").
            return Uri.UnescapeDataString(parentPath.Substring(rootIndex + 6).TrimStart('/'));
        }

        return Uri.UnescapeDataString(parentPath);
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
