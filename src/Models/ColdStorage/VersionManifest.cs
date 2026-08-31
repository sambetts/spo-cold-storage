using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Models.ColdStorage;

/// <summary>
/// One archived prior version of a file (issue #18, extended for issue #66).
/// </summary>
public sealed class ArchivedVersion
{
    /// <summary>
    /// Stable identifier for the version — the SharePoint version label when we have
    /// one ("1.0", "2.3"), otherwise the version URL. Also forms the blob key.
    /// </summary>
    public string VersionId { get; set; } = string.Empty;

    /// <summary>Blob path the version's content was archived to.</summary>
    public string BlobPath { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTime LastModifiedUtc { get; set; }

    /// <summary>
    /// SharePoint version label ("1.0", "2.3"). Kept separately from
    /// <see cref="VersionId"/> because the id falls back to the version URL when a
    /// label isn't available, and the label is what drives ordering + major/minor.
    /// </summary>
    public string? VersionLabel { get; set; }

    /// <summary>
    /// True for a published (major) version — a label whose minor part is 0. Replay
    /// uses this to decide whether to publish the replayed version rather than leave
    /// it as a draft, as far as the destination library's versioning settings allow.
    /// </summary>
    public bool IsMajor { get; set; }

    /// <summary>Display name of the user who created the version, when SharePoint exposes it.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Check-in comment recorded on the version, when present.</summary>
    public string? CheckInComment { get; set; }

    /// <summary>
    /// Base64 MD5 of the archived content, computed at capture time and re-checked
    /// against the stored blob so a truncated/corrupt version copy is caught before
    /// the SharePoint source is deleted (issue #66).
    /// </summary>
    public string? ContentMd5Base64 { get; set; }
}

/// <summary>
/// Inventory of the archived versions of a file, persisted as a sidecar blob
/// (<see cref="VersionBlobLayout.ManifestKey"/>) so a restore can replay the full
/// history (issue #18). Ordered oldest-first.
/// </summary>
public sealed class VersionManifest
{
    /// <summary>
    /// Manifests written before issue #66 carry no schema marker and no per-version
    /// hash, so they deserialize as this version and are replayed best-effort.
    /// </summary>
    public const int LegacySchemaVersion = 1;

    /// <summary>Manifests with validated per-version hashes and full version metadata.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// Deliberately defaults to <see cref="LegacySchemaVersion"/>: System.Text.Json runs
    /// property initializers before binding, so a manifest that predates this field must
    /// not masquerade as a current-schema one.
    /// </summary>
    public int SchemaVersion { get; set; } = LegacySchemaVersion;

    public List<ArchivedVersion> Versions { get; set; } = [];

    /// <summary>Blob key of the current-version ("base") blob these versions belong to.</summary>
    public string? BaseBlobPath { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    /// <summary>
    /// True only when every accessible prior version was captured <i>and</i> validated.
    /// When version-history preservation is enabled the migrate pipeline refuses to
    /// delete the SharePoint source unless this is set, so a partial capture can never
    /// silently lose history.
    /// </summary>
    public bool CaptureComplete { get; set; }

    [JsonIgnore]
    public int Count => Versions.Count;

    /// <summary>True for a manifest written before issue #66 (no validation metadata).</summary>
    [JsonIgnore]
    public bool IsLegacy => SchemaVersion < CurrentSchemaVersion;

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

    /// <summary>
    /// Orders versions oldest-first, which is the order replay must use so the
    /// destination rebuilds its history and the archived current version (uploaded
    /// last by the caller) ends up as the latest.
    /// <para>
    /// Sorts on the numeric SharePoint version label ("2.0" before "10.0" — a plain
    /// string sort gets this wrong) and falls back to the timestamp when a label is
    /// missing or unparsable.
    /// </para>
    /// </summary>
    public void SortOldestFirst() => Versions.Sort(CompareOldestFirst);

    internal static int CompareOldestFirst(ArchivedVersion a, ArchivedVersion b)
    {
        var left = ParseLabel(a.VersionLabel ?? a.VersionId);
        var right = ParseLabel(b.VersionLabel ?? b.VersionId);
        if (left is not null && right is not null)
        {
            var major = left.Value.Major.CompareTo(right.Value.Major);
            if (major != 0)
            {
                return major;
            }
            var minor = left.Value.Minor.CompareTo(right.Value.Minor);
            if (minor != 0)
            {
                return minor;
            }
        }
        return a.LastModifiedUtc.CompareTo(b.LastModifiedUtc);
    }

    /// <summary>
    /// Parses a SharePoint version label of the form "major.minor" (or a bare
    /// "major"). Returns null for anything else, e.g. a version URL used as a
    /// fallback id.
    /// </summary>
    internal static (int Major, int Minor)? ParseLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }
        var span = label.Trim();
        var dot = span.IndexOf('.');
        if (dot < 0)
        {
            return int.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out var only)
                ? (only, 0)
                : null;
        }
        return int.TryParse(span[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
               && int.TryParse(span[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            ? (major, minor)
            : null;
    }

    /// <summary>
    /// True when a SharePoint version label denotes a published (major) version —
    /// i.e. its minor part is 0. Unparsable labels are treated as major, matching a
    /// library with only major versioning enabled, where every label is "N.0".
    /// </summary>
    public static bool IsMajorLabel(string? label)
    {
        var parsed = ParseLabel(label);
        return parsed is null || parsed.Value.Minor == 0;
    }
}
