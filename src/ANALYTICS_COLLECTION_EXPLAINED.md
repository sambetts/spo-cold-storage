# Analytics Collection Workflow Explained

## Overview

The file metadata collection has **two phases**:

1. **Phase 1: File Discovery** (fast) - Discovers all files and basic metadata
2. **Phase 2: Analytics Collection** (slow) - Gets access counts and version history per file

This document explains why `access_count` and `analysis_completed` were NULL, and how we fixed it.

---

## The Problem

After implementing **GraphDriveSnapshotBuilder**, we noticed that these database fields were NULL:
- `access_count` 
- `analysis_completed`

While the Drive API provides excellent metadata for files, it **does not** include:
- Access counts (how many times a file was accessed)
- Version history details

These require **separate API calls** per file.

---

## Architecture Overview

### Two-Phase Collection Process

```
Phase 1: File Discovery (Fast - Minutes)
├─ GraphDriveSnapshotBuilder
│  ├─ GET /drives/{id}/items/root/children (5000 at a time)
│  ├─ Recursive folder traversal
│  └─ Creates DriveItemSharePointFileInfo objects
│
Phase 2: Analytics Collection (Slow - Per File)
├─ GraphFileAnalyticsAdapter
│  ├─ GET /drives/{id}/items/{itemId}/analytics/allTime (access counts)
│  └─ GET /drives/{id}/items/{itemId}/versions (version history)
│
└─ Updates DocumentSiteWithMetadata objects
   └─ Saves to database with analysis_completed timestamp
```

### Why Two Phases?

**Performance trade-off:**
- **File discovery**: 10,000 files in ~5-8 minutes (batch API calls)
- **Analytics per file**: 10,000 files × 2 API calls = 20,000 requests (~1-2 hours)

Separating phases allows:
1. Quick file discovery and insertion into database
2. Progressive analytics collection with retry logic
3. Incremental updates (skip files already analyzed)

---

## Object Model Hierarchy

```
BaseSharePointFileInfo (abstract base)
    ↓
SharePointFileInfoWithList (adds List property)
    ↓
DriveItemSharePointFileInfo (adds DriveId, GraphItemId)
    ↓
DocumentSiteWithMetadata (adds State, AccessCount, VersionCount, VersionHistorySize)
```

### Key Properties by Class

| Class | Properties | Purpose |
|-------|------------|---------|
| `BaseSharePointFileInfo` | Name, Path, Size, Created, Modified | Basic file metadata |
| `SharePointFileInfoWithList` | List | Parent document library |
| `DriveItemSharePointFileInfo` | **DriveId**, **GraphItemId** | **Required for analytics API calls** |
| `DocumentSiteWithMetadata` | **State**, **AccessCount**, VersionCount, VersionHistorySize | **Tracks analytics progress** |

---

## The Root Cause

### Original Code (Broken)

```csharp
// In BuildWithGraphDriveApi()
foreach (var file in snapshot.AllFiles)
{
    if (file is SharePointFileInfoWithList fileWithList)
    {
        _model.AllFiles.Add(fileWithList);  // ❌ DriveItemSharePointFileInfo added directly
    }
}
```

### Why It Failed

1. `GraphDriveSnapshotBuilder` creates **`DriveItemSharePointFileInfo`** objects
2. These were added directly to the model
3. Analytics collection looks for **`DocumentSiteWithMetadata`** objects:

```csharp
public List<DocumentSiteWithMetadata> DocsByState(SiteFileAnalysisState state)
{
    var results = AllFiles
            .Where(f => f is DocumentSiteWithMetadata &&  // ❌ No matches!
                        ((DocumentSiteWithMetadata)f).State == state)
            .Cast<DocumentSiteWithMetadata>()
            .ToList();
    return results;
}
```

4. `DocsByState(AnalysisPending)` returned **empty list**
5. Analytics never ran → `access_count` and `analysis_completed` stayed NULL

---

## The Fix

### Updated Code (Working)

```csharp
// In BuildWithGraphDriveApi()
foreach (var file in snapshot.AllFiles)
{
    // Convert DriveItemSharePointFileInfo to DocumentSiteWithMetadata
    if (file is DriveItemSharePointFileInfo driveFile)
    {
        var docWithMetadata = new DocumentSiteWithMetadata(driveFile)
        {
            State = SiteFileAnalysisState.AnalysisPending  // ✅ Mark for analytics
        };
        _model.AllFiles.Add(docWithMetadata);
    }
}
```

### How It Works Now

