namespace Migration.Engine.Migration;

/// <summary>
/// What to do when the file being migrated already has a blob in cold storage.
/// </summary>
public enum BlobConflictDecision
{
    /// <summary>Nothing exists at the destination yet — copy normally.</summary>
    Copy,

    /// <summary>The existing archive is OLDER than the source — overwrite it, then place the placeholder.</summary>
    Overwrite,

    /// <summary>
    /// The existing archive is the SAME version as the source — skip the (throttle-heavy)
    /// re-copy but still replace the source with a placeholder.
    /// </summary>
    SkipSameVersion,

    /// <summary>
    /// The existing archive is NEWER than the source we've been asked to archive — an anomaly
    /// (e.g. the source was reverted). Refuse: never overwrite a newer archive and never delete
    /// the source.
    /// </summary>
    DestinationNewer,
}

/// <summary>
/// Pure, unit-testable core of the migrate conflict rule (kept free of blob I/O). Given the
/// live source's last-modified time and the source-modified time recorded on an already-existing
/// cold-storage blob, decides whether to overwrite, skip, or refuse:
/// <list type="bullet">
///   <item>destination older than source → <see cref="BlobConflictDecision.Overwrite"/></item>
///   <item>destination same as source → <see cref="BlobConflictDecision.SkipSameVersion"/> (placeholder still written)</item>
///   <item>destination newer than source → <see cref="BlobConflictDecision.DestinationNewer"/> (error, source kept)</item>
/// </list>
/// </summary>
public static class MigrateConflictResolver
{
    /// <summary>Default jitter absorbed when deciding whether two timestamps are the "same version".</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(2);

    public static BlobConflictDecision Decide(
        DateTime sourceLastModifiedUtc,
        DateTime? archivedSourceLastModifiedUtc,
        TimeSpan? tolerance = null)
    {
        if (archivedSourceLastModifiedUtc is not DateTime archived)
        {
            // No comparable timestamp on the existing archive (legacy blob) — safest is to
            // re-copy so cold storage holds a known-good current copy.
            return BlobConflictDecision.Overwrite;
        }

        var tol = tolerance ?? DefaultTolerance;
        var diff = sourceLastModifiedUtc.ToUniversalTime() - archived.ToUniversalTime();

        if (diff > tol)
        {
            return BlobConflictDecision.Overwrite;        // source newer → archive is older → overwrite
        }
        if (diff < tol.Negate())
        {
            return BlobConflictDecision.DestinationNewer; // archive newer than source → anomaly
        }
        return BlobConflictDecision.SkipSameVersion;      // within tolerance → same version
    }
}
