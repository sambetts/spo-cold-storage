using Entities;
using Entities.DBEntities.ColdStorage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Migration.Engine.Settings;
using Web.Authorization;
using Web.Models.Api;
using Web.Services;

namespace Web.Controllers;

/// <summary>
/// Admin read/write for the handful of runtime product settings that can be changed
/// from the portal without a redeploy (issue #66).
///
/// <para>
/// Precedence is DB row → this host's app setting → code default, so the deploy-time
/// parameter still supplies the initial value while an operator can change behaviour
/// live. Both hosts read the same table, so a change applies to the API and the worker.
/// </para>
///
/// <para>
/// Only keys on <see cref="ColdStorageSettingKeys"/> are accepted — this is not a
/// general-purpose config escape hatch.
/// </para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/admin/settings")]
public class SettingsController(
    SPOColdStorageDbContext db,
    IColdStorageAdminAuthorizationService admin,
    global::Entities.Configuration.Config config,
    ILogger<SettingsController> logger) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly IColdStorageAdminAuthorizationService _admin = admin ?? throw new ArgumentNullException(nameof(admin));
    private readonly global::Entities.Configuration.Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger<SettingsController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    [HttpGet]
    public async Task<ActionResult<IEnumerable<RuntimeSettingResponse>>> ListAsync(CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }

        var rows = await _db.ColdStorageSettings
            .AsNoTracking()
            .ToDictionaryAsync(s => s.SettingKey, s => s, StringComparer.OrdinalIgnoreCase, cancellationToken)
            .ConfigureAwait(false);

        var result = new List<RuntimeSettingResponse>();
        foreach (var key in ColdStorageSettingKeys.All)
        {
            var deployed = DeployedValueFor(key);
            rows.TryGetValue(key, out var row);
            var overridden = row is not null && int.TryParse(row.SettingValue, out var parsed);
            result.Add(new RuntimeSettingResponse
            {
                Key = key,
                Value = overridden && int.TryParse(row!.SettingValue, out var v) ? v : deployed,
                DeployedValue = deployed,
                IsOverridden = overridden,
                UpdatedBy = row?.UpdatedBy,
                UpdatedAt = row?.UpdatedAt,
                Description = DescriptionFor(key),
            });
        }
        return result;
    }

    [HttpPut("{key}")]
    public async Task<ActionResult<RuntimeSettingResponse>> SetAsync(
        string key, [FromBody] UpdateRuntimeSettingRequest request, CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }
        if (!ColdStorageSettingKeys.IsKnown(key))
        {
            return BadRequest($"'{key}' is not a runtime-configurable setting.");
        }
        if (request is null)
        {
            return BadRequest("A value is required.");
        }

        // Canonicalise to the stored key casing so the unique index can't be defeated
        // by a differently-cased write.
        var canonical = ColdStorageSettingKeys.All.First(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

        var row = await _db.ColdStorageSettings
            .FirstOrDefaultAsync(s => s.SettingKey == canonical, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new ColdStorageSetting { SettingKey = canonical };
            _db.ColdStorageSettings.Add(row);
        }
        row.SettingValue = request.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        row.UpdatedBy = User.GetUpn();
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        DbColdStorageSettingsSource.InvalidateCache();

        _logger.LogInformation("Runtime setting '{Key}' set to {Value} by {Upn}.", canonical, request.Value, row.UpdatedBy);

        return new RuntimeSettingResponse
        {
            Key = canonical,
            Value = request.Value,
            DeployedValue = DeployedValueFor(canonical),
            IsOverridden = true,
            UpdatedBy = row.UpdatedBy,
            UpdatedAt = row.UpdatedAt,
            Description = DescriptionFor(canonical),
        };
    }

    /// <summary>Removes the override so the deployed app setting takes effect again.</summary>
    [HttpDelete("{key}")]
    public async Task<IActionResult> ResetAsync(string key, CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }
        var row = await _db.ColdStorageSettings
            .FirstOrDefaultAsync(s => s.SettingKey == key, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return NotFound();
        }
        _db.ColdStorageSettings.Remove(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        DbColdStorageSettingsSource.InvalidateCache();

        _logger.LogInformation("Runtime setting '{Key}' reset to the deployed value by {Upn}.", key, User.GetUpn());
        return NoContent();
    }

    private int DeployedValueFor(string key) => key switch
    {
        ColdStorageSettingKeys.CaptureVersionHistory => _config.ColdStorageCaptureVersionHistory,
        _ => 0,
    };

    private static string DescriptionFor(string key) => key switch
    {
        ColdStorageSettingKeys.CaptureVersionHistory =>
            "Preserve SharePoint version history. When on, every prior version is copied to cold storage and "
            + "validated before the source file is deleted, and versions are replayed oldest-first on restore. "
            + "Uses more storage and makes archiving slower. Replayed versions are re-authored by the service "
            + "account with new timestamps — SharePoint does not allow setting a version's author or date.",
        _ => string.Empty,
    };
}
