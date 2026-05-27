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

            var analytics = await _graphClient
                .Drives[fileToUpdate.DriveId]
                .Items[fileToUpdate.GraphItemId]
                .Analytics
                .AllTime
                .GetAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

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
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
        {
            fileToUpdate.State = ex.ResponseStatusCode == 429
                ? SiteFileAnalysisState.TransientError
                : SiteFileAnalysisState.FatalError;
            _logger.LogError(ex, $"Graph error getting analytics for drive item {fileToUpdate.GraphItemId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            fileToUpdate.State = SiteFileAnalysisState.FatalError;
            _logger.LogError(ex, $"Error getting analytics for drive item {fileToUpdate.GraphItemId}: {ex.Message}");
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

            var versionsResponse = await _graphClient
                .Drives[fileToUpdate.DriveId]
                .Items[fileToUpdate.GraphItemId]
                .Versions
                .GetAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

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
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
        {
            fileToUpdate.State = ex.ResponseStatusCode == 429
                ? SiteFileAnalysisState.TransientError
                : SiteFileAnalysisState.FatalError;
            _logger.LogError(ex, $"Graph error getting versions for drive item {fileToUpdate.GraphItemId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting versions for drive item {fileToUpdate.GraphItemId}: {ex.Message}");
        }
        finally
        {
            _rateLimiter.Release();
        }
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
