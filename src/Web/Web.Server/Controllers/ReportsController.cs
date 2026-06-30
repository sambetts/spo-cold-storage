using Entities;
using Entities.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Models.ColdStorage;
using System.Globalization;
using Web.Models.Api;
using Web.Services;

namespace Web.Controllers;

/// <summary>
/// <c>GET /api/reports/savings</c> – cost &amp; savings KPIs for the cold-storage
/// dashboard (issue #8). Aggregates the bytes reclaimed in SharePoint (from
/// completed migrations whose source was actually deleted) over an optional
/// period and estimates the Azure storage cost vs the reclaimed SPO value to
/// show net savings. Admin-only.
/// </summary>
[Authorize]
[ApiController]
[Route("api/reports")]
public class ReportsController(
    SPOColdStorageDbContext db,
    Config config,
    IColdStorageAdminAuthorizationService admin) : ControllerBase
{
    private readonly SPOColdStorageDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly Config _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly IColdStorageAdminAuthorizationService _admin = admin ?? throw new ArgumentNullException(nameof(admin));

    [HttpGet("savings")]
    public async Task<ActionResult<SavingsReportResponse>> SavingsAsync(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken cancellationToken)
    {
        if (!await _admin.IsAdminAsync(User, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }

        var azurePrice = ParseDecimal(_config.ColdStorageAzurePricePerGbMonth, 0.0198m);
        var spoPrice = ParseDecimal(_config.ColdStorageSpoPricePerGbMonth, 0.20m);

        // Reclaimed = completed migrations whose source was actually removed.
        var query = _db.MigrationJobItems
            .Where(i => i.Status == MigrationLifecycleStatus.ColdStorageMigrationCompleted
                        && i.SourceDeletedAt != null);
        if (from.HasValue)
        {
            query = query.Where(i => i.CompletedAt >= from);
        }
        if (to.HasValue)
        {
            query = query.Where(i => i.CompletedAt <= to);
        }

        var archivedCount = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var reclaimedBytes = await query.SumAsync(i => (long?)i.FileSize, cancellationToken).ConfigureAwait(false) ?? 0L;

        var breakdown = SavingsCalculator.Compute(reclaimedBytes, azurePrice, spoPrice);

        return new SavingsReportResponse
        {
            From = from,
            To = to,
            ArchivedItemCount = archivedCount,
            ReclaimedBytes = breakdown.ReclaimedBytes,
            ReclaimedGb = Math.Round(breakdown.ReclaimedGb, 3),
            AzurePricePerGbMonth = azurePrice,
            SpoPricePerGbMonth = spoPrice,
            EstimatedAzureCostPerMonth = breakdown.AzureCostPerMonth,
            EstimatedSpoValuePerMonth = breakdown.SpoValuePerMonth,
            EstimatedNetSavingsPerMonth = breakdown.NetSavingsPerMonth,
        };
    }

    private static decimal ParseDecimal(string? raw, decimal fallback)
        => decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : fallback;
}
