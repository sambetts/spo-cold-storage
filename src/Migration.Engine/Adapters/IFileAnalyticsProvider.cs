using Migration.Engine.SnapshotBuilder;
using Models;

namespace Migration.Engine.Adapters;

/// <summary>
/// Interface for providing file analytics and version history data.
/// Abstracts the data source (Graph API, mock data, etc.) from the business logic.
/// </summary>
public interface IFileAnalyticsProvider
{
    /// <summary>
    /// Gets analytics data for a batch of files.
    /// </summary>
    /// <param name="files">List of files to get analytics for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Background update with analytics results</returns>
    Task<BackgroundUpdate> GetFileAnalyticsAsync(
        IReadOnlyList<DocumentSiteWithMetadata> files,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets version history for a batch of files.
    /// </summary>
    /// <param name="files">List of files to get version history for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Background update with version history results</returns>
    Task<BackgroundUpdate> GetFileVersionHistoryAsync(
        IReadOnlyList<DocumentSiteWithMetadata> files,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a file should be skipped based on recent analysis.
    /// </summary>
    /// <param name="fileInfo">File information</param>
    /// <param name="skipHours">Hours to skip if recently analyzed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if file should be skipped</returns>
    Task<bool> ShouldSkipFileAnalysisAsync(
        DriveItemSharePointFileInfo fileInfo,
        int skipHours,
        CancellationToken cancellationToken = default);
}
