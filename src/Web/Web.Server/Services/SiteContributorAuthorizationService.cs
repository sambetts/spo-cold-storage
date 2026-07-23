using Entities.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
using Migration.Engine;
using System.Security.Claims;
using Web.Authorization;

namespace Web.Services;

/// <summary>
/// Gate for who may trigger cold-storage migrate/restore from SharePoint. Anyone with
/// <b>contributor rights</b> on the target web is allowed — i.e. effective
/// <see cref="PermissionKind.EditListItems"/>, which the Contribute/Edit levels grant (and,
/// by extension, Full-Control site owners); read-only visitors are not. Uses CSOM effective
/// permissions, which resolve through both SharePoint and Entra (AAD) group membership, so a
/// user who is a contributor via a group is correctly allowed. Falls back to owner-group
/// membership if effective permissions can't be resolved, so existing owners are never locked
/// out. Results are cached briefly to avoid repeated round-trips per SPFx page load.
/// </summary>
public interface ISiteContributorAuthorizationService
{
    Task<bool> IsCallerSiteContributorAsync(ClaimsPrincipal caller, string siteUrl, CancellationToken cancellationToken = default);
}

public sealed class SiteContributorAuthorizationService(Config config, ILogger<SiteContributorAuthorizationService> logger) : ISiteContributorAuthorizationService
{
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger<SiteContributorAuthorizationService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    // Per-process cache. siteUrl|upn -> (decision, expiry). Short TTL so role
    // changes propagate quickly while keeping interactive use snappy.
    private static readonly Dictionary<string, (bool Allowed, DateTime ExpiresAt)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _cacheLock = new();
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(2);

    public async Task<bool> IsCallerSiteContributorAsync(ClaimsPrincipal caller, string siteUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrEmpty(siteUrl))
        {
            return false;
        }

        var upn = caller.GetUpn();
        if (string.IsNullOrEmpty(upn))
        {
            _logger.LogWarning("Caller has no UPN claim; refusing contributor check for {SiteUrl}.", siteUrl);
            return false;
        }
        var oid = caller.GetEntraObjectId();

        var cacheKey = siteUrl + "|" + upn;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            {
                return cached.Allowed;
            }
        }

        bool allowed;
        try
        {
            using var ctx = await AuthUtils.GetClientContext(_config, siteUrl, _logger, null).ConfigureAwait(false);
            allowed = await CheckCanContributeAsync(ctx, upn, oid).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Per the security principle - default deny on errors so a transient
            // SharePoint failure can't accidentally grant access.
            _logger.LogError(ex, "Contributor check failed for {SiteUrl} / {Upn}. Denying.", siteUrl, upn);
            return false;
        }

        lock (_cacheLock)
        {
            _cache[cacheKey] = (allowed, DateTime.UtcNow + _cacheTtl);
        }
        return allowed;
    }

    /// <summary>
    /// True when the caller has contributor (edit) rights on the web. Primary check is the
    /// caller's effective permissions (EditListItems); if that lookup can't be resolved (e.g.
    /// an external/guest login-name shape we didn't build), fall back to owner-group membership
    /// so the broadening never regresses existing owners.
    /// </summary>
    private async Task<bool> CheckCanContributeAsync(ClientContext ctx, string upn, string? oid)
    {
        try
        {
            // Claims login name for an Entra member account in SharePoint Online. Effective
            // permissions resolve through SP + AAD groups, so group-based contributors pass too.
            var loginName = "i:0#.f|membership|" + upn;
            var perms = ctx.Web.GetUserEffectivePermissions(loginName);
            await ctx.ExecuteQueryAsync().ConfigureAwait(false);
            if (perms.Value.Has(PermissionKind.EditListItems))
            {
                return true;
            }
            // Resolved cleanly but the user only has read/visitor rights — not a contributor.
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Effective-permissions lookup failed for '{Upn}'; falling back to owner-group membership.", upn);
            return await CheckOwnerGroupMembershipAsync(ctx, upn, oid).ConfigureAwait(false);
        }
    }

    private static async Task<bool> CheckOwnerGroupMembershipAsync(ClientContext ctx, string upn, string? oid)
    {
        var ownersGroup = ctx.Web.AssociatedOwnerGroup;
        ctx.Load(ownersGroup, g => g.Users.Include(u => u.LoginName, u => u.Email, u => u.UserPrincipalName));
        await ctx.ExecuteQueryAsync().ConfigureAwait(false);

        foreach (var user in ownersGroup.Users)
        {
            if (Matches(user.UserPrincipalName, upn) || Matches(user.Email, upn) || ContainsClaim(user.LoginName, upn))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(oid) && ContainsClaim(user.LoginName, oid))
            {
                return true;
            }
        }
        return false;
    }

    private static bool Matches(string? candidate, string target)
        => !string.IsNullOrEmpty(candidate) && string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsClaim(string? loginName, string fragment)
        => !string.IsNullOrEmpty(loginName) && loginName.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
