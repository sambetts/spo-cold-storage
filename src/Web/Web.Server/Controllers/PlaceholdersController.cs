using Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
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
/// <c>GET /api/placeholders/download/{itemId}</c> – authorise a download and hand
/// back a URL to <c>content/{itemId}</c>; <c>GET /api/placeholders/content/{itemId}</c>
/// streams the blob back through this API. We stream (rather than redirect the
/// browser to a blob SAS) because the storage account denies public network access,
/// so only this VNet-integrated Web App can reach the blob (via its MSI).
/// </summary>
[Authorize]
[ApiController]
[Route("api/placeholders")]
public class PlaceholdersController(
    SPOColdStorageDbContext db,
    IContainerAccessService containerAccess,
    Entities.Configuration.Config config,
    IDataProtectionProvider dataProtection,
    ILogger<PlaceholdersController> logger) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IContainerAccessService _containerAccess = containerAccess ?? throw new ArgumentNullException(nameof(containerAccess));
    private readonly Entities.Configuration.Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger<PlaceholdersController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    // Short-lived, tamper-proof token that authorises the (otherwise anonymous) content
    // stream — minted only once the ACL check in DownloadAsync has passed.
    private readonly ITimeLimitedDataProtector _downloadProtector =
        (dataProtection ?? throw new ArgumentNullException(nameof(dataProtection)))
            .CreateProtector("ColdStorage.PlaceholderDownload.v1").ToTimeLimitedDataProtector();

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private static readonly TimeSpan DownloadTokenLifetime = TimeSpan.FromMinutes(5);

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
    /// Authorises a download for the blob behind a cold-storage placeholder and returns a URL
    /// to <see cref="ContentAsync"/> (which streams the bytes), carrying a short-lived, item-
    /// scoped, tamper-proof token minted only after the ACL check here passes. We stream through
    /// this API rather than redirecting to a blob SAS URL because the storage account denies
    /// public network access — the browser can't reach the blob, but this VNet-integrated Web
    /// App can (via its MSI over the private endpoint).
    ///
    /// Auth flow:
    ///   1. User double-clicks the .url placeholder in SharePoint or browser.
    ///   2. URL points at our SPA route /cold-storage/download/{itemId}.
    ///   3. SPA performs MSAL login if needed, then hits this endpoint with a
    ///      Bearer token.
    ///   4. This endpoint checks container ACL (CanBrowse OR CanRestore - read
    ///      access only) and mints the item-scoped download token.
    ///   5. Returns { url, expiresAt }; the SPA does window.location.replace(url) and the
    ///      browser downloads via GET content/{itemId}?t=token.
    ///
    /// We never grant write/delete - users always restore through the
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

        // The storage account denies public network access, so we don't hand the browser a
        // blob SAS (it couldn't reach the blob). Instead mint a short-lived, item-scoped,
        // tamper-proof token and point the browser at our own content endpoint, which streams
        // the blob over the private endpoint. The token is minted only now the ACL check passed.
        var fileName = System.IO.Path.GetFileName(item.SpServerRelativeUrl);
        var expiresOn = DateTimeOffset.UtcNow.Add(DownloadTokenLifetime);
        var token = _downloadProtector.Protect($"{item.ItemId:N}|{User.GetUpn()}", expiresOn);
        var url = $"/api/placeholders/content/{item.ItemId}?t={Uri.EscapeDataString(token)}";

        _logger.LogInformation(
            "Issued {Mins}m download token for item {ItemId} ({Path}) to {Upn}.",
            (int)DownloadTokenLifetime.TotalMinutes, item.ItemId, item.SpServerRelativeUrl, User.Identity?.Name ?? "(unknown)");

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
            Url = url,
            ExpiresAt = expiresOn.UtcDateTime,
            FileName = fileName,
            ContentLength = item.FileSize,
        };
    }

    /// <summary>
    /// Streams the blob behind a cold-storage placeholder back to the browser, authorised by the
    /// short-lived token minted by <see cref="DownloadAsync"/>. This is what the browser is sent
    /// to (not a blob SAS) because the storage account denies public network access — only this
    /// VNet-integrated Web App can reach the blob, via its MSI over the private endpoint.
    /// Anonymous by design: the item-scoped, time-limited, tamper-proof token is the authorisation.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("content/{itemId:guid}")]
    public async Task<IActionResult> ContentAsync(Guid itemId, [FromQuery] string? t, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(t))
        {
            return Unauthorized();
        }

        string payload;
        try
        {
            payload = _downloadProtector.Unprotect(t);
        }
        catch (Exception ex)
        {
            // Expired, tampered, or minted under a rotated key — all indistinguishable, all unauthorised.
            _logger.LogWarning(ex, "Rejected cold-storage download token for item {ItemId}.", itemId);
            return Unauthorized();
        }

        // Payload is "{itemId:N}|{upn}" — the token must be the one minted for this item.
        var parts = payload.Split('|', 2);
        if (!Guid.TryParse(parts[0], out var tokenItemId) || tokenItemId != itemId)
        {
            return Unauthorized();
        }
        var upn = parts.Length > 1 ? parts[1] : "(unknown)";

        var item = await _db.MigrationJobItems
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.ItemId == itemId, cancellationToken)
            .ConfigureAwait(false);
        if (item is null || string.IsNullOrEmpty(item.BlobContainerName) || string.IsNullOrEmpty(item.BlobPath))
        {
            return NotFound(new { error = "No downloadable cold-storage blob for that item." });
        }

        var container = item.ContainerId is null
            ? null
            : await _db.ColdStorageContainers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.ID == item.ContainerId, cancellationToken)
                .ConfigureAwait(false);
        var storageUri = container is not null && !string.IsNullOrEmpty(container.StorageAccountUri)
            ? container.StorageAccountUri
            : _config.ConnectionStrings.Storage;

        var fileName = System.IO.Path.GetFileName(item.SpServerRelativeUrl);
        try
        {
            var serviceClient = BlobServiceClientFactory.Create(storageUri, _config);
            var blobClient = serviceClient
                .GetBlobContainerClient(item.BlobContainerName)
                .GetBlobClient(item.BlobPath);

            // Stream server-side over the private endpoint (MSI). enableRangeProcessing gives the
            // browser Content-Length + resumable/range support for large archived files.
            var stream = await blobClient.OpenReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Streaming cold-storage blob for item {ItemId} ({Path}) to {Upn}.",
                itemId, item.SpServerRelativeUrl, upn);

            return File(stream, ContentTypeFor(fileName), fileName, enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stream cold-storage blob for item {ItemId}.", itemId);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"Could not download the file: {ex.Message}" });
        }
    }

    private static string ContentTypeFor(string fileName) =>
        ContentTypes.TryGetContentType(fileName, out var contentType) ? contentType : "application/octet-stream";
}
