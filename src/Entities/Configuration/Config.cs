namespace Entities.Configuration;

public class Config(Microsoft.Extensions.Configuration.IConfiguration config) : BaseConfig(config)
{
    [ConfigValue]
    public string BaseServerAddress { get; set; } = string.Empty;

    /// <summary>
    /// Public URL of this web app (e.g. <c>https://app-spocs-clean-4bf5.azurewebsites.net</c>).
    /// Written by the deploy script from the bicep webAppHostname output. Used by the
    /// migrator to build the placeholder <c>.url</c> file's redirect target so end users
    /// who double-click a placeholder are sent to our SPA download route (which handles
    /// AAD auth + ACL check + redirect to a short-lived blob SAS) rather than to the
    /// raw blob URL (which would fail the moment storage public network access is
    /// disabled by policy). Empty means "fall back to writing the blob URL directly",
    /// for dev convenience.
    /// </summary>
    [ConfigValue(true)]
    public string AppBaseUrl { get; set; } = string.Empty;

    public string ServiceBusQueueName => "filediscovery";

    /// <summary>
    /// Key Vault URL - only required when using Certificate authentication mode
    /// </summary>
    [ConfigValue(true)]
    public string KeyVaultUrl { get; set; } = string.Empty;

    /// <summary>
    /// Blob container name - only required for migration operations (not for snapshot building)
    /// </summary>
    [ConfigValue(true)]
    public string BlobContainerName { get; set; } = string.Empty;

    [ConfigValue(true)]
    public string AppInsightsInstrumentationKey { get; set; } = string.Empty;

    public bool HaveAppInsightsConfigured => !string.IsNullOrEmpty(AppInsightsInstrumentationKey);

    [ConfigValue(true)]
    public int AnalysisSkipHours { get; set; } = 24;

    /// <summary>
    /// Minimum source file size (in bytes) eligible for archiving. Files below
    /// this are skipped with a logged reason. 0 (default) disables the check.
    /// Archiving very small files can cost more to process than they save, so
    /// this lets an admin tune the ROI floor without a code change (issue #2).
    /// </summary>
    [ConfigValue(true)]
    public int ColdStorageMinFileSizeBytes { get; set; }

    /// <summary>
    /// Comma/semicolon-separated list of file extensions that must NEVER be
    /// archived, e.g. <c>.tmp;.ds_store;.lnk</c>. Leading dots optional, case
    /// insensitive. Empty (default) excludes nothing (issue #2).
    /// </summary>
    [ConfigValue(true)]
    public string ColdStorageExcludedExtensions { get; set; } = string.Empty;

    /// <summary>
    /// Optional allow-list of file extensions. When non-empty, ONLY these
    /// extensions are eligible and everything else is skipped. Empty (default)
    /// allows all extensions (subject to the exclude list) (issue #2).
    /// </summary>
    [ConfigValue(true)]
    public string ColdStorageIncludedExtensions { get; set; } = string.Empty;

    /// <summary>
    /// Maximum all-time access (read) count a file may have and still be eligible
    /// for archiving. Files read more than this are skipped so heavily-used
    /// documents aren't archived just because they're rarely edited (issue #11).
    /// Uses the access_count the indexer persists on the files table. 0 (default)
    /// disables the check.
    /// </summary>
    [ConfigValue(true)]
    public int ColdStorageMaxAccessCount { get; set; }

    /// <summary>
    /// When &gt; 0, items that carry a Purview retention label (a record /
    /// retention / legal-hold label, read from the SharePoint <c>_ComplianceTag</c>
    /// field) are skipped rather than archived, to avoid moving content under a
    /// compliance obligation out to Azure (issue #15). 0 (default) disables the
    /// check. NOTE: this detects retention LABELS; eDiscovery holds that apply no
    /// per-item label are not detectable via this API and are not covered.
    /// </summary>
    [ConfigValue(true)]
    public int ColdStorageSkipRetentionLabeled { get; set; }

    /// <summary>
    /// When &gt; 0, the cold-storage blob is deleted after a restore has been
    /// verified (the restored file confirmed present in SharePoint), so a file
    /// doesn't end up living in both places (issue #4). 0 (default) keeps the
    /// blob. Deletion mirrors the migrate-side invariant: it only happens AFTER
    /// post-restore validation succeeds, and a delete failure never fails the
    /// restore.
    /// </summary>
    [ConfigValue(true)]
    public int ColdStorageDeleteBlobAfterRestore { get; set; }

    /// <summary>
    /// What to do with a cold-storage blob whose placeholder/site no longer
    /// exists (issue #3): <c>report</c> (default — audit only), <c>quarantine</c>
    /// (flag + tag the blob, keep it for review) or <c>delete</c> (remove the
    /// blob — permanent, since the source was already deleted at migration).
    /// </summary>
    [ConfigValue(true)]
    public string ColdStorageOrphanPolicy { get; set; } = "report";

    /// <summary>
    /// How often (hours) the migrator runs orphan reconciliation. 0 (default)
    /// disables the scheduled loop; reconciliation can still be triggered
    /// on-demand by an admin via the API.
    /// </summary>
    [ConfigValue(true)]
    public int ColdStorageReconcileIntervalHours { get; set; }

    /// <summary>
    /// Estimated Azure storage price per GB/month for the cold-storage tier, used
    /// by the cost-savings KPI dashboard (issue #8). String so it can hold a
    /// decimal; parsed invariant. Default ~Azure Cold tier.
    /// </summary>
    [ConfigValue(true)]
    public string ColdStorageAzurePricePerGbMonth { get; set; } = "0.0198";

    /// <summary>
    /// Effective value per GB/month of the SharePoint storage reclaimed by
    /// archiving (what you'd otherwise pay for extra SPO storage), used by the
    /// KPI dashboard (issue #8). String holding a decimal; parsed invariant.
    /// </summary>
    [ConfigValue(true)]
    public string ColdStorageSpoPricePerGbMonth { get; set; } = "0.20";

    /// <summary>
    /// Grace window (hours) a user is given between a pre-archive notice and an
    /// auto-archive actually moving their file (issue #17). 0 (default) disables
    /// pre-archive notices — matching today's user-initiated flow, which needs no
    /// warning. Consumed by the (future) auto-archive trigger via PreArchiveGate.
    /// </summary>
    [ConfigValue(true)]
    public int ColdStoragePreArchiveGraceHours { get; set; }

    /// <summary>
    /// When &gt; 0, prior SharePoint versions are captured to cold storage at
    /// archive time and replayed on restore so version history survives (issue
    /// #18). 0 (default) archives only the current version. Larger effort + more
    /// storage; best-effort so it never fails the core migrate/restore.
    /// </summary>
    [ConfigValue(true)]
    public int ColdStorageCaptureVersionHistory { get; set; }

    [ConfigSection("AzureAd")]
    public AzureAdConfig AzureAdConfig { get; set; } = null!;

    [ConfigSection("ConnectionStrings")]
    public ConnectionStrings ConnectionStrings { get; set; } = null!;

    [ConfigSection("Dev")]
    public DevConfig DevConfig { get; set; } = null!;

    [ConfigSection("Search")]
    public SearchConfig SearchConfig { get; set; } = null!;
}

public class ConfigException(string message) : Exception(message)
{
}
