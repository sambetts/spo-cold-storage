using System.Text.Json.Serialization;

namespace Models;

public enum SiteFileAnalysisState
{
    Unknown,
    AnalysisPending,
    AnalysisInProgress,
    Complete,
    FatalError,
    TransientError
}

public class DocumentSiteWithMetadata : DriveItemSharePointFileInfo
{
    public DocumentSiteWithMetadata() { }
    public DocumentSiteWithMetadata(DriveItemSharePointFileInfo driveArg) : base(driveArg)
    {
        this.AccessCount = null;
    }

    public SiteFileAnalysisState State { get; set; } = SiteFileAnalysisState.Unknown;

    /// <summary>
    /// Number of times the analytics/version fetch has been attempted and failed
    /// with a transient error. Used by the analytics provider to cap retries on
    /// files that keep returning transient errors (e.g. Graph 500s on a single item).
    /// </summary>
    [JsonIgnore]
    public int AnalyticsRetryCount { get; set; }

    public int? AccessCount { get; set; } = null;
    public int VersionCount { get; set; }
    public long VersionHistorySize { get; set; }
}

// https://docs.microsoft.com/en-us/graph/api/resources/itemactivitystat?view=graph-rest-1.0
public class ItemAnalyticsResponse
{

    [JsonPropertyName("incompleteData")]
    public AnalyticsIncompleteData? IncompleteData { get; set; }

    [JsonPropertyName("access")]
    public AnalyticsItemActionStat? AccessStats { get; set; }

    [JsonPropertyName("startDateTime")]
    public DateTime StartDateTime { get; set; }

    [JsonPropertyName("endDateTime")]
    public DateTime EndDateTime { get; set; }

    public class AnalyticsIncompleteData
    {
        [JsonPropertyName("wasThrottled")]
        public bool WasThrottled { get; set; }

        [JsonPropertyName("resultsPending")]
        public bool ResultsPending { get; set; }

        [JsonPropertyName("notSupported")]
        public bool NotSupported { get; set; }
    }
    public class AnalyticsItemActionStat
    {
        /// <summary>
        /// The number of times the action took place.
        /// </summary>
        [JsonPropertyName("actionCount")]
        public int ActionCount { get; set; } = 0;

        /// <summary>
        /// The number of distinct actors that performed the action.
        /// </summary>
        [JsonPropertyName("actorCount")]
        public int ActorCount { get; set; } = 0;
    }
}
