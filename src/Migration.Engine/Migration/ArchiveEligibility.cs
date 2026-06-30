using Entities.Configuration;
using Models.ColdStorage;

namespace Migration.Engine.Migration;

/// <summary>
/// Outcome of an archive-eligibility check. When <see cref="IsEligible"/> is
/// false, <see cref="SkipReason"/> carries a human-readable explanation that is
/// surfaced to the user and written to the job log.
/// </summary>
public sealed record ArchiveEligibilityResult(bool IsEligible, string? SkipReason)
{
    public static readonly ArchiveEligibilityResult Eligible = new(true, null);

    public static ArchiveEligibilityResult Skip(string reason) => new(false, reason);
}

/// <summary>
/// The minimal set of facts the eligibility rules need about a candidate item.
/// Constructible both from the queue-time request DTO and from the worker's bus
/// envelope so the same rules run at submit time and in the pipeline.
/// </summary>
public sealed record ArchiveCandidate
{
    public string ServerRelativeUrl { get; init; } = string.Empty;
    public string SiteUrl { get; init; } = string.Empty;
    public string WebUrl { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public ColdStorageItemKind ItemKind { get; init; } = ColdStorageItemKind.File;
    public DateTime? LastModified { get; init; }
    public string? DriveId { get; init; }
    public string? GraphItemId { get; init; }
}

/// <summary>
/// Decides whether a SharePoint item should be archived to cold storage.
/// Returns a skip reason rather than throwing so callers can log + surface it.
/// Implementations are expected to be cheap and side-effect free.
/// </summary>
public interface IArchiveEligibilityEvaluator
{
    Task<ArchiveEligibilityResult> EvaluateAsync(ArchiveCandidate candidate, CancellationToken cancellationToken = default);
}

/// <summary>
/// A single archiving exclusion scope (issue #7): an entire site collection
/// (<see cref="SiteUrl"/>) and/or a server-relative path subtree
/// (<see cref="ServerRelativePrefix"/>).
/// </summary>
public sealed record ArchiveExclusion(string? SiteUrl, string? ServerRelativePrefix);

/// <summary>
/// Supplies the active archiving exclusion scopes. Abstracted so the evaluator
/// can be unit-tested without a database and so the live implementation can
/// cache DB reads.
/// </summary>
public interface IArchiveExclusionSource
{
    Task<IReadOnlyList<ArchiveExclusion>> GetActiveExclusionsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Config-driven eligibility rules (issue #2): a minimum file-size floor plus
/// include/exclude file-extension lists. Tunable via app settings with no code
/// change. This is the foundation that later issues (exclusion scopes, read
/// activity, legal hold) layer additional rules onto.
/// </summary>
public sealed class ArchiveEligibilityEvaluator : IArchiveEligibilityEvaluator
{
    private readonly long _minSizeBytes;
    private readonly HashSet<string> _excluded;
    private readonly HashSet<string> _included;
    private readonly IArchiveExclusionSource? _exclusionSource;

    public ArchiveEligibilityEvaluator(Config config, IArchiveExclusionSource? exclusionSource = null)
        : this(
            (config ?? throw new ArgumentNullException(nameof(config))).ColdStorageMinFileSizeBytes,
            config.ColdStorageExcludedExtensions,
            config.ColdStorageIncludedExtensions,
            exclusionSource)
    {
    }

    /// <summary>
    /// Explicit-values constructor (used directly by tests). <paramref name="minSizeBytes"/>
    /// of 0 or less disables the size floor.
    /// </summary>
    public ArchiveEligibilityEvaluator(long minSizeBytes, string? excludedExtensions, string? includedExtensions, IArchiveExclusionSource? exclusionSource = null)
    {
        _minSizeBytes = minSizeBytes > 0 ? minSizeBytes : 0;
        _excluded = ParseExtensions(excludedExtensions);
        _included = ParseExtensions(includedExtensions);
        _exclusionSource = exclusionSource;
    }

    public async Task<ArchiveEligibilityResult> EvaluateAsync(ArchiveCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // Exclusion scopes (issue #7) apply to files AND folders — a folder under
        // an excluded scope must not be expanded/archived either.
        if (_exclusionSource is not null)
        {
            var exclusions = await _exclusionSource.GetActiveExclusionsAsync(cancellationToken).ConfigureAwait(false);
            var matched = MatchExclusion(candidate, exclusions);
            if (matched is not null)
            {
                return ArchiveEligibilityResult.Skip($"under excluded scope '{matched}'");
            }
        }

        // Folders are expanded to their constituent files by the worker; the
        // per-file rules apply to those, so a folder selection is itself eligible.
        if (candidate.ItemKind == ColdStorageItemKind.Folder)
        {
            return ArchiveEligibilityResult.Eligible;
        }

        var ext = NormalizeExtension(candidate.ServerRelativeUrl);

        if (_included.Count > 0 && (ext.Length == 0 || !_included.Contains(ext)))
        {
            return ArchiveEligibilityResult.Skip(
                $"file type '{DisplayExtension(ext)}' is not in the archive include-list");
        }

        if (ext.Length > 0 && _excluded.Contains(ext))
        {
            return ArchiveEligibilityResult.Skip(
                $"file type '{ext}' is excluded from archiving");
        }

        // Only enforce the size floor when we actually know the size (> 0), so a
        // request that omitted the size isn't skipped on incomplete data.
        if (_minSizeBytes > 0 && candidate.FileSizeBytes > 0 && candidate.FileSizeBytes < _minSizeBytes)
        {
            return ArchiveEligibilityResult.Skip(
                $"file is {candidate.FileSizeBytes:N0} bytes, below the {_minSizeBytes:N0}-byte minimum archive size");
        }

        return ArchiveEligibilityResult.Eligible;
    }

    /// <summary>
    /// Returns the matched exclusion scope string, or null when the candidate is
    /// not under any excluded site/path.
    /// </summary>
    private static string? MatchExclusion(ArchiveCandidate candidate, IReadOnlyList<ArchiveExclusion> exclusions)
    {
        foreach (var ex in exclusions)
        {
            if (!string.IsNullOrEmpty(ex.SiteUrl)
                && string.Equals(candidate.SiteUrl, ex.SiteUrl, StringComparison.OrdinalIgnoreCase))
            {
                return ex.SiteUrl;
            }
            if (!string.IsNullOrEmpty(ex.ServerRelativePrefix)
                && IsAtOrUnder(candidate.ServerRelativeUrl, ex.ServerRelativePrefix))
            {
                return ex.ServerRelativePrefix;
            }
        }
        return null;
    }

    /// <summary>
    /// Segment-aware, case-insensitive prefix check: <c>/a/b</c> matches
    /// <c>/a/b</c> and <c>/a/b/c.docx</c> but not <c>/a/bc.docx</c>.
    /// </summary>
    private static bool IsAtOrUnder(string url, string prefix)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }
        var p = prefix.TrimEnd('/');
        if (p.Length == 0)
        {
            return false;
        }
        return url.Equals(p, StringComparison.OrdinalIgnoreCase)
            || url.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> ParseExtensions(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return set;
        }
        foreach (var part in raw.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var e = part.StartsWith('.') ? part : "." + part;
            set.Add(e.ToLowerInvariant());
        }
        return set;
    }

    private static string NormalizeExtension(string serverRelativeUrl)
        => Path.GetExtension(serverRelativeUrl ?? string.Empty).ToLowerInvariant();

    private static string DisplayExtension(string ext) => ext.Length == 0 ? "(no extension)" : ext;
}
