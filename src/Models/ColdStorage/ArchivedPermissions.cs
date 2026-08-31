using System.Text.Json;
using System.Text.Json.Serialization;

namespace Models.ColdStorage;

/// <summary>
/// One principal's access to an item, captured from a SharePoint role assignment
/// (issue #67).
/// </summary>
public sealed class ArchivedRoleAssignment
{
    /// <summary>Login name of the principal, e.g. <c>i:0#.f|membership|ada@contoso.com</c>.</summary>
    public string LoginName { get; set; } = string.Empty;

    /// <summary>Display name, kept for the audit trail and for a readable failure message.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Role definition names bound to this principal, e.g. <c>Full Control</c>,
    /// <c>Contribute</c>, <c>Read</c>. Names (not ids) because role-definition ids
    /// are per-web and would not resolve on a restore to a different web.
    /// </summary>
    public List<string> Roles { get; set; } = [];
}

/// <summary>
/// Snapshot of an item's unique permissions, taken before the SharePoint source is
/// deleted and persisted on the job item (<c>permissions_json</c>) so the same access
/// can be re-applied to the <c>.url</c> placeholder and to the restored file (issue #67).
///
/// <para>
/// Only meaningful when <see cref="HadUniqueRoleAssignments"/> is true. An item that
/// inherited its permissions needs nothing restored — the destination folder's
/// inheritance already gives the right result, and breaking inheritance to "restore"
/// it would be actively wrong.
/// </para>
/// </summary>
public sealed class ArchivedPermissions
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// True when the source item had broken inheritance. False means "inherited",
    /// and restore must leave the destination inheriting.
    /// </summary>
    public bool HadUniqueRoleAssignments { get; set; }

    public List<ArchivedRoleAssignment> Assignments { get; set; } = [];

    public DateTime CapturedAtUtc { get; set; }

    [JsonIgnore]
    public int Count => Assignments.Count;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parses a snapshot; returns null on empty/invalid input so callers fail safely.</summary>
    public static ArchivedPermissions? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ArchivedPermissions>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
