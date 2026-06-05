using Entities.DBEntities.ColdStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Entities;

/// <summary>
/// One-time seeder that creates a default <c>cold_storage_containers</c> row +
/// ACL on a brand-new deployment. Without this, the SPFx Migrate / Restore
/// commands fail with "no permission to migrate to any configured cold-storage
/// container" because container access is gated by a separate ACL table (NOT
/// by SharePoint permissions or RBAC on the storage account).
///
/// Idempotent: only runs when <c>cold_storage_containers</c> is empty. Values
/// come from configuration (written by deploy.ps1 Set-AppSettings):
///   ColdStorage:DefaultContainer:Name              (default "default")
///   ColdStorage:DefaultContainer:DisplayName       (default "Default cold storage")
///   ColdStorage:DefaultContainer:BlobContainerName (default = BlobContainerName)
///   ColdStorage:DefaultContainer:StorageAccountUri (e.g. https://acct.blob.core.windows.net)
///   ColdStorage:InitialAdminPrincipalId            (Entra OID, granted full ACL)
///   ColdStorage:InitialAdminPrincipalType          (0 = User, 1 = Group; default 0)
///   ColdStorage:InitialAdminPrincipalDisplay       (UPN / display name for audit)
///
/// If InitialAdminPrincipalId is empty the container is still seeded but with
/// no ACL — a real admin must then add ACL rows manually before the SPFx UI
/// can use it. This matches the security model: we never auto-grant access
/// to a principal whose identity we don't know.
/// </summary>
public static class ColdStorageContainerSeeder
{
    public static async Task SeedDefaultIfEmptyAsync(
        SPOColdStorageDbContext db,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        if (await db.ColdStorageContainers.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var name        = configuration["ColdStorage:DefaultContainer:Name"]              ?? "default";
        var displayName = configuration["ColdStorage:DefaultContainer:DisplayName"]       ?? "Default cold storage";
        var blobName    = configuration["ColdStorage:DefaultContainer:BlobContainerName"] ?? configuration["BlobContainerName"];
        var storageUri  = configuration["ColdStorage:DefaultContainer:StorageAccountUri"] ?? string.Empty;
        var adminOid    = configuration["ColdStorage:InitialAdminPrincipalId"];
        var adminType   = int.TryParse(configuration["ColdStorage:InitialAdminPrincipalType"], out var t) ? t : 0;
        var adminDisp   = configuration["ColdStorage:InitialAdminPrincipalDisplay"];

        if (string.IsNullOrWhiteSpace(blobName))
        {
            logger.LogWarning("Cold-storage container seed skipped: no BlobContainerName configured.");
            return;
        }

        var container = new ColdStorageContainer
        {
            Name = name,
            DisplayName = displayName,
            BlobContainerName = blobName,
            StorageAccountUri = storageUri,
            IsDefault = true,
            SortOrder = 0,
            Description = "Auto-seeded default container.",
        };
        db.ColdStorageContainers.Add(container);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(adminOid))
        {
            db.ColdStorageContainerAcls.Add(new ColdStorageContainerAcl
            {
                ContainerId = container.ID,
                PrincipalId = adminOid,
                PrincipalType = adminType,
                PrincipalDisplay = adminDisp,
                CanBrowse = true,
                CanMigrate = true,
                CanRestore = true,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Seeded default cold-storage container '{Name}' (id={Id}, blob={Blob}) with full-rights ACL for principal {AdminOid} ({Display}).",
                name, container.ID, blobName, adminOid, adminDisp ?? "(no display)");
        }
        else
        {
            logger.LogWarning(
                "Seeded default cold-storage container '{Name}' (id={Id}, blob={Blob}) but NO ACL — set ColdStorage:InitialAdminPrincipalId or add ACL rows manually before the SPFx UI will work.",
                name, container.ID, blobName);
        }
    }
}
