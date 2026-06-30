using Azure.Storage.Sas;
using Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Migration.Engine.Utils;
using Models.ColdStorage;
using Web.Authorization;
using Web.Models.Api;
using Web.Services;

namespace Web.Controllers;

/// <summary>
/// <c>GET /api/placeholders/resolve</c> – look up the metadata behind a .url
/// placeholder so the system can decide whether the item is eligible for
/// restore. Returns enough metadata for the restore workflow without
/// disclosing storage details to callers who lack restore permission.
///
/// <c>GET /api/placeholders/download/{itemId}</c> – issue a short-lived
/// user-delegation SAS URL for the underlying blob so authorised users can
/// download the file from the SPA without ever needing direct blob RBAC.
/// </summary>
[Authorize]
[ApiController]
[Route("api/placeholders")]
public class PlaceholdersController(
    SPOColdStorageDbContext db,
    IContainerAccessService containerAccess,
    Entities.Configuration.Config config,
    ILogger<PlaceholdersController> logger) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IContainerAccessService _containerAccess = containerAccess ?? throw new ArgumentNullException(nameof(containerAccess));
    private readonly Entities.Configuration.Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger<PlaceholdersController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    [HttpGet("resolve")]
    public async Task<ActionResult<PlaceholderMetadataResponse>> ResolveAsync(
        [FromQuery] string? placeholderServerRelativeUrl,
        [FromQuery] string? itemId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(placeholderServerRelativeUrl) && string.IsNullOrEmpty(itemId))
        {
            return BadRequest("placeholderServerRelativeUrl or itemId is required.");
        }

        Entities.DBEntities.ColdStorage.MigrationJobItem? item;
        if (!string.IsNullOrEmpty(itemId) && Guid.TryParse(itemId, out var itemGuid))
        {
            item = await _db.MigrationJobItems
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.ItemId == itemGuid, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            item = await _db.MigrationJobItems
                .AsNoTracking()
                .Where(i => i.PlaceholderServerRelativeUrl == placeholderServerRelativeUrl
                            || i.SpServerRelativeUrl == placeholderServerRelativeUrl)
                .OrderByDescending(i => i.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (item is null)
        {
            return new PlaceholderMetadataResponse
            {
                IsResolved = false,
                UnavailableReason = "No migration record found for that placeholder.",
                IsEligibleForRestore = false,
            };
        }

        var container = item.ContainerId is null
            ? null
            : await _db.ColdStorageContainers
                .Include(c => c.Acls)
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.ID == item.ContainerId, cancellationToken)
                .ConfigureAwait(false);

        var response = new PlaceholderMetadataResponse
        {
            IsResolved = true,
            OriginalSiteUrl = item.SpSiteUrl,
            OriginalWebUrl = item.SpWebUrl,
            OriginalServerRelativeUrl = item.SpServerRelativeUrl,
            OriginalFileName = System.IO.Path.GetFileName(item.SpServerRelativeUrl),
            OriginalFileSize = item.FileSize,
            OriginalLastModified = item.SourceLastModified ?? DateTime.MinValue,
            OriginalCreatedBy = item.OriginalCreatedBy,
            OriginalModifiedBy = item.OriginalModifiedBy,
            OriginalCreated = item.OriginalCreated,
            MigratedAt = item.CopiedAt ?? DateTime.MinValue,
            JobId = item.JobId,
            CurrentStatus = item.Status,
        };

        bool canRestore = container is not null
            && await _containerAccess.CanAsync(User, container, ContainerAction.Restore, cancellationToken).ConfigureAwait(false);

        if (canRestore)
        {
            response.ContainerName = container!.Name;
            response.BlobPath = item.BlobPath;
            response.BlobUrl = item.BlobUrl;
        }
        else
        {
            response.UnavailableReason = container is null
                ? "Cold-storage container is no longer configured."
                : "Caller does not have restore permission on the source container.";
        }

        response.IsEligibleForRestore = canRestore
            && item.Status == MigrationLifecycleStatus.ColdStorageMigrationCompleted
            && !string.IsNullOrEmpty(item.BlobPath);
        return response;
    }

    /// <summary>
    /// Issues a short-lived (5 min) user-delegation SAS URL for the blob behind
    /// a cold-storage placeholder and returns it to the caller. The SPA bounces
    /// the user's browser to this URL so the download works without our Web API
    /// having to stream the bytes itself.
    ///
    /// Auth flow:
    ///   1. User double-clicks the .url placeholder in SharePoint or browser.
    ///   2. URL points at our SPA route /cold-storage/download/{itemId}.
    ///   3. SPA performs MSAL login if needed, then hits this endpoint with a
    ///      Bearer token.
    ///   4. This endpoint checks container ACL (CanBrowse OR CanRestore - read
    ///      access only) and asks Azure Storage for a user-delegation key (the
    ///      Web App MSI has Storage Blob Data Contributor + Storage Blob
    ///      Delegator implicitly granted by the Contributor role).
    ///   5. Returns { url, expiresAt } - SPA does window.location.href = url.
    ///
    /// We never issue write/delete SAS - users always restore through the
    /// proper /api/restores/start path which has its own ACL + audit trail.
    /// </summary>
    [HttpGet("download/{itemId:guid}")]
    public async Task<ActionResult<DownloadUrlResponse>> DownloadAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await _db.MigrationJobItems
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.ItemId == itemId, cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
        {
            return NotFound(new { error = "No migration item with that id." });
        }
        if (item.ContainerId is null
            || string.IsNullOrEmpty(item.BlobContainerName)
            || string.IsNullOrEmpty(item.BlobPath))
        {
            return Conflict(new { error = "Item has not finished migrating to cold storage yet." });
        }

        var container = await _db.ColdStorageContainers
            .Include(c => c.Acls)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ID == item.ContainerId, cancellationToken)
            .ConfigureAwait(false);
        if (container is null)
        {
            return Conflict(new { error = "Cold-storage container is no longer configured." });
        }

        // Read access = either browse OR restore permission. Restore implies the user
        // can already pull the file back, so they can certainly download a copy.
        var canBrowse  = await _containerAccess.CanAsync(User, container, ContainerAction.Browse,  cancellationToken).ConfigureAwait(false);
        var canRestore = await _containerAccess.CanAsync(User, container, ContainerAction.Restore, cancellationToken).ConfigureAwait(false);
        if (!canBrowse && !canRestore)
        {
            return Forbid();
        }

        try
        {
            var storageUri = !string.IsNullOrEmpty(container.StorageAccountUri)
                ? container.StorageAccountUri
                : _config.ConnectionStrings.Storage;
            var serviceClient = BlobServiceClientFactory.Create(storageUri, _config);

            var startsOn  = DateTimeOffset.UtcNow.AddMinutes(-1);  // small clock-skew tolerance
            var expiresOn = DateTimeOffset.UtcNow.AddMinutes(5);

            var userDelegationKey = await serviceClient.GetUserDelegationKeyAsync(startsOn, expiresOn, cancellationToken).ConfigureAwait(false);

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = item.BlobContainerName,
                BlobName = item.BlobPath,
                Resource = "b",
                StartsOn = startsOn,
                ExpiresOn = expiresOn,
                ContentDisposition = $"attachment; filename=\"{Uri.EscapeDataString(System.IO.Path.GetFileName(item.SpServerRelativeUrl))}\"",
            };
            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            var blobClient = serviceClient
                .GetBlobContainerClient(item.BlobContainerName)
                .GetBlobClient(item.BlobPath);
            var sasToken = sasBuilder.ToSasQueryParameters(userDelegationKey.Value, serviceClient.AccountName).ToString();
            var sasUrl = new UriBuilder(blobClient.Uri) { Query = sasToken }.Uri.ToString();

            _logger.LogInformation(
                "Issued {Mins}m download SAS for item {ItemId} ({Path}) to {Upn}.",
                (int)(expiresOn - DateTimeOffset.UtcNow).TotalMinutes, item.ItemId, item.SpServerRelativeUrl, User.Identity?.Name ?? "(unknown)");

            // Audit trail (issue #13): persist who downloaded what, and when.
            _db.MigrationJobLogs.Add(new Entities.DBEntities.ColdStorage.MigrationJobLog
            {
                JobId = item.JobId,
                ItemId = item.ItemId,
                Status = item.Status,
                Level = (int)LogLevel.Information,
                Message = $"Download link issued for '{item.SpServerRelativeUrl}'.",
                ActorUpn = User.GetUpn(),
                Action = "Download",
            });
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new DownloadUrlResponse
            {
                Url = sasUrl,
                ExpiresAt = expiresOn.UtcDateTime,
                FileName = System.IO.Path.GetFileName(item.SpServerRelativeUrl),
                ContentLength = item.FileSize,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to issue download SAS for item {ItemId}.", item.ItemId);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"Could not issue download URL: {ex.Message}" });
        }
    }
}
