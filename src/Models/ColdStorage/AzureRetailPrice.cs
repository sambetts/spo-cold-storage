namespace Models.ColdStorage;

/// <summary>
/// One price row from the Azure Retail Prices API
/// (<c>https://prices.azure.com/api/retail/prices</c>), trimmed to the fields the
/// savings dashboard needs (issue #8). Kept dependency-free in <c>Models</c> so the
/// selection logic can be unit-tested without an HTTP dependency.
/// </summary>
public sealed record AzureRetailPriceItem(
    string SkuName,
    string MeterName,
    string ProductName,
    string UnitOfMeasure,
    decimal UnitPrice,
    decimal TierMinimumUnits,
    string ArmRegionName,
    string CurrencyCode);

/// <summary>
/// Pure selection of the per-GB/month storage price from a set of Azure Retail
/// Prices rows. The API returns several meters per SKU (data stored plus various
/// operations) and tiered "Data Stored" rows (e.g. first 50&#160;TB, next 450&#160;TB);
/// this picks the base "Data Stored" capacity tier so the figure matches the price
/// shown on the Azure pricing page. Dependency-free and unit-tested.
/// </summary>
public static class AzureRetailPriceSelector
{
    /// <summary>
    /// Returns the base-tier "&lt;sku&gt; Data Stored" price per GB/month for
    /// <paramref name="skuName"/>, or <c>null</c> when no matching capacity meter is
    /// present. Matches the storage "Data Stored" meter measured per GB/month, and
    /// picks the lowest <see cref="AzureRetailPriceItem.TierMinimumUnits"/> (the base
    /// tier), breaking ties by the lowest unit price. When <paramref name="skuName"/>
    /// is null/empty the SKU filter is skipped (the caller already narrowed by SKU).
    /// </summary>
    public static decimal? SelectStoragePricePerGbMonth(IEnumerable<AzureRetailPriceItem> items, string? skuName)
    {
        if (items is null)
        {
            return null;
        }

        var match = items
            .Where(i => i is not null)
            .Where(i => string.IsNullOrWhiteSpace(skuName)
                        || string.Equals(i.SkuName, skuName, StringComparison.OrdinalIgnoreCase))
            .Where(i => IsDataStoredMeter(i.MeterName))
            .Where(i => IsPerGbMonth(i.UnitOfMeasure))
            .Where(i => i.UnitPrice >= 0m)
            .OrderBy(i => i.TierMinimumUnits)
            .ThenBy(i => i.UnitPrice)
            .FirstOrDefault();

        return match?.UnitPrice;
    }

    private static bool IsDataStoredMeter(string? meterName)
        => !string.IsNullOrEmpty(meterName)
           && meterName.Contains("Data Stored", StringComparison.OrdinalIgnoreCase);

    // Retail API reports storage capacity as "1 GB/Month" (occasionally "10 GB/Month");
    // match any GB/Month capacity unit and exclude byte-hour / operation units.
    private static bool IsPerGbMonth(string? unitOfMeasure)
        => !string.IsNullOrEmpty(unitOfMeasure)
           && unitOfMeasure.Contains("GB/Month", StringComparison.OrdinalIgnoreCase);
}
