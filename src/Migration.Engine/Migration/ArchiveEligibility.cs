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
/// The runtime-editable file-extension archiving policy: an exclude (deny) set
/// and an optional include (allow) set, both of normalised extensions (leading
/// dot, lower-case). Supplied separately from <see cref="Config"/> so an admin can
/// change which file types are archived without a redeploy. <c>.url</c> is NOT
/// carried here — it is always excluded in <see cref="ArchiveEligibilityEvaluator"/>.
/// </summary>
public sealed record ArchiveExtensionPolicy(IReadOnlySet<string> Excluded, IReadOnlySet<string> Included)
{
    public static readonly ArchiveExtensionPolicy Empty =
        new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Supplies the current runtime extension policy. Abstracted so the evaluator can
/// be unit-tested without a database and so the live implementation can cache DB
/// reads. Returns <see cref="ArchiveExtensionPolicy.Empty"/> (not null) when no
/// rules exist, so the evaluator simply falls back to its config baseline.
/// </summary>
public interface IArchiveExtensionPolicySource
{
    Task<ArchiveExtensionPolicy> GetPolicyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Supplies a file's persisted read-activity signal (all-time access count from
/// indexer analytics) so heavily-read files can be kept out of cold storage
/// (issue #11). Returns null when no signal is available — the rule then does
/// not block, since absence of data must not cause a wrong archive decision.
/// </summary>
public interface IFileReadActivitySource
{
    Task<int?> GetAccessCountAsync(ArchiveCandidate candidate, CancellationToken cancellationToken = default);
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
    private readonly IArchiveExtensionPolicySource? _extensionPolicySource;
    private readonly IFileReadActivitySource? _readActivitySource;
    private readonly int _maxAccessCount;

    public ArchiveEligibilityEvaluator(Config config, IArchiveExclusionSource? exclusionSource = null, IFileReadActivitySource? readActivitySource = null, IArchiveExtensionPolicySource? extensionPolicySource = null)
        : this(
            (config ?? throw new ArgumentNullException(nameof(config))).ColdStorageMinFileSizeBytes,
            config.ColdStorageExcludedExtensions,
            config.ColdStorageIncludedExtensions,
            exclusionSource,
            readActivitySource,
            config.ColdStorageMaxAccessCount,
            extensionPolicySource)
    {
    }

    /// <summary>
    /// Explicit-values constructor (used directly by tests). <paramref name="minSizeBytes"/>
    /// of 0 or less disables the size floor; <paramref name="maxAccessCount"/> of 0 or
    /// less disables the read-activity rule.
    /// </summary>
    public ArchiveEligibilityEvaluator(
        long minSizeBytes,
        string? excludedExtensions,
        string? includedExtensions,
        IArchiveExclusionSource? exclusionSource = null,
        IFileReadActivitySource? readActivitySource = null,
        int maxAccessCount = 0,
        IArchiveExtensionPolicySource? extensionPolicySource = null)
    {
        _minSizeBytes = minSizeBytes > 0 ? minSizeBytes : 0;
        _excluded = ParseExtensions(excludedExtensions);
        // A .url file is a cold-storage placeholder. Archiving one would create a
        // nested/duplicate placeholder and, on a folder re-migration, "archive the
        // archive". Always exclude it regardless of configuration — including the
        // runtime extension policy below — so nothing can ever turn this safety off.
        _excluded.Add(".url");
        _included = ParseExtensions(includedExtensions);
        _exclusionSource = exclusionSource;
        _extensionPolicySource = extensionPolicySource;
        _readActivitySource = readActivitySource;
        _maxAccessCount = maxAccessCount > 0 ? maxAccessCount : 0;
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

        // Runtime, admin-editable extension policy (DB) layered on top of the
        // config baseline. .url stays in _excluded no matter what, so a placeholder
        // can never become archivable by editing rules in the UI.
        var extPolicy = _extensionPolicySource is null
            ? ArchiveExtensionPolicy.Empty
            : await _extensionPolicySource.GetPolicyAsync(cancellationToken).ConfigureAwait(false);

        var includeActive = _included.Count > 0 || extPolicy.Included.Count > 0;
        if (includeActive)
        {
            var isIncluded = _included.Contains(ext) || extPolicy.Included.Contains(ext);
            if (ext.Length == 0 || !isIncluded)
            {
                return ArchiveEligibilityResult.Skip(
                    $"file type '{DisplayExtension(ext)}' is not in the archive include-list");
            }
        }

        if (ext.Length > 0 && (_excluded.Contains(ext) || extPolicy.Excluded.Contains(ext)))
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

        // Read-activity rule (issue #11): keep heavily-read documents out of cold
        // storage even when they're rarely edited. Uses the persisted all-time
        // access count; absence of a signal never blocks.
        if (_readActivitySource is not null && _maxAccessCount > 0)
        {
            var accessCount = await _readActivitySource.GetAccessCountAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (accessCount.HasValue && accessCount.Value > _maxAccessCount)
            {
                return ArchiveEligibilityResult.Skip(
                    $"file has high read activity (access count {accessCount.Value} > {_maxAccessCount}); kept available rather than archived");
            }
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