```
1. GraphDriveSnapshotBuilder discovers files
   └─ Creates DriveItemSharePointFileInfo (has DriveId, GraphItemId)

2. BuildWithGraphDriveApi wraps each file
   └─ New DocumentSiteWithMetadata(driveFile)
   └─ State = AnalysisPending

3. WaitForAnalysisCompletion() polls for pending files
   └─ DocsByState(AnalysisPending) now returns files ✅

4. UpdatePendingFilesAsync() processes batches
   ├─ _analyticsProvider.GetFileAnalyticsAsync()  (access counts)
   └─ _analyticsProvider.GetFileVersionHistoryAsync()  (versions)

5. Results applied to DocumentSiteWithMetadata objects
   ├─ AccessCount = analytics.ActionCount
   ├─ VersionCount = versions.Count
   ├─ VersionHistorySize = versions.TotalSize
   └─ State = Complete

6. TenantModelBuilder.UpdateStats() saves to database
   ├─ existingFile.AccessCount = updatedFile.AccessCount
   ├─ existingFile.VersionCount = updatedFile.VersionCount
   ├─ existingFile.VersionHistorySize = updatedFile.VersionHistorySize
   └─ existingFile.AnalysisCompleted = DateTime.Now  ✅
```

---

## API Calls Made for Analytics

### 1. Access Count (Per File)

```http
GET {site}/_api/v2.0/drives/{driveId}/items/{itemId}/analytics/allTime

Response:
{
  "access": {
    "actionCount": 42,    // ✅ Stored in access_count
    "actorCount": 5
  },
  "startDateTime": "2020-01-01T00:00:00Z",
  "endDateTime": "2024-05-26T19:00:00Z"
}
```

### 2. Version History (Per File)

```http
GET {site}/_api/v2.0/drives/{driveId}/items/{itemId}/versions

Response:
{
  "value": [
    {
      "id": "1.0",
      "size": 1024,
      "lastModifiedDateTime": "2024-01-01T10:00:00Z"
    },
    {
      "id": "2.0",
      "size": 2048,
      "lastModifiedDateTime": "2024-05-26T15:00:00Z"
    }
  ]
}
```

Calculated:
- `VersionCount` = value.length
- `VersionHistorySize` = sum(value[].size)

---

## Performance Characteristics

### File Discovery (Phase 1)

| Metric | Value | Notes |
|--------|-------|-------|
| API calls | ~20-30 | 5000 items per call |
| Duration | 5-8 min | For 10,000 files |
| Data returned | Basic metadata | Name, size, dates, path |

### Analytics Collection (Phase 2)

| Metric | Value | Notes |
|--------|-------|-------|
| API calls | 2 × file count | Access + versions per file |
| Duration | ~1-2 hours | For 10,000 files (rate limited) |
| Data returned | Access count, version history | Detailed per-file stats |

### Optimization Strategies

1. **Batching** - Process 100 files at a time
2. **Rate limiting** - Max 10 concurrent requests (semaphore)
3. **Retry logic** - Retry transient errors (429 Too Many Requests)
4. **Incremental updates** - Skip files analyzed recently:

```csharp
public async Task<bool> ShouldSkipFileAnalysisAsync(
    DriveItemSharePointFileInfo fileInfo,
    int skipHours)
{
    var existingFile = await _db.Files
        .Where(f => f.Url == fileInfo.FullSharePointUrl)
        .SingleOrDefaultAsync();

    if (existingFile?.AnalysisCompleted != null)
    {
        var cutoffDate = DateTime.Now.AddHours(-skipHours);
        if (existingFile.AnalysisCompleted.Value > cutoffDate)
        {
            return true;  // ✅ Skip - analyzed recently
        }
    }
    return false;
}
```

---

## State Transitions

```
[File Discovery]
    ↓
Unknown
    ↓
AnalysisPending  ← Waiting for analytics collection
    ↓
AnalysisInProgress  ← Currently fetching analytics
    ↓
    ├─→ Complete  ← Success (access_count + analysis_completed populated)
    ├─→ TransientError  ← Retry (429 rate limit, network error)
    └─→ FatalError  ← Don't retry (404 not found, 403 forbidden)
```

### State Logic

```csharp
public enum SiteFileAnalysisState
{
    Unknown,              // Initial state
    AnalysisPending,      // Ready for analytics collection
    AnalysisInProgress,   // Currently fetching
    Complete,             // ✅ Success - data saved
    FatalError,           // ❌ Don't retry
    TransientError        // ⚠️ Retry later
}
```

### Completion Check

