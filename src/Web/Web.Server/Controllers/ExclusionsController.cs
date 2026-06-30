using Entities;
using Entities.DBEntities.ColdStorage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Migration.Engine.Migration;
using Web.Authorization;
using Web.Models.Api;
using Web.Services;

namespace Web.Controllers;

/// <summary>
/// Admin CRUD for archiving exclusion scopes (issue #7). Lets an admin protect
/// a site, library or folder from archiving at runtime — no redeploy. All
/// actions require cold-storage admin rights.
/// </summary>
[Authorize]
[ApiController]
[Route("api/exclusions")]
public class ExclusionsController(
    SPOColdStorageDbContext db,
    IColdStorageAdminAuthorizationService admin,
    ILogger<ExclusionsController> logger) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IColdStorageAdminAuthorizationService _admin = admin ?? throw new ArgumentNullException(nameof(admin));
    private readonly ILogger<ExclusionsController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ExclusionResponse>>> ListAsync(CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }
        var rows = await _db.ColdStorageExclusions
            .AsNoTracking()
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => Map(e))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows;
    }

    [HttpPost]
    public async Task<ActionResult<ExclusionResponse>> CreateAsync([FromBody] CreateExclusionRequest request, CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }
        if (request is null
            || (string.IsNullOrWhiteSpace(request.SiteUrl) && string.IsNullOrWhiteSpace(request.ServerRelativePrefix)))
        {
            return BadRequest("Provide at least one of siteUrl or serverRelativePrefix.");
        }

        var entity = new ColdStorageExclusion
        {
            SiteUrl = string.IsNullOrWhiteSpace(request.SiteUrl) ? null : request.SiteUrl.Trim(),
            ServerRelativePrefix = string.IsNullOrWhiteSpace(request.ServerRelativePrefix) ? null : request.ServerRelativePrefix.Trim(),
            Description = request.Description,
            Enabled = true,
            CreatedBy = User.GetUpn(),
            CreatedAt = DateTime.UtcNow,
        };
        _db.ColdStorageExclusions.Add(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        DbArchiveExclusionSource.InvalidateCache();

        _logger.LogInformation("Exclusion {Id} added by {Upn}: site='{Site}' prefix='{Prefix}'.",
            entity.ID, entity.CreatedBy, entity.SiteUrl, entity.ServerRelativePrefix);
        return CreatedAtAction(nameof(ListAsync), new { id = entity.ID }, Map(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }
        var entity = await _db.ColdStorageExclusions.FirstOrDefaultAsync(e => e.ID == id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return NotFound();
        }
        _db.ColdStorageExclusions.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        DbArchiveExclusionSource.InvalidateCache();

        _logger.LogInformation("Exclusion {Id} removed by {Upn}.", id, User.GetUpn());
        return NoContent();
    }

    private static ExclusionResponse Map(ColdStorageExclusion e) => new()
    {
        Id = e.ID,
        SiteUrl = e.SiteUrl,
        ServerRelativePrefix = e.ServerRelativePrefix,
        Description = e.Description,
        Enabled = e.Enabled,
        CreatedBy = e.CreatedBy,
        CreatedAt = e.CreatedAt,
    };
}
