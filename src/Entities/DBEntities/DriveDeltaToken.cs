using Microsoft.EntityFrameworkCore;

namespace Entities.DBEntities;

/// <summary>
/// Stores delta tokens for incremental drive scans
/// Enables efficient incremental updates using Graph API delta queries
/// </summary>
public class DriveDeltaToken
{
    public string DriveId { get; set; } = string.Empty;
    public string SiteId { get; set; } = string.Empty;
    public string SiteUrl { get; set; } = string.Empty;
    public string DeltaToken { get; set; } = string.Empty;
    public DateTime LastScanDate { get; set; }
    public DateTime? LastChangeDate { get; set; }
    public int FileCount { get; set; }
    public long TotalSize { get; set; }
}
