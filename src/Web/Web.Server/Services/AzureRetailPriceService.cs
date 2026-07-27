using Microsoft.Extensions.Caching.Memory;
using Models.ColdStorage;
using System.Text.Json;

namespace Web.Services;

/// <summary>
/// Fetches the live Azure Storage price per GB/month for the savings dashboard
/// (issue #8) from the public, unauthenticated Azure Retail Prices API
/// (<c>https://prices.azure.com/api/retail/prices</c>), by region + retail SKU +
/// currency. Results are cached; any failure returns <c>null</c> so the caller can
/// fall back to the configured price. It never throws for a pricing problem.
/// </summary>
public interface IAzureRetailPriceService
{
    /// <summary>
    /// Returns the base-tier storage "Data Stored" price per GB/month for the given
    /// region + retail SKU + ISO currency, or <c>null</c> when it can't be determined
    /// (blank inputs, network/HTTP failure, or no matching meter). Cached.
    /// </summary>
    Task<decimal?> TryGetStoragePricePerGbMonthAsync(string region, string skuName, string currency, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AzureRetailPriceService(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<AzureRetailPriceService> logger) : IAzureRetailPriceService
{
    // Prices change rarely; cache a hit for a day and a miss/failure briefly so a
    // transient outage doesn't pin the fallback for a whole day but we also don't
    // hammer the API from a dashboard refresh loop.
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<decimal?> TryGetStoragePricePerGbMonthAsync(string region, string skuName, string currency, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(skuName))
        {
            return null;
        }

        region = region.Trim();
        skuName = skuName.Trim();
        currency = string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();

        var cacheKey = $"azretail::{region}::{skuName}::{currency}".ToLowerInvariant();
        if (cache.TryGetValue(cacheKey, out PriceCacheEntry? cached) && cached is not null)
        {
            return cached.Price;
        }

        decimal? price = null;
        try
        {
            price = await FetchAsync(region, skuName, currency, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Timeout, DNS/egress failure, non-success status, malformed body — all non-fatal:
            // log and let the caller fall back to the configured price.
            logger.LogWarning(ex, "Azure Retail Prices lookup failed for {Region}/{Sku}/{Currency}; using the configured price.", region, skuName, currency);
        }

        cache.Set(cacheKey, new PriceCacheEntry(price), price.HasValue ? SuccessTtl : FailureTtl);
        return price;
    }

    private async Task<decimal?> FetchAsync(string region, string skuName, string currency, CancellationToken cancellationToken)
    {
        // OData filter narrows the response to the blob-storage "Data Stored" rows for this SKU + region.
        var url = $"{PricesPath}?currencyCode='{Uri.EscapeDataString(currency)}'&$filter={Uri.EscapeDataString(BuildFilter(region, skuName))}";

        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<RetailResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (payload?.Items is not { Count: > 0 } items)
        {
            logger.LogInformation("Azure Retail Prices returned no rows for {Region}/{Sku}/{Currency}.", region, skuName, currency);
            return null;
        }

        var mapped = items.Select(i => new AzureRetailPriceItem(
            i.SkuName ?? string.Empty,
            i.MeterName ?? string.Empty,
            i.ProductName ?? string.Empty,
            i.UnitOfMeasure ?? string.Empty,
            i.UnitPrice,
            i.TierMinimumUnits,
            i.ArmRegionName ?? string.Empty,
            i.CurrencyCode ?? string.Empty));

        var price = AzureRetailPriceSelector.SelectStoragePricePerGbMonth(mapped, skuName);
        if (price is null)
        {
            logger.LogInformation("Azure Retail Prices had no 'Data Stored' meter for {Region}/{Sku}/{Currency}.", region, skuName, currency);
        }
        return price;
    }

    // OData string literals escape a single quote by doubling it.
    private static string EscapeODataLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    // Kept in sync with the BaseAddress configured in Program.cs.
    private const string PricesBaseUrl = "https://prices.azure.com/";
    private const string PricesPath = "api/retail/prices";

    private static string BuildFilter(string region, string skuName) =>
        "serviceName eq 'Storage' and priceType eq 'Consumption' and productName eq 'Blob Storage'"
        + $" and armRegionName eq '{EscapeODataLiteral(region)}' and skuName eq '{EscapeODataLiteral(skuName)}'";

    /// <summary>
    /// The absolute Azure Retail Prices API query URL for a region + SKU + currency — the exact,
    /// publicly verifiable source of a live price. Surfaced by the savings dashboard so an admin
    /// can open it and confirm the figure the estimate is based on (issue #8, accountability).
    /// </summary>
    public static string BuildQueryUrl(string region, string skuName, string currency)
    {
        var c = string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();
        return $"{PricesBaseUrl}{PricesPath}?currencyCode='{Uri.EscapeDataString(c)}'&$filter={Uri.EscapeDataString(BuildFilter(region, skuName))}";
    }

    private sealed record PriceCacheEntry(decimal? Price);

    private sealed class RetailResponse
    {
        public List<RetailItem>? Items { get; set; }
    }

    private sealed class RetailItem
    {
        public string? SkuName { get; set; }
        public string? MeterName { get; set; }
        public string? ProductName { get; set; }
        public string? UnitOfMeasure { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TierMinimumUnits { get; set; }
        public string? ArmRegionName { get; set; }
        public string? CurrencyCode { get; set; }
    }
}
