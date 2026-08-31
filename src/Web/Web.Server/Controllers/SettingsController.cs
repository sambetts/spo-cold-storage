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
        foreach (var definition in ColdStorageSettingKeys.Definitions)
        {
            rows.TryGetValue(definition.Key, out var row);
            var deployed = DeployedValueFor(definition.Key);
            var overridden = row is not null && !string.IsNullOrWhiteSpace(row.SettingValue);
            result.Add(new RuntimeSettingResponse
            {
                Key = definition.Key,
                Label = definition.Label,
                Kind = definition.Kind.ToString(),
                Choices = definition.Choices,
                Value = overridden ? row!.SettingValue! : deployed,
                DeployedValue = deployed,
                IsOverridden = overridden,
                UpdatedBy = row?.UpdatedBy,
                UpdatedAt = row?.UpdatedAt,
                Description = definition.Description,
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
        var definition = ColdStorageSettingKeys.Find(key);
        if (definition is null)
        {
            return BadRequest($"'{key}' is not a runtime-configurable setting.");
        }
        if (request is null || string.IsNullOrWhiteSpace(request.Value))
        {
            return BadRequest("A value is required.");
        }

        var value = request.Value.Trim();

        // Validate against the declared shape so the portal can't write something the
        // worker will silently ignore (and fall back to the app setting on).
        switch (definition.Kind)
        {
            case RuntimeSettingKind.Toggle:
                if (value is not ("0" or "1"))
                {
                    return BadRequest("A toggle must be '0' or '1'.");
                }
                break;
            case RuntimeSettingKind.Number:
                if (!int.TryParse(value, out var number) || number < 0)
                {
                    return BadRequest("Enter a whole number of 0 or more.");
                }
                value = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case RuntimeSettingKind.Choice:
                if (definition.Choices is null
                    || !definition.Choices.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    return BadRequest($"Choose one of: {string.Join(", ", definition.Choices ?? [])}.");
                }
                value = definition.Choices.First(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase));
                break;
        }

        var row = await _db.ColdStorageSettings
            .FirstOrDefaultAsync(s => s.SettingKey == definition.Key, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new ColdStorageSetting { SettingKey = definition.Key };
            _db.ColdStorageSettings.Add(row);
        }
        row.SettingValue = value;
        row.UpdatedBy = User.GetUpn();
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        DbColdStorageSettingsSource.InvalidateCache();

        _logger.LogInformation("Runtime setting '{Key}' set to '{Value}' by {Upn}.", definition.Key, value, row.UpdatedBy);

        return new RuntimeSettingResponse
        {
            Key = definition.Key,
            Label = definition.Label,
            Kind = definition.Kind.ToString(),
            Choices = definition.Choices,
            Value = value,
            DeployedValue = DeployedValueFor(definition.Key),
            IsOverridden = true,
            UpdatedBy = row.UpdatedBy,
            UpdatedAt = row.UpdatedAt,
            Description = definition.Description,
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

    private string DeployedValueFor(string key) => key switch
    {
        ColdStorageSettingKeys.CaptureVersionHistory => Flag(_config.ColdStorageCaptureVersionHistory),
        ColdStorageSettingKeys.SkipRetentionLabeled => Flag(_config.ColdStorageSkipRetentionLabeled),
        ColdStorageSettingKeys.DeleteBlobAfterRestore => Flag(_config.ColdStorageDeleteBlobAfterRestore),
        ColdStorageSettingKeys.MinFileSizeBytes => Num(_config.ColdStorageMinFileSizeBytes),
        ColdStorageSettingKeys.MaxAccessCount => Num(_config.ColdStorageMaxAccessCount),
        ColdStorageSettingKeys.ReconcileIntervalHours => Num(_config.ColdStorageReconcileIntervalHours),
        ColdStorageSettingKeys.OrphanPolicy => string.IsNullOrWhiteSpace(_config.ColdStorageOrphanPolicy) ? "report" : _config.ColdStorageOrphanPolicy,
        _ => "0",
    };

    private static string Flag(int value) => value > 0 ? "1" : "0";

    private static string Num(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
