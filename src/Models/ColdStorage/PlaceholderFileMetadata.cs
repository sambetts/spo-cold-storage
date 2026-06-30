using System.Globalization;
using System.Text;

namespace Models.ColdStorage;

/// <summary>
/// Metadata persisted both in the SharePoint placeholder ".url" file (as
/// ini-style content) and in the migration_job_items row, so a restore request
/// can find the blob and recreate the original file in the correct library.
/// </summary>
public sealed class PlaceholderFileMetadata
{
    public string OriginalSiteUrl { get; set; } = string.Empty;
    public string OriginalWebUrl { get; set; } = string.Empty;
    public string OriginalServerRelativeUrl { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public long OriginalFileSize { get; set; }
    public DateTime OriginalLastModified { get; set; } = DateTime.MinValue;

    /// <summary>Display name of the original file's author (Created By).</summary>
    public string OriginalCreatedBy { get; set; } = string.Empty;

    /// <summary>Display name of the last person to edit the file (Modified By).</summary>
    public string OriginalModifiedBy { get; set; } = string.Empty;

    /// <summary>Original created timestamp of the source file.</summary>
    public DateTime OriginalCreated { get; set; } = DateTime.MinValue;
    public string ContainerName { get; set; } = string.Empty;
    public string BlobPath { get; set; } = string.Empty;
    public string BlobUrl { get; set; } = string.Empty;
    public string ContentMd5Base64 { get; set; } = string.Empty;
    public DateTime MigratedAt { get; set; } = DateTime.MinValue;
    public Guid JobId { get; set; } = Guid.Empty;

    private const string InternetShortcutSection = "InternetShortcut";
    private const string ColdStorageSection = "ColdStorage";

    /// <summary>
    /// Builds the textual content for a Windows-compatible ".url" file. The
    /// `[InternetShortcut]` section keeps the placeholder navigable from
    /// Office and File Explorer while `[ColdStorage]` carries the metadata
    /// the restore worker needs.
    /// </summary>
    /// <param name="userFacingUrl">
    /// Optional override for the <c>[InternetShortcut].URL</c> field. When set,
    /// this URL is what end users navigate to when they double-click the
    /// placeholder (typically a route on our own SPA, e.g.
    /// <c>https://app/cold-storage/download/{itemId}</c>, which then handles
    /// AAD auth + ACL check + redirect to a short-lived blob SAS). When null
    /// or empty, the raw <see cref="BlobUrl"/> is written instead (legacy
    /// behaviour, used by the unit-test round-trip + as a fallback when no
    /// public app base URL has been configured).
    /// </param>
    public string BuildUrlFileContent(string? userFacingUrl = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[" + InternetShortcutSection + "]");
        sb.Append("URL=").AppendLine(string.IsNullOrWhiteSpace(userFacingUrl) ? BlobUrl : userFacingUrl);
        sb.AppendLine();
        sb.AppendLine("[" + ColdStorageSection + "]");
        AppendKv(sb, nameof(JobId), JobId.ToString());
        AppendKv(sb, nameof(ContainerName), ContainerName);
        AppendKv(sb, nameof(BlobPath), BlobPath);
        AppendKv(sb, nameof(OriginalSiteUrl), OriginalSiteUrl);
        AppendKv(sb, nameof(OriginalWebUrl), OriginalWebUrl);
        AppendKv(sb, nameof(OriginalServerRelativeUrl), OriginalServerRelativeUrl);
        AppendKv(sb, nameof(OriginalFileName), OriginalFileName);
        AppendKv(sb, nameof(OriginalFileSize), OriginalFileSize.ToString(CultureInfo.InvariantCulture));
        AppendKv(sb, nameof(OriginalLastModified), OriginalLastModified.ToString("O", CultureInfo.InvariantCulture));
        AppendKv(sb, nameof(OriginalCreatedBy), OriginalCreatedBy);
        AppendKv(sb, nameof(OriginalModifiedBy), OriginalModifiedBy);
        AppendKv(sb, nameof(OriginalCreated), OriginalCreated.ToString("O", CultureInfo.InvariantCulture));
        AppendKv(sb, nameof(ContentMd5Base64), ContentMd5Base64);
        AppendKv(sb, nameof(MigratedAt), MigratedAt.ToString("O", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// Parse the ini content emitted by <see cref="BuildUrlFileContent"/>.
    /// Returns null if the [ColdStorage] section is missing or any required
    /// field is empty, so the restore worker can fail safely.
    /// </summary>
    public static PlaceholderFileMetadata? TryParse(string urlFileContent)
    {
        if (string.IsNullOrWhiteSpace(urlFileContent))
        {
            return null;
        }

        var sections = ParseSections(urlFileContent);
        if (!sections.TryGetValue(ColdStorageSection, out var meta))
        {
            return null;
        }

        var result = new PlaceholderFileMetadata
        {
            ContainerName = meta.GetValueOrDefault(nameof(ContainerName), string.Empty),
            BlobPath = meta.GetValueOrDefault(nameof(BlobPath), string.Empty),
            BlobUrl = sections.GetValueOrDefault(InternetShortcutSection, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                .GetValueOrDefault("URL", string.Empty),
            OriginalSiteUrl = meta.GetValueOrDefault(nameof(OriginalSiteUrl), string.Empty),
            OriginalWebUrl = meta.GetValueOrDefault(nameof(OriginalWebUrl), string.Empty),
            OriginalServerRelativeUrl = meta.GetValueOrDefault(nameof(OriginalServerRelativeUrl), string.Empty),
            OriginalFileName = meta.GetValueOrDefault(nameof(OriginalFileName), string.Empty),
            OriginalCreatedBy = meta.GetValueOrDefault(nameof(OriginalCreatedBy), string.Empty),
            OriginalModifiedBy = meta.GetValueOrDefault(nameof(OriginalModifiedBy), string.Empty),
            ContentMd5Base64 = meta.GetValueOrDefault(nameof(ContentMd5Base64), string.Empty),
        };

        if (meta.TryGetValue(nameof(JobId), out var jobIdRaw)
            && Guid.TryParse(jobIdRaw, out var jobId))
        {
            result.JobId = jobId;
        }

        if (meta.TryGetValue(nameof(OriginalFileSize), out var sizeRaw)
            && long.TryParse(sizeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
        {
            result.OriginalFileSize = size;
        }

        if (meta.TryGetValue(nameof(OriginalLastModified), out var lmRaw)
            && DateTime.TryParse(lmRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lm))
        {
            result.OriginalLastModified = lm;
        }

        if (meta.TryGetValue(nameof(OriginalCreated), out var createdRaw)
            && DateTime.TryParse(createdRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created))
        {
            result.OriginalCreated = created;
        }

        if (meta.TryGetValue(nameof(MigratedAt), out var migratedRaw)
            && DateTime.TryParse(migratedRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var migrated))
        {
            result.MigratedAt = migrated;
        }

        if (string.IsNullOrEmpty(result.ContainerName)
            || string.IsNullOrEmpty(result.BlobPath)
            || string.IsNullOrEmpty(result.OriginalServerRelativeUrl))
        {
            // Per requirements: "If placeholder metadata is incomplete or
            // corrupted, the restore action should fail safely". Returning null
            // forces callers down that path.
            return null;
        }

        return result;
    }

    private static Dictionary<string, Dictionary<string, string>> ParseSections(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        sections[string.Empty] = current;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[name] = current;
                continue;
            }
            var idx = line.IndexOf('=', StringComparison.Ordinal);
            if (idx <= 0)
            {
                continue;
            }
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            current[key] = value;
        }
        return sections;
    }

    private static void AppendKv(StringBuilder sb, string key, string value)
        => sb.Append(key).Append('=').AppendLine(value);
}
