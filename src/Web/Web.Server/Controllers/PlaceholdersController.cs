using Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Models.ColdStorage;
using Web.Models.Api;
using Web.Services;

namespace Web.Controllers;

/// <summary>
/// <c>GET /api/placeholders/resolve</c> – look up the metadata behind a .url
/// placeholder so the system can decide whether the item is eligible for
/// restore. Returns enough metadata for the restore workflow without
/// disclosing storage details to callers who lack restore permission.
/// </summary>
[Authorize]
[ApiController]
[Route("api/placeholders")]
public class PlaceholdersController(
    SPOColdStorageDbContext db,
    IContainerAccessService containerAccess,
    ILogger<PlaceholdersController> logger) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IContainerAccessService _containerAccess = containerAccess ?? throw new ArgumentNullException(nameof(containerAccess));
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
}
