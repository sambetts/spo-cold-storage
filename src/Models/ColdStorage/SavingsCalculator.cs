namespace Models.ColdStorage;

/// <summary>
/// Cost / savings figures derived from the bytes reclaimed in SharePoint by
/// archiving to cold storage (issue #8). All monetary values are per-month and
/// rounded to cents.
/// </summary>
public sealed record SavingsBreakdown(
    long ReclaimedBytes,
    double ReclaimedGb,
    decimal AzureCostPerMonth,
    decimal SpoValuePerMonth,
    decimal NetSavingsPerMonth);

/// <summary>
/// Pure cost model for the cold-storage KPI dashboard. Kept dependency-free so
/// it can be unit-tested and reused by the reporting endpoint.
/// </summary>
public static class SavingsCalculator
{
    public const double BytesPerGb = 1024d * 1024d * 1024d;

    /// <summary>
    /// Given the total bytes reclaimed in SharePoint and the per-GB/month prices,
    /// returns the estimated Azure storage cost, the value of the reclaimed SPO
    /// storage, and the net monthly saving.
    /// </summary>
    public static SavingsBreakdown Compute(long reclaimedBytes, decimal azurePricePerGbMonth, decimal spoPricePerGbMonth)
    {
        var safeBytes = reclaimedBytes < 0 ? 0 : reclaimedBytes;
        var gb = safeBytes / BytesPerGb;
        var gbDecimal = (decimal)gb;

        var azure = Round(gbDecimal * azurePricePerGbMonth);
        var spo = Round(gbDecimal * spoPricePerGbMonth);
        var net = Round(spo - azure);

        return new SavingsBreakdown(safeBytes, gb, azure, spo, net);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
