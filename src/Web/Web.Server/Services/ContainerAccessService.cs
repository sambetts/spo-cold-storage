using Entities;
using Entities.DBEntities.ColdStorage;
using Microsoft.EntityFrameworkCore;
using Models.ColdStorage;
using System.Security.Claims;
using Web.Authorization;
using Web.Models.Api;

namespace Web.Services;

/// <summary>
/// Resolves cold-storage containers visible/usable by the calling user. The
/// API never returns a container the caller cannot at least browse, and
/// migrate/restore actions on a container are blocked unless the caller has
/// the corresponding ACL row.
/// </summary>
public interface IContainerAccessService
{
    Task<List<ContainerResponse>> ListVisibleContainersAsync(ClaimsPrincipal caller, CancellationToken cancellationToken = default);

    Task<ColdStorageContainer?> ResolveAsync(string containerName, CancellationToken cancellationToken = default);

    /// <summary>
    /// True if the caller is permitted to perform the requested action on the
    /// container. Used by controllers to short-circuit before queueing work.
    /// </summary>
    Task<bool> CanAsync(ClaimsPrincipal caller, ColdStorageContainer container, ContainerAction action, CancellationToken cancellationToken = default);
}

public enum ContainerAction
{
    Browse,
    Migrate,
    Restore,
}

public sealed class ContainerAccessService(SPOColdStorageDbContext db) : IContainerAccessService
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<List<ContainerResponse>> ListVisibleContainersAsync(ClaimsPrincipal caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var oid = caller.GetEntraObjectId();
        var groups = caller.GetGroupIds();
        var principalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(oid))
        {
            principalIds.Add(oid);
        }
        foreach (var g in groups)
        {
            principalIds.Add(g);
        }

        var containers = await _db.ColdStorageContainers
            .Include(c => c.Acls)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.DisplayName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var visible = new List<ContainerResponse>();
        foreach (var c in containers)
        {
            var matching = c.Acls.Where(a => principalIds.Contains(a.PrincipalId)).ToList();
            if (matching.Count == 0)
            {
                continue;
            }
            var canBrowse = matching.Any(a => a.CanBrowse);
            if (!canBrowse)
            {
                continue;
            }
            visible.Add(new ContainerResponse
            {
                Name = c.Name,
                DisplayName = c.DisplayName,
                Description = c.Description,
                CanBrowse = canBrowse,
                CanMigrate = matching.Any(a => a.CanMigrate),
                CanRestore = matching.Any(a => a.CanRestore),
                IsDefault = c.IsDefault,
            });
        }
        return visible;
    }

    public async Task<ColdStorageContainer?> ResolveAsync(string containerName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(containerName))
        {
            return null;
        }
        return await _db.ColdStorageContainers
            .Include(c => c.Acls)
            .FirstOrDefaultAsync(c => c.Name == containerName, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<bool> CanAsync(ClaimsPrincipal caller, ColdStorageContainer container, ContainerAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(container);

        var oid = caller.GetEntraObjectId();
        var principalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(oid))
        {
            principalIds.Add(oid);
        }
        foreach (var g in caller.GetGroupIds())
        {
            principalIds.Add(g);
        }

        var permitted = container.Acls
            .Where(a => principalIds.Contains(a.PrincipalId))
            .Any(a => action switch
            {
                ContainerAction.Browse => a.CanBrowse,
                ContainerAction.Migrate => a.CanMigrate,
                ContainerAction.Restore => a.CanRestore,
                _ => false,
            });
        return Task.FromResult(permitted);
    }
}
