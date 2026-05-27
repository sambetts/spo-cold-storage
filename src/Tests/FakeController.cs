using Microsoft.AspNetCore.Mvc;
using Models;

namespace Tests;

[Route("api/[controller]")]
public class ValuesController : Controller
{
    [HttpGet]
    public IEnumerable<string> Get()
    {
        return new string[] { "value1", "value2" };
    }

    [HttpGet("/_api/v2.0/drives/{driveId}/items/{graphItemId}/analytics/allTime")]
    public ItemAnalyticsResponse GetAnalytics(string driveId, string graphItemId)
    {
        return new ItemAnalyticsResponse { };
    }
}
