using Entities;
using Entities.Configuration;
using Entities.DBEntities;
using Migration.Engine.Adapters;
using Migration.Engine.Connectors;
using Migration.Engine.Utils;
using Migration.Engine.Utils.Extensions;
using Models;
using Microsoft.SharePoint.Client;
using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.SnapshotBuilder;

/// <summary>
/// Builds a snapshot for a single site
/// </summary>
public class SiteModelBuilder : BaseComponent, IDisposable
{
    private readonly TargetMigrationSite _site;
    private readonly SiteListFilterConfig _siteFilterConfig;
    private readonly SiteSnapshotModel _model;
    private readonly IFileAnalyticsProvider _analyticsProvider;

    private readonly object _statsLock = new();
    private readonly object _bufferLock = new();
    private readonly object _tasksLock = new();
    private readonly SemaphoreSlim _bgTasksLimit = new(1, 1);

    private volatile bool _showStats = false;
    private readonly List<SharePointFileInfoWithList> _fileFoundBuffer = [];
    private readonly ConcurrentBag<Task<BackgroundUpdate>> _backgroundMetaTasksAll = [];

    public SiteModelBuilder(
        Config config,
        ILogger ILogger,
        TargetMigrationSite site,
        IFileAnalyticsProvider? analyticsProvider = null) : base(config, ILogger)
    {
        _site = site;
        _model = new SiteSnapshotModel();

        // Use provided adapter or create default Graph adapter
        _analyticsProvider = analyticsProvider ?? new GraphFileAnalyticsAdapter(config, site.RootURL, ILogger);

        // Figure out what to analyse
        SiteListFilterConfig? siteFilterConfig = null;
        if (!string.IsNullOrEmpty(site.FilterConfigJson))
        {
            try
            {
                siteFilterConfig = SiteListFilterConfig.FromJson(site.FilterConfigJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Couldn't deserialise filter JSon for site '{site.RootURL}': {ex.Message}");
            }
        }

        // Instantiate "allow all" config if none can be found in the DB
        _siteFilterConfig = siteFilterConfig ?? new SiteListFilterConfig();
    }

    public void Dispose()
    {
        if (_analyticsProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _bgTasksLimit.Dispose();
    }

    /// <summary>
    /// Check if a file was successfully analyzed recently and should be skipped
    /// </summary>
    private Task<bool> ShouldSkipFileAnalysis(DriveItemSharePointFileInfo fileInfo)
    {
        return _analyticsProvider.ShouldSkipFileAnalysisAsync(fileInfo, _config.AnalysisSkipHours);
    }

    /// <summary>
    /// Background tasks getting item analytics
    /// </summary>
    public IEnumerable<Task<BackgroundUpdate>> BackgroundMetaTasksAll => _backgroundMetaTasksAll;

    public Task<SiteSnapshotModel> Build()
    {
        return Build(100, null, null);
    }
    public async Task<SiteSnapshotModel> Build(int batchSize, Action<List<SharePointFileInfoWithList>>? newFilesCallback, Action<List<DocumentSiteWithMetadata>>? filesUpdatedCallback)
    {
        if (batchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        if (!_model.Finished.HasValue)
        {
            // Determine which approach to use based on configuration
            // Priority: Drive API > Graph Connectors > CSOM
            var useGraphDriveApi = _config.AzureAdConfig.UseClientSecretAuth;  // Default to Drive API for ClientSecret
            var useGraphConnector = false;  // Can be enabled via future config option
            
            if (useGraphDriveApi)
            {
                _logger.LogInformation("Using Graph Drive API (optimal performance with delta query support)");
                await BuildWithGraphDriveApi(batchSize, newFilesCallback, filesUpdatedCallback).ConfigureAwait(false);
            }
            else if (useGraphConnector)
            {
                _logger.LogInformation("Using Graph API connector (no SharePoint CSOM permissions required)");
                await BuildWithGraphConnector(batchSize, newFilesCallback, filesUpdatedCallback).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Using CSOM connector (requires SharePoint API permissions)");
                await BuildWithCSOMConnector(batchSize, newFilesCallback, filesUpdatedCallback).ConfigureAwait(false);
            }
        }

        return _model;
    }

    /// <summary>
    /// Build using Graph Drive API with delta query support (fastest, most efficient).
    /// GraphDriveSnapshotBuilder fills _model directly via model.AddFile() and wraps
    /// each file in DocumentSiteWithMetadata so analytics collection picks them up.
    /// </summary>
    private async Task BuildWithGraphDriveApi(int batchSize, Action<List<SharePointFileInfoWithList>>? newFilesCallback, Action<List<DocumentSiteWithMetadata>>? filesUpdatedCallback)
    {
        try
        {
            _model.Started = DateTime.UtcNow;

            var driveBuilder = new GraphDriveSnapshotBuilder(_config, _site.RootURL, _logger);

            // Fill OUR model directly (no merging, no cache issues)
            await driveBuilder.BuildSnapshotAsync(_model, batchSize, newFilesCallback).ConfigureAwait(false);

            _logger.LogInformation($"STAGE 1/2: Drive crawl complete. Files found: {_model.AllFiles.Count}");

            // STAGE 2: Collect analytics (access counts) and version history per file
            if (_analyticsProvider != null && _model.AllFiles.Count > 0)
            {
                _logger.LogInformation("STAGE 2/2: Collecting analytics & version history for files...");
                _ = Task.Run(StartAnalysisStatsUpdates);
                await WaitForAnalysisCompletion(batchSize, filesUpdatedCallback).ConfigureAwait(false);
            }
            else
            {
                _model.Finished = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"ERROR: '{ex.Message}' reading site {_site.RootURL} with Graph Drive API");
        }
    }

    /// <summary>
    /// Build using Graph API connectors (no SharePoint API permissions needed)
    /// </summary>
    private async Task BuildWithGraphConnector(int batchSize, Action<List<SharePointFileInfoWithList>>? newFilesCallback, Action<List<DocumentSiteWithMetadata>>? filesUpdatedCallback)
    {
        try
        {
            var graphConnector = new GraphSiteCollectionLoader(_config, _site.RootURL, _logger);
            var crawler = new SiteListsAndLibrariesCrawler<string>(graphConnector, _logger);

            // Begin and block until all files crawled
            _model.Started = DateTime.Now;

            // Run background tasks
            _ = Task.Run(StartAnalysisStatsUpdates);

            await crawler.StartSiteCrawl(_siteFilterConfig, (SharePointFileInfoWithList foundFile) => Crawler_SharePointFileFound(foundFile, batchSize, newFilesCallback),
                () => CrawlComplete(newFilesCallback)).ConfigureAwait(false);

            _logger.LogInformation($"STAGE 1/2: Finished crawling site files. Waiting for background update tasks to finish...");
            await Task.WhenAll(BackgroundMetaTasksAll).ConfigureAwait(false);

            await WaitForAnalysisCompletion(batchSize, filesUpdatedCallback).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"ERROR: '{ex.Message}' reading site {_site.RootURL} with Graph API");
        }
    }

    /// <summary>
    /// Build using CSOM connectors (requires SharePoint API permissions)
    /// </summary>
    private async Task BuildWithCSOMConnector(int batchSize, Action<List<SharePointFileInfoWithList>>? newFilesCallback, Action<List<DocumentSiteWithMetadata>>? filesUpdatedCallback)
    {
        ClientContext? ctx = null;
        try
        {
            ctx = await AuthUtils.GetClientContext(_config, _site.RootURL, _logger, null).ConfigureAwait(false);
        }
        catch (System.Net.WebException ex)
        {
            _logger.LogError($"ERROR: '{ex.Message}' reading site {_site.RootURL}");
            return;
        }

        var spConnector = new SPOSiteCollectionLoader(_config, _site.RootURL, _logger);
        var crawler = new SiteListsAndLibrariesCrawler<ListItemCollectionPosition>(spConnector, _logger);

        // Begin and block until all files crawled
        _model.Started = DateTime.Now;

        // Run background tasks
        _ = Task.Run(StartAnalysisStatsUpdates);

        await crawler.StartSiteCrawl(_siteFilterConfig, (SharePointFileInfoWithList foundFile) => Crawler_SharePointFileFound(foundFile, batchSize, newFilesCallback),
            () => CrawlComplete(newFilesCallback)).ConfigureAwait(false);

        _logger.LogInformation($"STAGE 1/2: Finished crawling site files. Waiting for background update tasks to finish...");
        await Task.WhenAll(BackgroundMetaTasksAll).ConfigureAwait(false);

        await WaitForAnalysisCompletion(batchSize, filesUpdatedCallback).ConfigureAwait(false);
    }

    /// <summary>
    /// Wait for file analysis to complete
    /// </summary>
    private async Task WaitForAnalysisCompletion(int batchSize, Action<List<DocumentSiteWithMetadata>>? filesUpdatedCallback)
    {
        var filesToGetAnalysisFor = true;
        while (filesToGetAnalysisFor)
        {
            // Check every 5 seconds
            await Task.Delay(5000).ConfigureAwait(false);

            // Load pending & non-fatal error files
            var filesToLoad = _model.DocsByState(SiteFileAnalysisState.AnalysisPending);
            filesToLoad.AddRange(_model.DocsByState(SiteFileAnalysisState.TransientError));

            if (filesToLoad.Count > 0)
            {
                // Start metadata update any doc with "pending" state
                Console.WriteLine($"Have completed {_model.DocsCompleted.Count} of {_model.AllFiles.Count}. Pending: {filesToLoad.Count} ({_model.DocsByState(SiteFileAnalysisState.TransientError).Count} errors to retry)");
                await UpdatePendingFilesAsync(batchSize, [.. filesToLoad.Cast<SharePointFileInfoWithList>()], filesUpdatedCallback).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine("Waiting for update tasks to finish...");
            }

            // Check again if anything to do
            filesToGetAnalysisFor = !_model.AnalysisFinished;
        }
        StopAnalysisStatsUpdates();
        _model.InvalidateCaches();
        _model.Finished = DateTime.Now;
        var ts = _model.Finished.Value.Subtract(_model.Started);
        _logger.LogInformation($"STAGE 2/2: Finished getting metadata for site files. All done in {ts.TotalMinutes:N2} minutes.");
    }

    private void StopAnalysisStatsUpdates()
    {
        _showStats = false;
    }

    async Task StartAnalysisStatsUpdates()
    {
        _showStats = true;
        while (_showStats)
        {
            var pendingCount = _model.DocsByState(SiteFileAnalysisState.AnalysisPending).Count;
            if (pendingCount > 0)
            {
                Console.WriteLine($"{pendingCount:N0} files pending analytics & version data");
            }

            await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        }
    }

    async Task UpdatePendingFilesAsync(int batchSize, List<SharePointFileInfoWithList> filesToUpdate, Action<List<DocumentSiteWithMetadata>>? filesUpdatedCallback)
    {
        var backgroundTasksThisChunk = new List<Task<BackgroundUpdate>>();

        // Begin background loading of extra metadata
        var pendingFilesToAnalyse = new List<DocumentSiteWithMetadata>();

        // Throttle requests to one set of files to update at once
        await _bgTasksLimit.WaitAsync().ConfigureAwait(false);

        foreach (var fileToUpdate in filesToUpdate)
        {
            // We only get stats for docs, not attachments
            if (fileToUpdate is DocumentSiteWithMetadata docToUpdate)
            {

                // Avoid analysing more than once
                docToUpdate.State = SiteFileAnalysisState.AnalysisInProgress;
                pendingFilesToAnalyse.Add(docToUpdate);
            }

            // Start new background every $CHUNK_SIZE
            if (pendingFilesToAnalyse.Count >= batchSize)
            {
                var newFileChunkCopy = new List<DocumentSiteWithMetadata>(pendingFilesToAnalyse);
                pendingFilesToAnalyse.Clear();

                // Background process chunk using adapter
                backgroundTasksThisChunk.Add(_analyticsProvider.GetFileAnalyticsAsync(newFileChunkCopy));
                backgroundTasksThisChunk.Add(_analyticsProvider.GetFileVersionHistoryAsync(newFileChunkCopy));
            }
        }

        // Background process the rest
        if (pendingFilesToAnalyse.Count > 0)
        {
            backgroundTasksThisChunk.Add(_analyticsProvider.GetFileAnalyticsAsync(pendingFilesToAnalyse));
            backgroundTasksThisChunk.Add(_analyticsProvider.GetFileVersionHistoryAsync(pendingFilesToAnalyse));
        }
        else
        {
            return;
        }

        // Update global tasks (ConcurrentBag is thread-safe)
        foreach (var task in backgroundTasksThisChunk)
        {
            _backgroundMetaTasksAll.Add(task);
        }

        // Compile results as they come
        var versionUpdates = new Dictionary<DriveItemSharePointFileInfo, DriveItemVersionInfo>();
        var analyticsUpdates = new Dictionary<DriveItemSharePointFileInfo, ItemAnalyticsResponse.AnalyticsItemActionStat>();

        await Task.WhenAll(backgroundTasksThisChunk).ConfigureAwait(false);

        foreach (var finishedTask in backgroundTasksThisChunk)
        {
            foreach (var stat in finishedTask.Result.UpdateResults)
            {
                if (stat.Value is DriveItemVersionInfo)
                {
                    versionUpdates.Add(stat.Key, (DriveItemVersionInfo)stat.Value);
                }
                else if (stat.Value is ItemAnalyticsResponse)
                {
                    analyticsUpdates.Add(stat.Key, ((ItemAnalyticsResponse)stat.Value).AccessStats ?? new ItemAnalyticsResponse.AnalyticsItemActionStat());
                }
            }
        }

        // Release throttle now chunk is completed
        _bgTasksLimit.Release();

        // Update model with metadata & fire event
        var updatedFiles = new List<DocumentSiteWithMetadata>(analyticsUpdates.Count);
        foreach (var fileUpdated in analyticsUpdates)
        {
            // Update model - use TryGetValue for better performance
            var versionInfo = versionUpdates.TryGetValue(fileUpdated.Key, out var info)
                ? info.Versions.ToVersionStorageInfo()
                : null;

            var updatedDoc = _model.UpdateDocItemAndInvalidateCaches(fileUpdated.Key, fileUpdated.Value, versionInfo);
            updatedDoc.State = SiteFileAnalysisState.Complete;
            updatedFiles.Add(updatedDoc);
        }

        filesUpdatedCallback?.Invoke(updatedFiles);
    }

    private void CrawlComplete(Action<List<SharePointFileInfoWithList>>? remainderFilesCallback)
    {
        // Handle remaining files
        if (remainderFilesCallback != null)
        {
            remainderFilesCallback.Invoke(_fileFoundBuffer);
        }

        _fileFoundBuffer.Clear();
    }

    private async Task Crawler_SharePointFileFound(SharePointFileInfoWithList foundFile, int batchSize, Action<List<SharePointFileInfoWithList>>? newFilesCallback)
    {
        SharePointFileInfoWithList newFile;

        if (foundFile is DriveItemSharePointFileInfo driveArg)
        {
            // Check if file was already analyzed recently
            var shouldSkip = await ShouldSkipFileAnalysis(driveArg).ConfigureAwait(false);
            newFile = new DocumentSiteWithMetadata(driveArg)
            {
                State = shouldSkip ? SiteFileAnalysisState.Complete : SiteFileAnalysisState.AnalysisPending
            };
        }
        else
        {
            // Nothing to analyse for list item attachments
            newFile = foundFile;
        }

        // Add new found files to model & event buffer
        lock (_bufferLock)
        {
            _fileFoundBuffer.Add(newFile);
            _model.AddFile(newFile, foundFile.List);

            // Do things every $batchSize
            if (_fileFoundBuffer.Count == batchSize)
            {
                var bufferCopy = new List<SharePointFileInfoWithList>(_fileFoundBuffer);
                newFilesCallback?.Invoke(bufferCopy);
                _fileFoundBuffer.Clear();
            }
        }
    }
}
