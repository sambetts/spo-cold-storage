using System.Text;
using System.Text.Json.Serialization;

namespace Models;

public class DriveItemVersionInfo
{
    [JsonPropertyName("value")]
    public List<DriveItemVersion> Versions { get; set; } = [];
}

public class DriveItemVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = String.Empty;

    [JsonPropertyName("lastModifiedDateTime")]
    public DateTime LastModifiedDateTime { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }
}
