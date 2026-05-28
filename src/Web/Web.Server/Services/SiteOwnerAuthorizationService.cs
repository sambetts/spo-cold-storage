using Entities.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
using Migration.Engine;
using System.Security.Claims;
using Web.Authorization;

namespace Web.Services;

/// <summary>
/// Enforces the "only site-collection owners can trigger cold storage
/// actions" rule from requirements.md. Uses CSOM to read the associated
/// owner group and checks the calling user UPN / object id against its
/// members. Results are cached briefly to avoid repeated round-trips per
/// page load in the SPFx component.
/// </summary>
public interface ISiteOwnerAuthorizationService
{
    Task<bool> IsCallerSiteOwnerAsync(ClaimsPrincipal caller, string siteUrl, CancellationToken cancellationToken = default);
}

public sealed class SiteOwnerAuthorizationService(Config config, ILogger<SiteOwnerAuthorizationService> logger) : ISiteOwnerAuthorizationService
{
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger<SiteOwnerAuthorizationService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    // Per-process cache. siteUrl|upn -> (decision, expiry). Short TTL so role
    // changes propagate quickly while keeping interactive use snappy.
    private static readonly Dictionary<string, (bool IsOwner, DateTime ExpiresAt)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _cacheLock = new();
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(2);

    public async Task<bool> IsCallerSiteOwnerAsync(ClaimsPrincipal caller, string siteUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrEmpty(siteUrl))
        {
            return false;
        }

        var upn = caller.GetUpn();
        if (string.IsNullOrEmpty(upn))
        {
            _logger.LogWarning("Caller has no UPN claim; refusing site-owner check for {SiteUrl}.", siteUrl);
            return false;
        }
        var oid = caller.GetEntraObjectId();

        var cacheKey = siteUrl + "|" + upn;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            {
                return cached.IsOwner;
            }
        }

        bool isOwner;
        try
        {
            using var ctx = await AuthUtils.GetClientContext(_config, siteUrl, _logger, null).ConfigureAwait(false);
            isOwner = await CheckOwnerGroupMembershipAsync(ctx, upn, oid).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Per the security principle - default deny on errors so a transient
            // SharePoint failure can't accidentally grant access.
            _logger.LogError(ex, "Site-owner check failed for {SiteUrl} / {Upn}. Denying.", siteUrl, upn);
            return false;
        }

        lock (_cacheLock)
        {
            _cache[cacheKey] = (isOwner, DateTime.UtcNow + _cacheTtl);
        }
        return isOwner;
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
