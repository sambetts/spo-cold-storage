using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Entities;
using Entities.Configuration;
using Migration.Engine.SnapshotBuilder;
using Models;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.Adapters;

/// <summary>
/// Graph API implementation of file analytics provider.
/// Uses the Microsoft Graph SDK with Graph-only scope (no SharePoint API permissions needed).
/// </summary>
public class GraphFileAnalyticsAdapter : IFileAnalyticsProvider, IDisposable
{
    // Inline retries inside a single ProcessAnalyticsAsync / ProcessVersionsAsync call,
    // separated by exponential backoff. These absorb transient Graph errors locally
    // so they don't bounce back to the outer "TransientError" retry loop.
    private const int InlineRetryAttempts = 3;
    private const int InitialBackoffMs = 500;

    // After this many outer-loop retries (i.e. times the file came back through the
    // analytics adapter with the inline retries exhausted), give up on the file and
    // mark it FatalError so the loop terminates.
    private const int OuterRetryLimit = 3;

    private readonly Config _config;
    private readonly string _siteUrl;
    private readonly GraphServiceClient _graphClient;
    private readonly ILogger _logger;
    private readonly SPOColdStorageDbContext _db;
    private readonly SemaphoreSlim _rateLimiter = new(10, 10);

    public GraphFileAnalyticsAdapter(
        Config config,
        string siteUrl,
        ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _siteUrl = siteUrl ?? throw new ArgumentNullException(nameof(siteUrl));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var credential = new Azure.Identity.ClientSecretCredential(
            _config.AzureAdConfig.TenantId,
            _config.AzureAdConfig.ClientID,
            _config.AzureAdConfig.Secret
        );
        _graphClient = new GraphServiceClient(credential);
        _db = new SPOColdStorageDbContext(_config);
    }

