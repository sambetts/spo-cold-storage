# Fixing 401 Unauthorized Error - SharePoint Permissions Required

## Problem
Getting `401 Unauthorized` when accessing SharePoint with ClientSecret authentication.

## Root Cause
The application requests a token for SharePoint API (`https://yourtenant.sharepoint.com/.default`), but the Azure AD app registration only has Microsoft Graph permissions.

For SharePoint CSOM (Client-Side Object Model) access, you need **SharePoint API permissions**, not just Graph permissions.

## Solution: Add SharePoint API Permissions

### Step 1: Go to Azure Portal
1. Navigate to **Azure Active Directory**
2. Click **App registrations**
3. Find your app: `22222222-2222-2222-2222-222222222222`

### Step 2: Add SharePoint API Permissions
1. Click **API permissions** in the left menu
2. Click **+ Add a permission**
3. Select **SharePoint** (not Microsoft Graph!)
4. Choose **Application permissions** (not Delegated)
5. Find and select: **Sites.FullControl.All**
6. Click **Add permissions**

### Step 3: Grant Admin Consent
1. Click **Grant admin consent for [Your Tenant]**
2. Click **Yes** to confirm
3. Wait 2-5 minutes for permissions to propagate

## Required Permissions Summary

Your app registration needs **TWO different APIs**:

### Microsoft Graph API (for Graph operations)
- ✅ `Sites.Read.All` (Application permission)
- ✅ `Files.Read.All` (Application permission)

### SharePoint API (for CSOM operations)
- ⚠️ **`Sites.FullControl.All`** (Application permission) - **REQUIRED - ADD THIS!**

## Why Two APIs?

The code uses two different authentication approaches:

1. **Graph API** (`GraphThrottledHttpClient`) 
   - Uses: `https://graph.microsoft.com/.default` scope
   - For: Modern REST API operations
   
2. **SharePoint CSOM** (`AuthUtils.GetClientContext`)
   - Uses: `https://yourtenant.sharepoint.com/.default` scope
   - For: Legacy SharePoint Client-Side Object Model operations

Both use the same client secret, but request tokens for different resource APIs.

## Verification Steps

After adding permissions and granting consent:

### 1. Check API Permissions in Portal
Your app should show:

**Microsoft Graph (3)**
- Files.Read.All (Application) ✓
- Sites.Read.All (Application) ✓
- (possibly) User.Read (Delegated) ✓

**SharePoint (1)**
- Sites.FullControl.All (Application) ✓ **← Must see this!**

### 2. Test with PowerShell

Run this to verify the SharePoint API permission works:

```powershell
$tenantId = "11111111-1111-1111-1111-111111111111"
$clientId = "22222222-2222-2222-2222-222222222222"
$clientSecret = "<client-secret>"
$resource = "https://contoso.sharepoint.com"

$body = @{
    grant_type    = "client_credentials"
    client_id     = $clientId
    client_secret = $clientSecret
    resource      = $resource  # Note: 'resource' not 'scope' for SharePoint!
}

$tokenUrl = "https://login.microsoftonline.com/$tenantId/oauth2/token"  # v1.0 endpoint
$response = Invoke-RestMethod -Method Post -Uri $tokenUrl -Body $body -ContentType "application/x-www-form-urlencoded"

if ($response.access_token) {
    Write-Host "✓ Successfully obtained SharePoint access token!" -ForegroundColor Green
    Write-Host "Token expires in: $($response.expires_in) seconds"
} else {
    Write-Host "✗ Failed to get token" -ForegroundColor Red
}
```

### 3. Run the Application Again

Once permissions are added and admin consent is granted:

```powershell
cd V:\Repos\m365-poc\SPO\ColdStorage\src\Migration.SiteSnapshotBuilder
dotnet run
```

## Common Issues

### "AADSTS65001: The user or administrator has not consented"
- Solution: Click **Grant admin consent** in API permissions

### "AADSTS70011: The provided value for the input parameter 'scope' is not valid"
- Solution: Make sure you added **SharePoint** API permissions, not just Graph

### Still getting 401 after adding permissions
- Wait 5-10 minutes for permissions to propagate through Azure AD
- Try clearing any cached tokens
- Verify admin consent was granted (green checkmarks in portal)

### Permission "Sites.FullControl.All" seems excessive
- For read-only snapshot operations, you can try **Sites.Read.All** instead
- However, CSOM operations often require FullControl even for reads due to legacy API design
- Start with FullControl, then reduce if your operations succeed with Read

## Alternative: Use Graph API Only (Future Enhancement)

If you want to avoid CSOM and use only Graph API:

**Pros:**
- Only needs Microsoft Graph permissions
- Modern REST API
- Better permission granularity

**Cons:**
- Code changes required to replace CSOM calls
- May not support all legacy SharePoint features

This would be a good future enhancement to eliminate the SharePoint API dependency.

## Security Note

`Sites.FullControl.All` is a powerful permission. In production:
1. Use managed identities or certificates instead of client secrets
2. Limit the app to specific site collections if possible
3. Monitor app usage with Azure AD sign-in logs
4. Rotate client secrets regularly (every 6-12 months)
5. Consider using Microsoft Graph API exclusively in the future
