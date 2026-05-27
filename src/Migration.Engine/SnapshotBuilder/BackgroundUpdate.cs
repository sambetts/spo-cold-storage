using Models;
using System.Text;

namespace Migration.Engine.SnapshotBuilder;

public class BackgroundUpdate
{
    /// <summary>
    /// Value is either DriveItemVersionInfo or ItemAnalyticsResponse
    /// </summary>
    public Dictionary<DocumentSiteWithMetadata, object> UpdateResults { get; set; } = [];
}
