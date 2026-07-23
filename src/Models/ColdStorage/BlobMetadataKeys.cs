namespace Models.ColdStorage;

/// <summary>
/// Well-known blob metadata + index-tag names used by the migrator and the
/// placeholder/restore lookup endpoints. Centralised so producers and
/// consumers stay in sync.
/// </summary>
public static class BlobMetadataKeys
{
    /// <summary>Original SharePoint server-relative URL of the migrated file.</summary>
    public const string OriginalServerRelativeUrl = "spOriginalServerRelativeUrl";

    public const string OriginalSiteUrl = "spOriginalSiteUrl";
    public const string OriginalWebUrl = "spOriginalWebUrl";
    public const string OriginalFileName = "spOriginalFileName";
    public const string OriginalLastModifiedUtc = "spOriginalLastModifiedUtc";

    /// <summary>
    /// Original authorship of the source file, captured at migration time and
    /// stored on the blob so the metadata is preserved in cold storage (and
    /// restorable) even when the placeholder is left as just a <c>.url</c> file
    /// with no "Original *" library columns.
    /// </summary>
    public const string OriginalCreatedBy = "spOriginalCreatedBy";

    public const string OriginalModifiedBy = "spOriginalModifiedBy";
    public const string OriginalCreatedUtc = "spOriginalCreatedUtc";

    /// <summary>Job that produced this blob.</summary>
    public const string MigrationJobId = "spJobId";

    /// <summary>UPN of the user who initiated the migrate request.</summary>
    public const string RequestedByUpn = "spRequestedByUpn";

    /// <summary>Base64-encoded MD5 hash of the source file as observed at migration time.</summary>
    public const string SourceContentMd5 = "spSourceMd5";

    /// <summary>Set by reconciliation when a blob is quarantined as an orphan (issue #3).</summary>
    public const string OrphanQuarantinedUtc = "spOrphanQuarantinedUtc";
}
