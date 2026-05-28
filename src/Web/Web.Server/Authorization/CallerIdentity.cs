using System.Security.Claims;

namespace Web.Authorization;

/// <summary>
/// Helpers for reading the validated Entra access token claims attached to the
/// current HTTP request. Centralised so controllers can identify the calling
/// user without duplicating claim-name knowledge.
/// </summary>
public static class CallerIdentity
{
    public static string? GetUpn(this ClaimsPrincipal principal)
        => principal.FindFirst("preferred_username")?.Value
           ?? principal.FindFirst(ClaimTypes.Upn)?.Value
           ?? principal.FindFirst(ClaimTypes.Email)?.Value
           ?? principal.FindFirst(ClaimTypes.Name)?.Value;

    public static string? GetEntraObjectId(this ClaimsPrincipal principal)
        => principal.FindFirst("oid")?.Value
           ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
           ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public static IReadOnlyCollection<string> GetGroupIds(this ClaimsPrincipal principal)
    {
        var groups = new List<string>();
        foreach (var c in principal.FindAll("groups"))
        {
            groups.Add(c.Value);
        }
        foreach (var c in principal.FindAll("group"))
        {
            groups.Add(c.Value);
        }
        return groups;
    }
}
