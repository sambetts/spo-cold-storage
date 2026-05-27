# What Requires SharePoint CSOM Permissions?

## The Architecture

The `Migration.SiteSnapshotBuilder` uses **TWO different SharePoint APIs**:

### 1. Microsoft Graph API (Modern REST API)
**Used for:**
- ✅ Enumerating all site collections in the tenant (`TenantModelBuilder`)
- ✅ Getting file analytics/metadata (`GraphFileAnalyticsAdapter`)
- ✅ Getting version history, access counts

**Permissions Required:**
- `Microsoft Graph API` → `Sites.Read.All` (Application)
- `Microsoft Graph API` → `Files.Read.All` (Application)

**Authentication:**
- Uses: `https://graph.microsoft.com/.default` scope
- Works with: Client Secret ✅

---

### 2. SharePoint CSOM API (Legacy Client-Side Object Model)
**Used for:**
- ❌ **Crawling site structure** (`SPOSiteCollectionLoader`)
- ❌ **Enumerating webs/subwebs** (Line 18-19: `spClient.Load(rootWeb.Webs)`)
- ❌ **Getting lists and libraries** (`SPOWebLoader.GetLists()`)
- ❌ **Paginating through list items** (`IListLoader.GetListItems()`)
- ❌ **Reading file metadata from lists**

**Permissions Required:**
- `SharePoint API` → `Sites.FullControl.All` (Application) ⚠️

**Authentication:**
- Uses: `https://yourtenant.sharepoint.com/.default` scope
- Works with: Client Secret ✅ (but needs the permission!)

---

## Why Use CSOM Instead of Graph?

Looking at `SiteListsAndLibrariesCrawler.cs` and `SPOSiteCollectionLoader.cs`, the code uses CSOM because:

1. **Pagination Control** - CSOM's `ListItemCollectionPosition` provides better control over large list pagination
2. **Legacy Code** - The crawler was likely built before Graph API had feature parity
3. **Performance** - CSOM batching can be more efficient for bulk operations
4. **Feature Completeness** - Some SharePoint-specific operations aren't available in Graph

---

## The Problem

```csharp
// Line 102 in SiteModelBuilder.cs
ctx = await AuthUtils.GetClientContext(_config, _site.RootURL, _logger, null);

// This creates a SharePoint CSOM ClientContext, which needs:
// - SharePoint API permission (not just Graph!)
// - Token with scope: https://contoso.sharepoint.com/.default
```

The `ClientContext` is then used to:
```csharp
// Line 110
var spConnector = new SPOSiteCollectionLoader(_config, _site.RootURL, _logger);

// Which does things like:
spClient.Load(rootWeb.Webs);  // Enumerate subwebs
await spClient.ExecuteQueryAsync();  // Execute CSOM query
```

---

## Can We Avoid SharePoint API Permissions?

### Option 1: Add the SharePoint Permission (Easiest - 5 minutes)
✅ Just add `Sites.FullControl.All` to SharePoint API in your app registration

**Pros:**
- Minimal effort
- Code works as-is
- Proven, stable implementation

**Cons:**
- Requires an additional API permission
- "FullControl" sounds scary (but it's app-only, scoped to the app)

---

### Option 2: Rewrite to Use Only Graph API (Major Refactoring)
Rewrite the crawler to use Microsoft Graph exclusively:

**Would need to change:**
1. `SPOSiteCollectionLoader` → Use Graph API `/sites/{site-id}/sites` for subwebs
2. `SPOWebLoader` → Use Graph API `/sites/{site-id}/lists` for lists
3. `SPOListLoader` → Use Graph API `/sites/{site-id}/lists/{list-id}/items` for items
4. Pagination logic → Use Graph's `@odata.nextLink` instead of `ListItemCollectionPosition`

**Pros:**
- Only needs Microsoft Graph permissions
- Modern API
- Potentially simpler auth model

**Cons:**
- **Major code rewrite** (several days of work)
- Testing required across many site types
- Graph API pagination might not handle large lists as well
- Some SharePoint-specific metadata might not be available

---

## Recommendation

### For Now: Add the SharePoint API Permission

This is the **pragmatic choice**:

1. **Go to Azure Portal** → App registrations → Your app
2. **API permissions** → Add permission → **SharePoint** → Application permissions
3. Select **`Sites.FullControl.All`** (or try `Sites.Read.All` first)
4. **Grant admin consent**
5. Update your client secret if it's expired
6. Run the app

**Time investment:** 5-10 minutes

---

### Future Enhancement: Migrate to Graph-Only

Consider this as a future improvement:

- Create a `GraphSiteCollectionLoader` to replace `SPOSiteCollectionLoader`
- Implement `IListLoader` using Graph API instead of CSOM
- Keep both implementations and make it configurable
- Test thoroughly with large lists (10k+ items)

**Time investment:** 2-3 days of development + testing

---

## What Permission Should You Use?

### For Read-Only Snapshot Operations:
Try **`Sites.Read.All`** first (less scary than FullControl)

### If Sites.Read.All Doesn't Work:
Use **`Sites.FullControl.All`**

CSOM was designed when SharePoint permissions were coarser. Even read operations sometimes require "FullControl" in the CSOM API because of how the legacy API validates permissions.

---

## Security Note

**"Sites.FullControl.All" with Application Permission:**
- ✅ App can only act on its own behalf (no user impersonation)
- ✅ Scoped to the application's client ID
- ✅ Audited in Azure AD sign-in logs
- ✅ Can be revoked instantly by removing the permission
- ⚠️ App can read/write all sites in the tenant
- ⚠️ Use managed identity or certificates in production (not client secrets)

For a **snapshot/read operation**, this permission level makes sense - you're reading site structures, not modifying them.

---

## Summary

**Q: What needs SharePoint CSOM permissions?**
**A:** The site crawler (`SiteListsAndLibrariesCrawler`) that enumerates webs, lists, and files using the SharePoint Client-Side Object Model.

**Q: Why not use Graph API only?**
**A:** The code was built using CSOM for its pagination, batching, and legacy SharePoint features. Migrating to Graph-only would require significant refactoring.

**Q: Should I add the permission or rewrite the code?**
**A:** Add the permission now (5 minutes), consider Graph API migration as a future enhancement.
