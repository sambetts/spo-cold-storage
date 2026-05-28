using Migration.Engine.Adapters;
using Migration.Engine.SnapshotBuilder;
using Models;

namespace Migration.Engine.Tests.Adapters;

/// <summary>
/// Test implementation of IFileAnalyticsProvider for unit testing.
/// Provides configurable behavior for testing different scenarios.
/// </summary>
public class TestFileAnalyticsAdapter : IFileAnalyticsProvider
{
    private readonly Dictionary<string, ItemAnalyticsResponse.AnalyticsItemActionStat> _analyticsData = [];
    private readonly Dictionary<string, DriveItemVersionInfo> _versionData = [];
    private readonly HashSet<string> _skipFiles = [];
    private int _analyticsCallCount;
    private int _versionCallCount;
    private int _skipCheckCount;

    /// <summary>
    /// Gets the number of times GetFileAnalyticsAsync was called.
    /// </summary>
    public int AnalyticsCallCount => _analyticsCallCount;

    /// <summary>
    /// Gets the number of times GetFileVersionHistoryAsync was called.
    /// </summary>
    public int VersionCallCount => _versionCallCount;

    /// <summary>
    /// Gets the number of times ShouldSkipFileAnalysisAsync was called.
    /// </summary>
    public int SkipCheckCount => _skipCheckCount;

    /// <summary>
    /// Configures analytics data for a specific file.
    /// </summary>
    public void SetAnalyticsData(string graphItemId, ItemAnalyticsResponse.AnalyticsItemActionStat stats)
    {
        _analyticsData[graphItemId] = stats;
    }

    /// <summary>
    /// Configures version history for a specific file.
    /// </summary>
    public void SetVersionData(string graphItemId, DriveItemVersionInfo versionInfo)
    {
        _versionData[graphItemId] = versionInfo;
    }

    /// <summary>
    /// Configures a file to be skipped during analysis.
    /// </summary>
    public void SetFileToSkip(string fullSharePointUrl)
    {
        _skipFiles.Add(fullSharePointUrl);
    }

    /// <summary>
    /// Resets all call counters.
    /// </summary>
    public void ResetCounters()
    {
        _analyticsCallCount = 0;
        _versionCallCount = 0;
        _skipCheckCount = 0;
    }

    public Task<BackgroundUpdate> GetFileAnalyticsAsync(
        IReadOnlyList<DocumentSiteWithMetadata> files,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _analyticsCallCount);

        var results = new Dictionary<DocumentSiteWithMetadata, object>();

        foreach (var file in files)
        {
            if (file.GraphItemId is not null && _analyticsData.TryGetValue(file.GraphItemId, out var stats))
            {
                var response = new ItemAnalyticsResponse { AccessStats = stats };
                results[file] = response;
                file.State = SiteFileAnalysisState.Complete;
            }
            else
            {
                // Default empty analytics
                var response = new ItemAnalyticsResponse { AccessStats = new ItemAnalyticsResponse.AnalyticsItemActionStat() };
                results[file] = response;
                file.State = SiteFileAnalysisState.Complete;
            }
        }

        return Task.FromResult(new BackgroundUpdate { UpdateResults = results });
    }

    public Task<BackgroundUpdate> GetFileVersionHistoryAsync(
        IReadOnlyList<DocumentSiteWithMetadata> files,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _versionCallCount);

        var results = new Dictionary<DocumentSiteWithMetadata, object>();

        foreach (var file in files)
        {
            if (file.GraphItemId is not null && _versionData.TryGetValue(file.GraphItemId, out var versionInfo))
            {
                results[file] = versionInfo;
                file.State = SiteFileAnalysisState.Complete;
            }
            else
            {
                // Default single version
                var defaultVersion = new DriveItemVersionInfo
                {
                    Versions = 
                    [
                        new DriveItemVersion 
                        { 
                            Id = "1.0", 
                            LastModifiedDateTime = DateTime.UtcNow,
                            Size = 1024
                        }
                    ]
                };
                results[file] = defaultVersion;
                file.State = SiteFileAnalysisState.Complete;
            }
        }

        return Task.FromResult(new BackgroundUpdate { UpdateResults = results });
    }

    public Task<bool> ShouldSkipFileAnalysisAsync(
        DriveItemSharePointFileInfo fileInfo,
        int skipHours,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _skipCheckCount);
        return Task.FromResult(_skipFiles.Contains(fileInfo.FullSharePointUrl));
    }
}