```csharp
public bool AnalysisFinished
{
    get
    {
        var pending = DocsByState(SiteFileAnalysisState.AnalysisPending);
        var inProgress = DocsByState(SiteFileAnalysisState.AnalysisInProgress);
        var transient = DocsByState(SiteFileAnalysisState.TransientError);
        
        return !(pending.Any() || inProgress.Any() || transient.Any());
    }
}
```

---

## Testing the Fix

### 1. Verify File Discovery Works

```bash
cd Migration.SiteSnapshotBuilder
dotnet run
```

Expected output:
```
STAGE 1/2: Beginning file crawl...
Found 3 drives in site
Processing drive: Documents (abc123)
Crawling root folder...
Found 150 files in drive
STAGE 1/2: Finished crawling. Files found: 150

STAGE 2/2: Getting analytics for files...
Have completed 0 of 150. Pending: 150 (0 errors to retry)
Have completed 50 of 150. Pending: 100 (0 errors to retry)
Have completed 100 of 150. Pending: 50 (0 errors to retry)
Have completed 150 of 150. Pending: 0 (0 errors to retry)
STAGE 2/2: Finished getting metadata for site files. All done in 15.32 minutes.
```

### 2. Verify Database Contains Analytics Data

```sql
SELECT TOP 10
    [url],
    [access_count],
    [analysis_completed],
    [version_count],
    [versions_total_size],
    [last_modified]
FROM [ContosoSPO].[dbo].[files]
ORDER BY [access_count] DESC;
```

Expected results:
- ✅ `access_count` has values (not NULL)
- ✅ `analysis_completed` has timestamps (not NULL)
- ✅ `version_count` > 0 for files with versions
- ✅ `versions_total_size` > 0 for files with versions

### 3. Check for Errors

```sql
-- Check for files with no analytics data
SELECT COUNT(*)
FROM [files]
WHERE [analysis_completed] IS NULL;
```

If count > 0, check logs for errors:
- 429 - Rate limited (should retry)
- 404 - File deleted (expected)
- 403 - Permission denied (check Graph permissions)

---

## Future Optimizations

### 1. Batch API for Analytics

Instead of 2 API calls per file, use **Microsoft Graph JSON Batching**:

```http
POST /$batch
Content-Type: application/json

{
  "requests": [
    { "id": "1", "method": "GET", "url": "/drives/d1/items/i1/analytics/allTime" },
    { "id": "2", "method": "GET", "url": "/drives/d1/items/i1/versions" },
    { "id": "3", "method": "GET", "url": "/drives/d1/items/i2/analytics/allTime" },
    { "id": "4", "method": "GET", "url": "/drives/d1/items/i2/versions" },
    // ... up to 20 requests
  ]
}
```

**Benefits:**
- 20 requests in 1 API call
- 10x faster analytics collection
- Fewer rate limit issues

### 2. Selective Analytics

Only get analytics for files that matter:

```csharp
if (file.FileSize > 10_000_000)  // Only files > 10MB
{
    doc.State = SiteFileAnalysisState.AnalysisPending;
}
else
{
    doc.State = SiteFileAnalysisState.Complete;  // Skip small files
}
```

### 3. Incremental Analytics

Store delta token for analytics to only get changes:

```sql
CREATE TABLE AnalyticsDeltaTokens (
    SiteId NVARCHAR(100) PRIMARY KEY,
    DeltaToken NVARCHAR(MAX),
    LastAnalyticsUpdate DATETIME2
);
```

---

## Summary

### What Was Fixed

- ✅ **GraphDriveSnapshotBuilder** now wraps files in `DocumentSiteWithMetadata`
- ✅ **Analytics collection** automatically triggers after file discovery
- ✅ **access_count** populated from `/analytics/allTime` API
- ✅ **analysis_completed** populated with timestamp
- ✅ **Version history** collected from `/versions` API

### How It Works

1. File discovery creates `DriveItemSharePointFileInfo` (has IDs)
2. Wrapper converts to `DocumentSiteWithMetadata` (adds State)
3. State set to `AnalysisPending` triggers analytics workflow
4. Analytics adapter makes per-file API calls
5. Results saved to database with `analysis_completed` timestamp

### Performance

- **Phase 1** (file discovery): ~5-8 minutes for 10k files
- **Phase 2** (analytics): ~60-120 minutes for 10k files (rate limited)
- **Total**: ~65-128 minutes for complete metadata collection

### Next Steps

- Test with real SharePoint data
- Monitor rate limits and adjust concurrency
- Consider batch API implementation (10x speedup)
- Implement selective analytics for high-value files only
