using Entities;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Web.Authorization;

namespace Web.Services;

/// <summary>
/// Tenant-level "cold-storage admin" check. There is no separate admin role in
/// this product; an admin is a principal that was granted full rights
/// (browse + migrate + restore) on the default container — exactly the grant the
/// <c>ColdStorageContainerSeeder</c> gives the initial admin. Centralised here so
/// admin-only endpoints (exclusions, force-restore, dashboards, queue, audit)
/// all gate consistently.
/// </summary>
public interface IColdStorageAdminAuthorizationService
{
    Task<bool> IsAdminAsync(ClaimsPrincipal caller, CancellationToken cancellationToken = default);
}

public sealed class ColdStorageAdminAuthorizationService(SPOColdStorageDbContext db) : IColdStorageAdminAuthorizationService
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<bool> IsAdminAsync(ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var principalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var oid = caller.GetEntraObjectId();
        if (!string.IsNullOrEmpty(oid))
        {
            principalIds.Add(oid);
        }
        foreach (var g in caller.GetGroupIds())
        {
            principalIds.Add(g);
        }
        if (principalIds.Count == 0)
        {
            return false;
        }

        var defaultContainer = await _db.ColdStorageContainers
            .Include(c => c.Acls)
            .FirstOrDefaultAsync(c => c.IsDefault, cancellationToken)
            .ConfigureAwait(false);
        if (defaultContainer is null)
        {
            return false;
        }

        return defaultContainer.Acls.Any(a =>
            principalIds.Contains(a.PrincipalId) && a.CanBrowse && a.CanMigrate && a.CanRestore);
    }
}
