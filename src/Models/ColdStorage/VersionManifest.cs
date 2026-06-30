using System.Text.Json;
using System.Text.Json.Serialization;

namespace Models.ColdStorage;

/// <summary>
/// One archived prior version of a file (issue #18).
/// </summary>
public sealed class ArchivedVersion
{
    /// <summary>SharePoint/Graph version label or id, e.g. "1.0", "2.0".</summary>
    public string VersionId { get; set; } = string.Empty;

    /// <summary>Blob path the version's content was archived to.</summary>
    public string BlobPath { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTime LastModifiedUtc { get; set; }
}

/// <summary>
/// Inventory of the archived versions of a file, persisted as a sidecar blob
/// (<see cref="VersionBlobLayout.ManifestKey"/>) so a restore can replay the full
/// history (issue #18). Ordered oldest-first.
/// </summary>
public sealed class VersionManifest
{
    public List<ArchivedVersion> Versions { get; set; } = [];

    [JsonIgnore]
    public int Count => Versions.Count;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parses a manifest; returns null on empty/invalid input so callers fail safely.</summary>
    public static VersionManifest? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<VersionManifest>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