    /// <inheritdoc/>
    public async Task<BackgroundUpdate> GetFileAnalyticsAsync(
        IReadOnlyList<DocumentSiteWithMetadata> files,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<DocumentSiteWithMetadata, object>(files.Count);
        var tasks = files.Select(file => ProcessAnalyticsAsync(file, results, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new BackgroundUpdate { UpdateResults = results };
    }

    /// <inheritdoc/>
    public async Task<BackgroundUpdate> GetFileVersionHistoryAsync(
        IReadOnlyList<DocumentSiteWithMetadata> files,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<DocumentSiteWithMetadata, object>(files.Count);
        var tasks = files.Select(file => ProcessVersionsAsync(file, results, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new BackgroundUpdate { UpdateResults = results };
    }

    private async Task ProcessAnalyticsAsync(
        DocumentSiteWithMetadata fileToUpdate,
        Dictionary<DocumentSiteWithMetadata, object> results,
        CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(fileToUpdate.DriveId) || string.IsNullOrEmpty(fileToUpdate.GraphItemId))
            {
                fileToUpdate.State = SiteFileAnalysisState.FatalError;
                return;
            }

            ItemActivityStat? analytics = null;
            Exception? lastError = null;

            for (var attempt = 1; attempt <= InlineRetryAttempts; attempt++)
            {
                try
                {
                    analytics = await _graphClient
                        .Drives[fileToUpdate.DriveId]
                        .Items[fileToUpdate.GraphItemId]
                        .Analytics
                        .AllTime
                        .GetAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    lastError = null;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    lastError = ex;
                    if (attempt == InlineRetryAttempts) break;
                    await Task.Delay(ComputeBackoff(attempt, ex), cancellationToken).ConfigureAwait(false);
                }
            }

            if (lastError != null)
            {
                HandleAnalyticsFailure(fileToUpdate, lastError, "analytics");
                return;
            }

            var response = new ItemAnalyticsResponse
            {
                AccessStats = new ItemAnalyticsResponse.AnalyticsItemActionStat
                {
                    ActionCount = analytics?.Access?.ActionCount ?? 0,
                    ActorCount = analytics?.Access?.ActorCount ?? 0
                }
            };

            fileToUpdate.State = SiteFileAnalysisState.Complete;
            lock (results)
            {
                results[fileToUpdate] = response;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            fileToUpdate.State = SiteFileAnalysisState.FatalError;
            _logger.LogError(ex, $"Unexpected error getting analytics for drive item {fileToUpdate.GraphItemId}: {ex.Message}");
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    private async Task ProcessVersionsAsync(
        DocumentSiteWithMetadata fileToUpdate,
        Dictionary<DocumentSiteWithMetadata, object> results,
        CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(fileToUpdate.DriveId) || string.IsNullOrEmpty(fileToUpdate.GraphItemId))
            {
                return;
            }

            DriveItemVersionCollectionResponse? versionsResponse = null;
            Exception? lastError = null;

            for (var attempt = 1; attempt <= InlineRetryAttempts; attempt++)
            {
                try
                {
                    versionsResponse = await _graphClient
                        .Drives[fileToUpdate.DriveId]
                        .Items[fileToUpdate.GraphItemId]
                        .Versions
                        .GetAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    lastError = null;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    lastError = ex;
                    if (attempt == InlineRetryAttempts) break;
                    await Task.Delay(ComputeBackoff(attempt, ex), cancellationToken).ConfigureAwait(false);
                }
            }

            if (lastError != null)
            {
                HandleAnalyticsFailure(fileToUpdate, lastError, "versions");
                return;
            }

            var versionInfo = new DriveItemVersionInfo();
            if (versionsResponse?.Value != null)
            {
                foreach (var v in versionsResponse.Value)
                {
                    versionInfo.Versions.Add(new Models.DriveItemVersion
                    {
                        Id = v.Id ?? string.Empty,
                        LastModifiedDateTime = v.LastModifiedDateTime?.UtcDateTime ?? DateTime.MinValue,
                        Size = v.Size
                    });
                }
            }

            lock (results)
            {
                results[fileToUpdate] = versionInfo;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Unexpected error getting versions for drive item {fileToUpdate.GraphItemId}: {ex.Message}");
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    /// <summary>
    /// True if the exception represents a recoverable Graph/network condition that
    /// should be retried (HTTP 408/429/500/502/503/504, request timeouts, network errors).
    /// </summary>
    private static bool IsTransient(Exception ex)
    {
        if (ex is Microsoft.Graph.Models.ODataErrors.ODataError odata)
        {
            return odata.ResponseStatusCode == 408   // request timeout
                || odata.ResponseStatusCode == 429   // throttled
                || odata.ResponseStatusCode == 500   // server error (incl. "General exception while processing")
                || odata.ResponseStatusCode == 502
                || odata.ResponseStatusCode == 503
                || odata.ResponseStatusCode == 504;
        }
        // HttpClient.Timeout surfaces as TaskCanceledException with no cancellation requested.
        if (ex is TaskCanceledException) return true;
        if (ex is HttpRequestException) return true;
        if (ex is System.IO.IOException) return true;
        return false;
    }

    /// <summary>
    /// Compute backoff delay. Honors Retry-After header when present on ODataError,
    /// otherwise exponential backoff with jitter (~0.5s, 1s, 2s, ...).
    /// </summary>
    private static TimeSpan ComputeBackoff(int attempt, Exception ex)
    {
        if (ex is Microsoft.Graph.Models.ODataErrors.ODataError odata
            && odata.ResponseHeaders != null
            && odata.ResponseHeaders.TryGetValue("Retry-After", out var values))
        {
            foreach (var v in values)
            {
                if (int.TryParse(v, out var seconds) && seconds > 0 && seconds <= 600)
                {
                    return TimeSpan.FromSeconds(seconds);
                }
            }
        }

        var ms = InitialBackoffMs * (int)Math.Pow(2, attempt - 1);
        ms += Random.Shared.Next(0, 250); // jitter
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>
    /// Apply state + per-file retry-count bookkeeping to a file whose inline retries
    /// have been exhausted. Files that exceed OuterRetryLimit are marked FatalError
    /// so the outer loop stops retrying them.
    /// </summary>
    private void HandleAnalyticsFailure(DocumentSiteWithMetadata file, Exception ex, string kind)
    {
        if (IsTransient(ex))
        {
            file.AnalyticsRetryCount++;
            var reason = ShortReason(ex);
            if (file.AnalyticsRetryCount >= OuterRetryLimit)
            {
                file.State = SiteFileAnalysisState.FatalError;
                _logger.LogWarning(
                    $"Giving up on {kind} for drive item {file.GraphItemId} after {file.AnalyticsRetryCount} attempts: {reason}");
            }
            else
            {
                file.State = SiteFileAnalysisState.TransientError;
                _logger.LogDebug(
                    $"Transient Graph error fetching {kind} for drive item {file.GraphItemId} (attempt {file.AnalyticsRetryCount}/{OuterRetryLimit}): {reason}");
            }
        }
        else
        {
            file.State = SiteFileAnalysisState.FatalError;
            _logger.LogError(ex,
                $"Fatal error fetching {kind} for drive item {file.GraphItemId}: {ex.Message}");
        }
    }

    private static string ShortReason(Exception ex)
    {
        if (ex is Microsoft.Graph.Models.ODataErrors.ODataError odata)
        {
            return $"HTTP {odata.ResponseStatusCode} {odata.Message ?? ex.Message}";
        }
        if (ex is TaskCanceledException) return $"timeout ({ex.Message})";
        return $"{ex.GetType().Name}: {ex.Message}";
    }

    /// <inheritdoc/>
    public async Task<bool> ShouldSkipFileAnalysisAsync(
        DriveItemSharePointFileInfo fileInfo,
        int skipHours,
        CancellationToken cancellationToken = default)
    {
        var existingFile = await _db.Files
            .Where(f => f.Url == fileInfo.FullSharePointUrl)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existingFile?.AnalysisCompleted != null)
        {
            var cutoffDate = DateTime.Now.AddHours(-skipHours);
            if (existingFile.AnalysisCompleted.Value > cutoffDate)
            {
                _logger.LogInformation($"Skipping analysis for {fileInfo.ServerRelativeFilePath} - already analyzed at {existingFile.AnalysisCompleted.Value}");
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        _db?.Dispose();
        _rateLimiter?.Dispose();
    }
}
