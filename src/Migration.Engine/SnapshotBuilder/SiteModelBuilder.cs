using Entities;
using Entities.Configuration;
using Entities.DBEntities;
using Microsoft.EntityFrameworkCore;
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

            _logger.LogInformation($"STAGE 1/2: Drive crawl complete. Files found from delta: {_model.AllFiles.Count}");

            // STAGE 1b: re-enqueue files in SQL that never finished analytics
            // (analysis_completed IS NULL). Delta only returns *changes*, so a
            // second run with nothing changed in SP would otherwise leave any
            // previously-unfinished rows abandoned forever.
            var resumed = await EnqueueUnanalyzedFilesFromDbAsync().ConfigureAwait(false);
            if (resumed > 0)
            {
                _logger.LogInformation(
                    $"STAGE 1b: re-queued {resumed} previously-unanalyzed file(s) from DB. " +
                    $"Total files awaiting analytics: {_model.AllFiles.Count}.");
            }

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
    /// Load files from SQL where analysis_completed IS NULL (and we have Graph
    /// IDs to call analytics) and add them to the in-memory model so STAGE 2
    /// will process them. Skips anything already present in the model from
    /// the delta scan (matched by GraphItemId).
    /// </summary>
    /// <returns>Count of files re-enqueued.</returns>
    private async Task<int> EnqueueUnanalyzedFilesFromDbAsync()
    {
        using var db = new SPOColdStorageDbContext(_config);

        // Pull only what we need to rehydrate analytics. AsNoTracking because
        // these rows are projected into in-memory DTOs; we don't want EF
        // change-tracking 80k rows for nothing.
        var unfinished = await db.Files
            .AsNoTracking()
            .Where(f => f.AnalysisCompleted == null
                     && f.GraphItemId != null && f.GraphItemId != ""
                     && f.DriveId != null && f.DriveId != "")
            .Select(f => new
            {
                f.Url,
                f.DriveId,
                f.GraphItemId,
                f.FileSize,
                f.LastModified,
                f.CreatedDate
            })
            .ToListAsync()
            .ConfigureAwait(false);

        // Also count rows that are NULL-analyzed but have no IDs — these are
        // legacy rows from before the schema change; they can't be retried
        // until they're re-discovered by the delta scan (which will UPDATE
        // their IDs via MergeStagingFiles.sql).
        var legacyCount = await db.Files
            .AsNoTracking()
            .CountAsync(f => f.AnalysisCompleted == null
                          && (f.GraphItemId == null || f.GraphItemId == ""
                              || f.DriveId == null || f.DriveId == ""))
            .ConfigureAwait(false);

        if (legacyCount > 0)
        {
            _logger.LogWarning(
                $"{legacyCount} file(s) in DB have NULL analysis_completed but no Graph IDs " +
                $"(pre-migration rows). They will be filled in when SharePoint next reports them " +
                $"as changed (or when DriveDeltaTokens is cleared to force a fresh full scan).");
        }

        if (unfinished.Count == 0) return 0;

        // Build a quick set of GraphItemIds already in the model (from this run's
        // delta scan) so we don't double-add. _model.AllFiles is cached/cheap.
        var inModel = new HashSet<string>(
            _model.AllFiles
                .OfType<DocumentSiteWithMetadata>()
                .Where(d => !string.IsNullOrEmpty(d.GraphItemId))
                .Select(d => d.GraphItemId!));

        // Map known DocLibs in the model by DriveId. The delta scan creates
        // one per processed drive; a re-queued file's DriveId may or may not
        // match — if not, we construct a stub DocLib so AddFile still works.
        var docLibsByDrive = _model.AllDocLibs
            .Where(l => !string.IsNullOrEmpty(l.DriveId))
            .GroupBy(l => l.DriveId)
            .ToDictionary(g => g.Key, g => (DocLib)g.First());

        int added = 0;
        int skippedAlreadyPresent = 0;
        foreach (var row in unfinished)
        {
            if (inModel.Contains(row.GraphItemId!))
            {
                skippedAlreadyPresent++;
                continue;
            }

            if (!docLibsByDrive.TryGetValue(row.DriveId!, out var docLib))
            {
                // No matching DocLib in this run's model (e.g. the drive
                // wasn't returned by delta and we never instantiated it).
                // Create a stub – analytics only needs DriveId + GraphItemId.
                docLib = new DocLib
                {
                    DriveId = row.DriveId!,
                    Title = $"<resumed-drive {row.DriveId}>",
                    ServerRelativeUrl = string.Empty
                };
                docLibsByDrive[row.DriveId!] = docLib;
            }

            var driveItem = new DriveItemSharePointFileInfo
            {
                ServerRelativeFilePath = row.Url ?? string.Empty,
                FileSize = row.FileSize,
                LastModified = row.LastModified,
                CreatedDate = row.CreatedDate,
                DriveId = row.DriveId,
                GraphItemId = row.GraphItemId,
                SiteUrl = _site.RootURL,
                WebUrl = _site.RootURL,
                List = docLib
            };

            var doc = new DocumentSiteWithMetadata(driveItem)
            {
                State = SiteFileAnalysisState.AnalysisPending
            };

            _model.AddFile(doc, docLib);
            added++;
        }

        if (skippedAlreadyPresent > 0)
        {
            _logger.LogDebug(
                $"Skipped {skippedAlreadyPresent} unanalyzed DB row(s) that were already in this run's delta.");
        }

        return added;
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
                var transientCount = _model.DocsByState(SiteFileAnalysisState.TransientError).Count;
                var fatalCount = _model.DocsByState(SiteFileAnalysisState.FatalError).Count;
                Console.WriteLine(
                    $"Have completed {_model.DocsCompleted.Count} of {_model.AllFiles.Count}. " +
                    $"Pending: {filesToLoad.Count} ({transientCount} transient errors to retry, {fatalCount} given up)");
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
        var startedAt = DateTime.UtcNow;
        int previousComplete = 0;
        var previousTickAt = startedAt;

        while (_showStats)
        {
            // Single-pass bucket count over AllFiles to avoid 5 separate
            // DocsByState() scans each tick (each of which allocates a List).
            int pending = 0, inProgress = 0, complete = 0, transient = 0, fatal = 0;
            int totalDocs = 0;
            foreach (var f in _model.AllFiles)
            {
                if (f is not DocumentSiteWithMetadata d) continue;
                totalDocs++;
                switch (d.State)
                {
                    case SiteFileAnalysisState.AnalysisPending: pending++; break;
                    case SiteFileAnalysisState.AnalysisInProgress: inProgress++; break;
                    case SiteFileAnalysisState.Complete: complete++; break;
                    case SiteFileAnalysisState.TransientError: transient++; break;
                    case SiteFileAnalysisState.FatalError: fatal++; break;
                }
            }

            var now = DateTime.UtcNow;
            var tickElapsed = (now - previousTickAt).TotalSeconds;
            var sinceStart = (now - startedAt).TotalSeconds;
            var deltaComplete = complete - previousComplete;
            var instantRate = tickElapsed > 0 ? deltaComplete / tickElapsed : 0;
            var avgRate = sinceStart > 0 ? complete / sinceStart : 0;
            var remaining = pending + inProgress + transient;
            string eta = "?";
            if (avgRate > 0.01 && remaining > 0)
            {
                var etaSec = remaining / avgRate;
                eta = etaSec >= 3600
                    ? $"{(int)(etaSec / 3600)}h {((int)etaSec % 3600) / 60}m"
                    : etaSec >= 60
                        ? $"{(int)(etaSec / 60)}m {(int)etaSec % 60}s"
                        : $"{(int)etaSec}s";
            }

            Console.WriteLine(
                $"Analytics progress: {complete}/{totalDocs} complete " +
                $"(+{deltaComplete} in last {tickElapsed:0}s, " +
                $"{instantRate:0.0}/s now, {avgRate:0.0}/s avg). " +
                $"InProgress: {inProgress}, Pending: {pending}, " +
                $"Transient retry: {transient}, Given up: {fatal}. ETA: {eta}.");

            previousComplete = complete;
            previousTickAt = now;

            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
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

        _logger.LogInformation(
            $"Dispatched {backgroundTasksThisChunk.Count} analytics/version task(s) " +
            $"for {filesToUpdate.Count} files (batch size {batchSize}, " +
            $"max 10 concurrent Graph calls per adapter). Awaiting completion " +
            $"— see 30s heartbeat for progress.");
        var chunkStart = DateTime.UtcNow;

        await Task.WhenAll(backgroundTasksThisChunk).ConfigureAwait(false);

        _logger.LogInformation(
            $"Analytics/version batch finished in " +
            $"{(DateTime.UtcNow - chunkStart).TotalSeconds:N0}s " +
            $"for {filesToUpdate.Count} files.");

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
