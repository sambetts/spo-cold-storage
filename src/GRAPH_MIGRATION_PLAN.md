# Graph API Migration Plan

## Goal
Replace SharePoint CSOM-based connectors with Microsoft Graph API to eliminate the need for SharePoint API permissions (`Sites.FullControl.All`).

## Current Architecture (CSOM)

### Components to Replace:
1. **SPOSiteCollectionLoader** - Enumerates webs/subsites
2. **SPOWebLoader** - Enumerates lists in a web
3. **SPOListLoader** - Paginates through list items
4. **SPOTokenManager** - Manages CSOM ClientContext

### Current Flow:
```
TenantModelBuilder (Graph) 
  → SiteModelBuilder
    → AuthUtils.GetClientContext() → ClientContext (CSOM)
      → SPOSiteCollectionLoader
        → SPOWebLoader
          → SPOListLoader (CamlQuery + ListItemCollectionPosition)
```

## Target Architecture (Graph Only)

### New Components to Create:
1. **GraphSiteCollectionLoader** - Uses `/sites/{site-id}/sites` for subsites
2. **GraphWebLoader** - Uses `/sites/{site-id}/lists` for lists  
3. **GraphListLoader** - Uses `/sites/{site-id}/lists/{list-id}/items` + `@odata.nextLink`
4. **GraphClientManager** - Manages Graph HttpClient and token refresh

### Target Flow:
```
TenantModelBuilder (Graph)
  → SiteModelBuilder
    → GraphClientManager → HttpClient (Graph)
      → GraphSiteCollectionLoader
        → GraphWebLoader
          → GraphListLoader (@odata.nextLink pagination)
```

## Implementation Steps

### Phase 1: Create Graph-based Interfaces & Base Classes
- [x] Interfaces already exist (ISiteCollectionLoader, IWebLoader, IListLoader)
- [ ] Create `GraphClientManager` (similar to SPOTokenManager but for Graph)
- [ ] Create `BaseGraphConnector` (similar to BaseSharePointConnector)

### Phase 2: Implement Graph Loaders
- [ ] **GraphSiteCollectionLoader** implementing `ISiteCollectionLoader<string>`
  - Use Graph API: `GET /sites/{site-id}/sites` 
  - Token type: `string` (for @odata.nextLink)
  
- [ ] **GraphWebLoader** implementing `IWebLoader<string>`
  - Use Graph API: `GET /sites/{site-id}/lists`
  - Filter: `?$filter=hidden eq false`
  
- [ ] **GraphListLoader** implementing `IListLoader<string>`
  - Use Graph API: `GET /sites/{site-id}/lists/{list-id}/items`
  - Query: `?$expand=fields,driveItem&$top=5000`
  - Pagination: Follow `@odata.nextLink`

### Phase 3: Update SiteModelBuilder to Support Both
- [ ] Add configuration option to choose connector type
- [ ] Keep CSOM as fallback for backward compatibility
- [ ] Default to Graph for new deployments

### Phase 4: Testing & Validation
- [ ] Test with small lists (<100 items)
- [ ] Test with large lists (10k+ items, pagination)
- [ ] Test with subsites
- [ ] Test with different list types (document library, custom list)
- [ ] Performance comparison: Graph vs CSOM

### Phase 5: Documentation & Migration Guide
- [ ] Update configuration docs
- [ ] Create migration guide for existing deployments
- [ ] Document any Graph API limitations vs CSOM

## API Mapping

### 1. Get Subsites/Webs
**CSOM:**
```csharp
spClient.Load(rootWeb.Webs);
await spClient.ExecuteQueryAsync();
```

**Graph:**
```http
GET /sites/{site-id}/sites
```

### 2. Get Lists
**CSOM:**
```csharp
_clientContext.Load(SPWeb.Lists);
await _clientContext.ExecuteQueryAsync();
// Filter: !list.Hidden && !list.IsSystemList
```

**Graph:**
```http
GET /sites/{site-id}/lists?$filter=hidden eq false&$select=id,displayName,name,list
```

### 3. Get List Items (Paginated)
**CSOM:**
```csharp
var camlQuery = new CamlQuery {
    ViewXml = "<View Scope=\"RecursiveAll\"><Query>" +
        "<OrderBy><FieldRef Name='ID' Ascending='TRUE'/></OrderBy></Query>" +
        "<RowLimit Paged=\"TRUE\">5000</RowLimit></View>"
};
camlQuery.ListItemCollectionPosition = position;
var items = list.GetItems(camlQuery);
```

**Graph:**
```http
GET /sites/{site-id}/lists/{list-id}/items?$expand=fields,driveItem&$top=5000
# Follow @odata.nextLink for next page
```

### 4. Get File Metadata
**CSOM:**
```csharp
item["Modified"], item["Created"], item["Editor"], 
item["File_x0020_Size"], item.File.ServerRelativeUrl,
item.File.VroomItemID, item.File.VroomDriveID
```

**Graph:**
```json
{
  "fields": {
    "Modified": "...",
    "Created": "...",
    "Editor": {...}
  },
  "driveItem": {
    "id": "...",
    "size": 123,
    "webUrl": "..."
  }
}
```

## Advantages of Graph API

✅ **No SharePoint API Permission Required** - Only needs Microsoft Graph permissions  
✅ **Modern REST API** - Easier to work with, better documentation  
✅ **Consistent Pagination** - `@odata.nextLink` vs CSOM's `ListItemCollectionPosition`  
✅ **Better Error Handling** - HTTP status codes vs CSOM exceptions  
✅ **Rate Limiting** - Easier to handle with `Retry-After` headers  
✅ **JSON Response** - Native .NET deserialization  

## Potential Challenges

⚠️ **Field Names** - Graph uses different field names than CSOM  
⚠️ **System Lists** - Graph filtering may differ from CSOM's IsSystemList check  
⚠️ **Attachments** - List item attachments may need different API calls  
⚠️ **Performance** - Need to test if Graph pagination handles 10k+ items as well as CSOM  
⚠️ **Feature Parity** - Some CSOM features might not be available in Graph  

## Configuration Changes

Add to `AzureAdConfig`:

```csharp
[ConfigValue(true)]
public string ConnectorType { get; set; } = "Graph";  // "Graph" or "CSOM"
```

Or create a new config section:

```json
{
  "SiteConnector": {
    "Type": "Graph",  // or "CSOM"
    "PreferGraph": true
  }
}
```

## Success Criteria

1. ✅ Can enumerate all sites without SharePoint API permission
2. ✅ Can crawl lists with same accuracy as CSOM
3. ✅ Handles pagination for large lists (10k+ items)
4. ✅ Performance within 20% of CSOM (slower is acceptable)
5. ✅ Backward compatible - CSOM still works for existing deployments
6. ✅ Unit tests pass
7. ✅ Integration tests pass with real SharePoint data

## Rollback Plan

If Graph API doesn't meet requirements:
1. Keep CSOM implementation as default
2. Make Graph opt-in via configuration
3. Document limitations in Graph implementation
4. Consider hybrid approach (Graph for discovery, CSOM for crawling)

## Timeline Estimate

- Phase 1: 2 hours
- Phase 2: 1 day (core implementation)
- Phase 3: 4 hours (integration)
- Phase 4: 1 day (testing)
- Phase 5: 2 hours (documentation)

**Total: 2-3 days** (assuming no major blockers)
