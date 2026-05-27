# Better Approaches for SharePoint Metadata Collection

## Current Approach Issues

The current crawler:
1. ❌ **Enumerates lists/items one by one** - Slow for large sites
2. ❌ **Two-phase process** - First crawl structure, then get analytics
3. ❌ **May require SharePoint CSOM permissions** - More complex auth
4. ❌ **Doesn't efficiently handle incremental updates** - Recrawls everything
5. ❌ **Limited metadata** - List items don't expose all file properties

---

## Recommended: Graph API Drives + Delta Query

### Approach 1: Direct Drive API (Best for Initial Scan)

**Use `/sites/{site-id}/drives` → `/drives/{drive-id}/root/children`**

```http
GET /sites/{site-id}/drives
# For each drive:
GET /drives/{drive-id}/root/children?$expand=listItem($expand=fields)
```

**Advantages:**
- ✅ **Richer metadata** - Drive items include size, hash, versions info
- ✅ **Faster** - Skip list abstraction, go straight to files
- ✅ **Single API** - Don't need separate analytics calls
- ✅ **Recursive** - Can traverse folders efficiently
- ✅ **Only Graph permissions** - No SharePoint API needed

**What You Get:**
```json
{
  "name": "Document.docx",
  "size": 123456,
  "createdDateTime": "...",
  "lastModifiedDateTime": "...",
  "file": {
    "hashes": {
      "quickXorHash": "...",
      "sha1Hash": "..."
    }
  },
  "listItem": {
    "fields": {
      "Modified": "...",
      "Editor": "...",
      "_ComplianceTag": "...",
      "FileLeafRef": "..."
    }
  },
  "fileSystemInfo": {...},
  "parentReference": {...}
}
```

**Implementation:**
```csharp
// 1. Get all drives in site
var drives = await graphClient.Sites[siteId].Drives.GetAsync();

// 2. For each drive, get all items recursively
foreach (var drive in drives.Value)
{
    await CrawlDriveRecursive(drive.Id, "/root");
}

async Task CrawlDriveRecursive(string driveId, string path)
{
    var items = await graphClient.Drives[driveId]
        .Root
        .ItemWithPath(path)
        .Children
        .GetAsync(config => {
            config.QueryParameters.Expand = new[] { "listItem($expand=fields)" };
            config.QueryParameters.Top = 5000;
        });
    
    foreach (var item in items.Value)
    {
        if (item.Folder != null)
        {
            // Recurse into folder
            await CrawlDriveRecursive(driveId, item.Path);
        }
        else if (item.File != null)
        {
            // Store file metadata
            StoreFileMetadata(item);
        }
    }
}
```

---

### Approach 2: Delta Query (Best for Incremental Updates)

**Use `/drives/{drive-id}/root/delta`**

```http
GET /drives/{drive-id}/root/delta?token=latest
# Store deltaToken
# Next time:
GET /drives/{drive-id}/root/delta?token={previousToken}
```

**Advantages:**
- ✅ **Incremental** - Only get changes since last scan
- ✅ **Efficient** - Don't re-scan unchanged files
- ✅ **Detects deletions** - Includes removed items
- ✅ **Fast** - Massive performance improvement for re-scans

**When to Use:**
- After initial scan (use Approach 1 first time)
- For regular updates (daily/weekly scans)
- When monitoring for changes

**Implementation:**
```csharp
// First scan
var deltaResponse = await graphClient.Drives[driveId]
    .Root
    .Delta()
    .GetAsync(config => {
        config.QueryParameters.Token = "latest";  // Get baseline + token
    });

// Store the deltaToken
var deltaToken = deltaResponse.OdataDeltaLink?.Split("token=")[1];
await SaveDeltaToken(driveId, deltaToken);

// Subsequent scans
var changes = await graphClient.Drives[driveId]
    .Root
    .Delta()
    .GetAsync(config => {
        config.QueryParameters.Token = deltaToken;  // Get only changes
    });

// Process only changed items
foreach (var item in changes.Value)
{
    if (item.Deleted != null)
    {
        // File was deleted
        DeleteFileFromDatabase(item.Id);
    }
    else
    {
        // File was added or modified
        UpdateFileInDatabase(item);
    }
}
```

---

## Alternative Approach: SharePoint Search API

**Use `/_api/search/query`**

**Advantages:**
- ✅ **Very fast** - Search index is pre-built
- ✅ **Powerful filtering** - KQL queries
- ✅ **Good for large sites** - Optimized for scale
- ✅ **Only Graph/SharePoint permissions**

**Disadvantages:**
- ⚠️ **Not real-time** - Search index has lag (minutes to hours)
- ⚠️ **May miss new files** - Need to wait for crawl
- ⚠️ **Complex queries** - KQL syntax

**When to Use:**
- For reporting/analytics (not real-time)
- For large tenant-wide scans
- When you need filtering (e.g., only .docx files)

**Example Query:**
```http
POST /_api/search/query
Content-Type: application/json

{
  "request": {
    "Querytext": "path:\"https://tenant.sharepoint.com/sites/sitename\" AND (IsDocument:1)",
    "SelectProperties": [
      "Title", "Size", "LastModifiedTime", "CreatedBy", "ModifiedBy",
      "FileExtension", "Path", "SiteTitle", "ListId", "UniqueId"
    ],
    "RowLimit": 500,
    "StartRow": 0,
    "SortList": [{"Property": "LastModifiedTime", "Direction": 1}]
  }
}
```

