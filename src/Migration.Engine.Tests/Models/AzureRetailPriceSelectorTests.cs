using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Models;

/// <summary>
/// Locks down how the savings dashboard picks the per-GB/month storage price out of an
/// Azure Retail Prices API response (issue #8): it must take the base capacity tier of the
/// "&lt;sku&gt; Data Stored" meter and ignore operation meters and other SKUs.
/// </summary>
public class AzureRetailPriceSelectorTests
{
    private static AzureRetailPriceItem Item(
        string sku,
        string meter,
        decimal unitPrice,
        decimal tierMin = 0m,
        string unit = "1 GB/Month") =>
        new(sku, meter, "Blob Storage", unit, unitPrice, tierMin, "westeurope", "USD");

    [Fact]
    public void Picks_DataStored_Meter_For_The_Sku()
    {
        var items = new[]
        {
            Item("Hot GRS", "Hot GRS Data Stored", 0.0368m),
            Item("Hot GRS", "Hot GRS Write Operations", 1.25m, unit: "10K"),
            Item("Hot GRS", "Hot GRS Read Operations", 0.10m, unit: "10K"),
        };

        Assert.Equal(0.0368m, AzureRetailPriceSelector.SelectStoragePricePerGbMonth(items, "Hot GRS"));
    }

    [Fact]
    public void Picks_Base_Tier_When_Multiple_Capacity_Tiers()
    {
        // Retail API returns tiered "Data Stored" rows; the base tier (tierMinimumUnits 0)
        // is the price shown on the pricing page.
        var items = new[]
        {
            Item("Cool LRS", "Cool LRS Data Stored", 0.0080m, tierMin: 512000m),
            Item("Cool LRS", "Cool LRS Data Stored", 0.0088m, tierMin: 0m),
            Item("Cool LRS", "Cool LRS Data Stored", 0.0084m, tierMin: 51200m),
        };

        Assert.Equal(0.0088m, AzureRetailPriceSelector.SelectStoragePricePerGbMonth(items, "Cool LRS"));
    }

    [Fact]
    public void Ignores_Other_Skus()
    {
        var items = new[]
        {
            Item("Hot LRS", "Hot LRS Data Stored", 0.0184m),
            Item("Cold LRS", "Cold LRS Data Stored", 0.0036m),
        };

        Assert.Equal(0.0036m, AzureRetailPriceSelector.SelectStoragePricePerGbMonth(items, "Cold LRS"));
    }

    [Fact]
    public void Matches_Sku_And_Meter_CaseInsensitively()
    {
        var items = new[] { Item("Hot GRS", "Hot GRS data stored", 0.04m) };

        Assert.Equal(0.04m, AzureRetailPriceSelector.SelectStoragePricePerGbMonth(items, "hot grs"));
    }

    [Fact]
    public void Returns_Null_When_No_DataStored_Meter()
    {
        var items = new[]
        {
            Item("Hot GRS", "Hot GRS Write Operations", 1.25m, unit: "10K"),
            Item("Hot GRS", "Hot GRS Iterative Read Operations", 0.10m, unit: "10K"),
        };

        Assert.Null(AzureRetailPriceSelector.SelectStoragePricePerGbMonth(items, "Hot GRS"));
    }

    [Fact]
    public void Returns_Null_For_Empty_Or_Null_Input()
    {
        Assert.Null(AzureRetailPriceSelector.SelectStoragePricePerGbMonth([], "Hot GRS"));
        Assert.Null(AzureRetailPriceSelector.SelectStoragePricePerGbMonth(null!, "Hot GRS"));
    }

    [Fact]
    public void Skips_Sku_Filter_When_Sku_Blank()
    {
        // Caller already narrowed by SKU in the query — a blank sku shouldn't drop the row.
        var items = new[] { Item("Hot GRS", "Hot GRS Data Stored", 0.0368m) };

        Assert.Equal(0.0368m, AzureRetailPriceSelector.SelectStoragePricePerGbMonth(items, ""));
    }
}
