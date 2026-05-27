using Migration.Engine.SnapshotBuilder;
using Migration.Engine.Utils.Http;
using Models;
using System.Text.Json;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.Utils;

public static class GraphFileInfoListExtensions
{
    private static readonly SemaphoreSlim _rateLimiter = new(10, 10); // Max 10 concurrent requests

    public static Task<BackgroundUpdate> GetDriveItemsAnalytics(
        this List<DocumentSiteWithMetadata> graphFiles,
        string baseSiteAddress,
        SecureSPThrottledHttpClient httpClient,
        ILogger logger)
    {
        return GetDriveItemsAnalyticsCore(graphFiles, baseSiteAddress, httpClient, logger);
    }

    private static async Task<BackgroundUpdate> GetDriveItemsAnalyticsCore(
        List<DocumentSiteWithMetadata> graphFiles,
        string baseSiteAddress,
        SecureSPThrottledHttpClient httpClient,
        ILogger logger)
    {
        var fileSuccessResults = new Dictionary<DocumentSiteWithMetadata, object>(graphFiles.Count);
        var tasks = graphFiles.Select(file => ProcessAnalyticsAsync(file, baseSiteAddress, httpClient, logger, fileSuccessResults));

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return new BackgroundUpdate { UpdateResults = fileSuccessResults };
    }

    private static async Task ProcessAnalyticsAsync(
        DocumentSiteWithMetadata fileToUpdate,
        string baseSiteAddress,
        SecureSPThrottledHttpClient httpClient,
        ILogger logger,
        Dictionary<DocumentSiteWithMetadata, object> results)
    {
        await _rateLimiter.WaitAsync().ConfigureAwait(false);
        try
        {
            var url = $"{baseSiteAddress}/_api/v2.0/drives/{fileToUpdate.DriveId}/items/{fileToUpdate.GraphItemId}/analytics/allTime";

            try
            {
                using var analyticsResponse = await httpClient.GetAsyncWithThrottleRetries(url, logger).ConfigureAwait(false);
                var analyticsResponseBody = await analyticsResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                analyticsResponse.EnsureSuccessStatusCode();
                var activitiesResponse = JsonSerializer.Deserialize<ItemAnalyticsResponse>(analyticsResponseBody) ?? new ItemAnalyticsResponse();
                fileToUpdate.State = SiteFileAnalysisState.Complete;

                lock (results)
                {
                    results.Add(fileToUpdate, activitiesResponse);
                }
            }
            catch (HttpRequestException ex)
            {
                fileToUpdate.State = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? SiteFileAnalysisState.TransientError
                    : SiteFileAnalysisState.FatalError;

                logger.LogError(ex, "Unhandled exception");
                logger.LogError($"Got HTTP exception {ex.Message} getting analytics data for drive item {fileToUpdate.GraphItemId}");
            }
            catch (Exception ex)
            {
                fileToUpdate.State = SiteFileAnalysisState.FatalError;
                logger.LogError(ex, "Unhandled exception");
                logger.LogError($"Got general exception {ex.Message} getting analytics data for drive item {fileToUpdate.GraphItemId}");
            }
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    public static Task<BackgroundUpdate> GetDriveItemsHistory(
        this List<DocumentSiteWithMetadata> graphFiles,
        string baseSiteAddress,
        SecureSPThrottledHttpClient httpClient,
        ILogger logger)
    {
        return GetDriveItemsHistoryCore(graphFiles, baseSiteAddress, httpClient, logger);
    }

    private static async Task<BackgroundUpdate> GetDriveItemsHistoryCore(
        List<DocumentSiteWithMetadata> graphFiles,
        string baseSiteAddress,
        SecureSPThrottledHttpClient httpClient,
        ILogger logger)
    {
        var fileSuccessResults = new Dictionary<DocumentSiteWithMetadata, object>(graphFiles.Count);
        var tasks = graphFiles.Select(file => ProcessHistoryAsync(file, baseSiteAddress, httpClient, logger, fileSuccessResults));

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return new BackgroundUpdate { UpdateResults = fileSuccessResults };
    }

    private static async Task ProcessHistoryAsync(
        DocumentSiteWithMetadata fileToUpdate,
        string baseSiteAddress,
        SecureSPThrottledHttpClient httpClient,
        ILogger logger,
        Dictionary<DocumentSiteWithMetadata, object> results)
    {
        await _rateLimiter.WaitAsync().ConfigureAwait(false);
        try
        {
            var url = $"{baseSiteAddress}/_api/v2.0/drives/{fileToUpdate.DriveId}/items/{fileToUpdate.GraphItemId}/versions";

            try
            {
                using var analyticsResponse = await httpClient.GetAsyncWithThrottleRetries(url, logger).ConfigureAwait(false);
                var analyticsResponseBody = await analyticsResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                analyticsResponse.EnsureSuccessStatusCode();
                var activitiesResponse = JsonSerializer.Deserialize<DriveItemVersionInfo>(analyticsResponseBody) ?? new DriveItemVersionInfo();
                fileToUpdate.State = SiteFileAnalysisState.Complete;

                lock (results)
                {
                    results.Add(fileToUpdate, activitiesResponse);
                }
            }
            catch (HttpRequestException ex)
            {
                fileToUpdate.State = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? SiteFileAnalysisState.TransientError
                    : SiteFileAnalysisState.FatalError;

                logger.LogError(ex, "Unhandled exception");
                logger.LogError($"Got HTTP exception {ex.Message} getting version data for drive item {fileToUpdate.GraphItemId}");
            }
            catch (Exception ex)
            {
                fileToUpdate.State = SiteFileAnalysisState.FatalError;
                logger.LogError(ex, "Unhandled exception");
                logger.LogError($"Got general exception {ex.Message} getting version data for drive item {fileToUpdate.GraphItemId}");
            }
        }
        finally
        {
            _rateLimiter.Release();
        }
    }
}