**KQL Examples:**
```kql
// All documents in site
path:"https://tenant.sharepoint.com/sites/sitename" AND IsDocument:1

// Large files only
Size>=10485760  // > 10MB

// Modified in last 30 days
LastModifiedTime>="2024-01-01"

// Specific file types
FileExtension:docx OR FileExtension:xlsx
```

---

## Version History

For version history, all approaches need additional calls:

### Option 1: Graph API (Per File)
```http
GET /drives/{drive-id}/items/{item-id}/versions
```

Returns:
```json
{
  "value": [
    {
      "id": "1.0",
      "lastModifiedDateTime": "...",
      "lastModifiedBy": {...},
      "size": 12345
    }
  ]
}
```

### Option 2: Batch Requests (Efficient)
```http
POST /$batch
{
  "requests": [
    {
      "id": "1",
      "method": "GET",
      "url": "/drives/drive1/items/item1/versions"
    },
    {
      "id": "2",
      "method": "GET",
      "url": "/drives/drive1/items/item2/versions"
    },
    // ... up to 20 requests
  ]
}
```

**Batch 20 version calls** in one HTTP request!

---

## Analytics Data (Access Count, etc.)

### Graph API Analytics
```http
GET /drives/{drive-id}/items/{item-id}/analytics
```

Returns:
```json
{
  "access": {
    "actionCount": 123,
    "actorCount": 45
  },
  "lastSevenDays": {
    "access": {
      "actionCount": 23,
      "actorCount": 12
    }
  }
}
```

**Note:** Not available for all files, requires SharePoint Premium in some cases.

---

## Recommended Architecture

### Phase 1: Initial Full Scan
```
1. Get all drives → /sites/{site-id}/drives
2. For each drive → /drives/{drive-id}/root/children (recursive)
3. Store deltaToken for each drive
4. Batch version requests (20 at a time) for files needing versions
```

### Phase 2: Incremental Updates
```
1. Use deltaToken → /drives/{drive-id}/root/delta?token={token}
2. Process only changed items
3. Update deltaToken
4. Run daily/weekly
```

### Phase 3: Analytics (Optional)
```
1. For important files, get analytics
2. Batch requests for efficiency
3. Run less frequently (weekly/monthly)
```

---

## Performance Comparison

| Approach | Initial Scan (10k files) | Incremental (100 changes) | Permissions Required |
|----------|-------------------------|--------------------------|---------------------|
| **CSOM List Items** | ~15-20 min | ~15-20 min (full rescan) | SharePoint API |
| **Graph List Items** | ~10-15 min | ~10-15 min (full rescan) | Graph API |
| **Graph Drive API** | ~5-8 min | ~5-8 min (full rescan) | Graph API |
| **Graph Drive + Delta** | ~5-8 min | **~30 seconds** ⚡ | Graph API |
| **Search API** | **~1-2 min** ⚡ | ~1-2 min (query) | Graph/SharePoint |

---

## Recommended Implementation

### New Class: `GraphDriveSnapshotBuilder`

```csharp
public class GraphDriveSnapshotBuilder
{
    private readonly GraphServiceClient _graphClient;
    private readonly string _siteId;

    public async Task<SiteSnapshotModel> BuildSnapshot()
    {
        var snapshot = new SiteSnapshotModel();
        
        // 1. Get all drives
        var drives = await GetDrivesAsync();
        
        // 2. For each drive, crawl files
        foreach (var drive in drives)
        {
            if (IsFirstScan(drive.Id))
            {
                // Full scan with delta token
                await CrawlDriveWithDelta(drive, snapshot);
            }
            else
            {
                // Incremental scan using stored token
                await CrawlDriveIncremental(drive, snapshot);
            }
        }
        
        // 3. Batch get versions for files that need it
        await BatchGetVersions(snapshot.FilesNeedingVersions);
        
        return snapshot;
    }
}
```

---

## Migration Path

### Step 1: Add Graph Drive Connector (Parallel to existing)
- Create `GraphDriveSnapshotBuilder`
- Test with small site first
- Compare results with CSOM approach

### Step 2: Add Delta Token Support
- Store delta tokens in database
- Implement incremental scan logic
- Schedule regular incremental updates

### Step 3: Add Batch Version Fetching
- Group version requests (20 per batch)
- Only get versions for files that changed
- Cache version counts

### Step 4: Deprecate CSOM
- Make Graph Drive the default
- Keep CSOM as fallback option
- Remove after validation period

---

## Storage Schema Addition

Add to database:

```sql
CREATE TABLE DriveDeltaTokens (
    DriveId NVARCHAR(100) PRIMARY KEY,
    SiteId NVARCHAR(100),
    DeltaToken NVARCHAR(MAX),
    LastScanDate DATETIME2,
    LastChangeDate DATETIME2
);

CREATE INDEX IDX_DriveDeltaTokens_Site ON DriveDeltaTokens(SiteId);
```

---

## Summary

**Best Approach:**
1. **Initial Scan:** Graph Drive API `/drives/{id}/root/children` (recursive)
2. **Incremental:** Graph Delta Query `/drives/{id}/root/delta?token={token}`
3. **Versions:** Batch requests (20 at a time)
4. **Analytics:** Optional, for specific files only

**Benefits:**
- 🚀 **10x faster incremental scans** (30 seconds vs 15 minutes)
- ✅ **Only Graph permissions needed** (no SharePoint API)
- 📊 **Richer metadata** (file hashes, better versioning info)
- 🔄 **Detects deletions** (delta query includes removed items)
- 💰 **Lower API costs** (fewer calls)

**Estimated Implementation:** 2-3 days for full Graph Drive approach with delta support.
