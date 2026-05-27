using Microsoft.EntityFrameworkCore;
using Entities;
using Entities.Configuration;
using Migration.Engine.SnapshotBuilder;
using Migration.Engine.Utils;
using Migration.Engine.Utils.Extensions;
using Migration.Engine.Utils.Http;
using Models;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.Adapters;

/// <summary>
/// Graph API implementation of file analytics provider.
/// Retrieves analytics and version data from Microsoft Graph API.
/// </summary>
public class GraphFileAnalyticsAdapter : IFileAnalyticsProvider, IDisposable
{
    private readonly Config _config;
    private readonly string _siteUrl;
    private readonly SecureSPThrottledHttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly SPOColdStorageDbContext _db;

    public GraphFileAnalyticsAdapter(
        Config config,
        string siteUrl,
        ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _siteUrl = siteUrl ?? throw new ArgumentNullException(nameof(siteUrl));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _httpClient = new SecureSPThrottledHttpClient(_config, true, _logger);
        _db = new SPOColdStorageDbContext(_config);
    }

    /// <inheritdoc/>
    public Task<BackgroundUpdate> GetFileAnalyticsAsync(
        IReadOnlyList<DocumentSiteWithMetadata> files,
        CancellationToken cancellationToken = default)
    {
        if (files is List<DocumentSiteWithMetadata> list)
        {
            return list.GetDriveItemsAnalytics(_siteUrl, _httpClient, _logger);
        }

        // Convert to List for extension method compatibility
        var fileList = files.ToList();
        return fileList.GetDriveItemsAnalytics(_siteUrl, _httpClient, _logger);
    }

    /// <inheritdoc/>
    public Task<BackgroundUpdate> GetFileVersionHistoryAsync(
        IReadOnlyList<DocumentSiteWithMetadata> files,
        CancellationToken cancellationToken = default)
    {
        if (files is List<DocumentSiteWithMetadata> list)
        {
            return list.GetDriveItemsHistory(_siteUrl, _httpClient, _logger);
        }

        // Convert to List for extension method compatibility
        var fileList = files.ToList();
        return fileList.GetDriveItemsHistory(_siteUrl, _httpClient, _logger);
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
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
