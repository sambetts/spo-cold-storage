using System.Text;
using System.Text.Json.Serialization;

namespace Models;

public abstract class GraphPageableResponse<T> where T : BaseGraphObject
{
    [JsonPropertyName("@odata.nextLink")]
    public string OdataNextLink { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public List<T> PageResults { get; set; } = [];
}

public abstract class BaseGraphObject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}
