using Migration.Engine.Utils.Http;
using Models;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
namespace Migration.Engine;

public static class GraphLoader
{
    public static async Task<List<P>> LoadGraphPageable<T, P>(this AutoThrottleHttpClient httpClient, string url, ILogger ILogger) where T : GraphPageableResponse<P> where P : BaseGraphObject
    {
        var allResults = new List<P>();
        var nextUrl = url;

        ILogger.LogInformation($"Loading pagable query {url}...");

        int pageCount = 0;
        while (!string.IsNullOrEmpty(nextUrl))
        {
            var response = await httpClient.ExecuteHttpCallWithThrottleRetries(async () => await httpClient.GetAsync(nextUrl), nextUrl);

            var body = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();
            var r = JsonSerializer.Deserialize<T>(body);

            if (r != null)
            {
                allResults.AddRange(r.PageResults);
                nextUrl = r.OdataNextLink;
                pageCount++;
                ILogger.LogInformation($"Loading page {pageCount} ({nextUrl})...");
            }
        }
        ILogger.LogInformation($"{allResults.Count} for {url}.");

        return allResults;
    }
}
