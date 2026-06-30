using Entities;
using Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Migration.Engine.Migration;

/// <summary>
/// Reads a file's persisted all-time access count from the indexer's
/// <c>files</c> table to back the read-activity eligibility rule (issue #11).
///
/// The files table is keyed by the full SharePoint URL, while a candidate only
/// carries the server-relative URL. Because SharePoint hosts have no path
/// component, the full URL always ends with the server-relative URL, so we match
/// on that suffix (optionally narrowed to the candidate's host). Returns null
/// when there's no indexed analytics for the file — the rule then doesn't block.
/// </summary>
public sealed class DbFileReadActivitySource : IFileReadActivitySource
{
    private readonly Config _config;
    private readonly ILogger? _logger;

    public DbFileReadActivitySource(Config config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    public async Task<int?> GetAccessCountAsync(ArchiveCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var serverRelativeUrl = candidate.ServerRelativeUrl;
        if (string.IsNullOrEmpty(serverRelativeUrl))
        {
            return null;
        }

        try
        {
            using var db = new SPOColdStorageDbContext(_config);
            var query = db.Files.Where(f => f.Url.EndsWith(serverRelativeUrl));

            var host = ExtractHost(candidate.SiteUrl);
            if (!string.IsNullOrEmpty(host))
            {
                query = query.Where(f => f.Url.Contains(host));
            }

            // Prefer the most recently analysed row when multiple match.
            return await query
                .OrderByDescending(f => f.AnalysisCompleted)
                .Select(f => f.AccessCount)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No signal => don't block archiving on a read error.
            _logger?.LogWarning(ex, "Failed to read access count for '{Url}'; treating as no signal.", serverRelativeUrl);
            return null;
        }
    }

    private static string ExtractHost(string? siteUrl)
        => !string.IsNullOrWhiteSpace(siteUrl) && Uri.TryCreate(siteUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : string.Empty;
}
