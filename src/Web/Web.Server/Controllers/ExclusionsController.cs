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

    // ---- File-extension rules (runtime denylist / allowlist) ----

    [HttpGet("extensions")]
    public async Task<ActionResult<IEnumerable<ExtensionRuleResponse>>> ListExtensionsAsync(CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }
        var rows = await _db.ColdStorageExtensionRules
            .AsNoTracking()
            .OrderBy(r => r.Mode).ThenBy(r => r.Extension)
            .Select(r => MapExtension(r))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows;
    }

    [HttpPost("extensions")]
    public async Task<ActionResult<ExtensionRuleResponse>> CreateExtensionAsync([FromBody] CreateExtensionRuleRequest request, CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }
        if (request is null || string.IsNullOrWhiteSpace(request.Extension))
        {
            return BadRequest("An extension is required, e.g. \".tmp\".");
        }

        var ext = NormalizeExtension(request.Extension);
        if (ext.Length <= 1)
        {
            return BadRequest("Enter a valid file extension, e.g. \".tmp\".");
        }
        // .url is a cold-storage placeholder and is ALWAYS excluded in code — refuse
        // to create a rule for it so the UI can never imply it's editable.
        if (string.Equals(ext, ".url", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("\".url\" is always excluded and cannot be changed — cold-storage placeholders must never be archived.");
        }

        var mode = ParseMode(request.Mode);

        var already = await _db.ColdStorageExtensionRules
            .FirstOrDefaultAsync(r => r.Extension == ext && r.Mode == mode, cancellationToken)
            .ConfigureAwait(false);
        if (already is not null)
        {
            if (!already.Enabled) { already.Enabled = true; await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); DbArchiveExtensionPolicySource.InvalidateCache(); }
            return Ok(MapExtension(already));
        }

        var entity = new ColdStorageExtensionRule
        {
            Extension = ext,
            Mode = mode,
            Description = request.Description,
            Enabled = true,
            CreatedBy = User.GetUpn(),
            CreatedAt = DateTime.UtcNow,
        };
        _db.ColdStorageExtensionRules.Add(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        DbArchiveExtensionPolicySource.InvalidateCache();

        _logger.LogInformation("Extension rule {Id} added by {Upn}: '{Ext}' mode={Mode}.",
            entity.ID, entity.CreatedBy, entity.Extension, entity.Mode);
        return CreatedAtAction(nameof(ListExtensionsAsync), new { id = entity.ID }, MapExtension(entity));
    }

    [HttpDelete("extensions/{id:int}")]
    public async Task<IActionResult> DeleteExtensionAsync(int id, CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }
        var entity = await _db.ColdStorageExtensionRules.FirstOrDefaultAsync(r => r.ID == id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return NotFound();
        }
        _db.ColdStorageExtensionRules.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        DbArchiveExtensionPolicySource.InvalidateCache();

        _logger.LogInformation("Extension rule {Id} removed by {Upn}.", id, User.GetUpn());
        return NoContent();
    }

    private static ExtensionRuleResponse MapExtension(ColdStorageExtensionRule r) => new()
    {
        Id = r.ID,
        Extension = r.Extension,
        Mode = r.Mode.ToString(),
        Description = r.Description,
        Enabled = r.Enabled,
        CreatedBy = r.CreatedBy,
        CreatedAt = r.CreatedAt,
    };

    private static string NormalizeExtension(string raw)
    {
        var e = raw.Trim();
        if (e.Length == 0) return string.Empty;
        if (!e.StartsWith('.')) e = "." + e;
        return e.ToLowerInvariant();
    }

    private static ArchiveExtensionRuleMode ParseMode(string? raw)
        => string.Equals(raw?.Trim(), "Include", StringComparison.OrdinalIgnoreCase)
            ? ArchiveExtensionRuleMode.Include
            : ArchiveExtensionRuleMode.Exclude;
}
