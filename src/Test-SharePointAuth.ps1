# Test SharePoint API Token Acquisition
# This script tests if your Azure AD app can get a token for SharePoint API

param(
    [string]$TenantId = "11111111-1111-1111-1111-111111111111",
    [string]$ClientId = "22222222-2222-2222-2222-222222222222",
    [string]$ClientSecret = "<client-secret>",
    [string]$SharePointUrl = "https://contoso.sharepoint.com"
)

Write-Host "`n=== Testing SharePoint API Authentication ===" -ForegroundColor Cyan
Write-Host "Tenant: $TenantId"
Write-Host "Client: $ClientId"
Write-Host "SharePoint: $SharePointUrl`n"

# Test 1: Get token using OAuth2 v1.0 endpoint (for SharePoint)
Write-Host "[1/3] Testing SharePoint API token (OAuth 2.0 v1.0)..." -ForegroundColor Yellow

try {
    $body = @{
        grant_type    = "client_credentials"
        client_id     = $ClientId
        client_secret = $ClientSecret
        resource      = $SharePointUrl
    }

    $tokenUrl = "https://login.microsoftonline.com/$TenantId/oauth2/token"
    $response = Invoke-RestMethod -Method Post -Uri $tokenUrl -Body $body -ContentType "application/x-www-form-urlencoded" -ErrorAction Stop

    if ($response.access_token) {
        Write-Host "  ✓ SUCCESS: Got SharePoint access token!" -ForegroundColor Green
        Write-Host "    Token type: $($response.token_type)"
        Write-Host "    Expires in: $($response.expires_in) seconds ($([int]($response.expires_in/60)) minutes)"
        Write-Host "    Resource: $($response.resource)"
        
        # Decode the token to see claims (optional)
        $tokenParts = $response.access_token.Split('.')
        if ($tokenParts.Length -ge 2) {
            $payload = $tokenParts[1]
            # Pad base64 string if needed
            while ($payload.Length % 4 -ne 0) { $payload += "=" }
            $payloadJson = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($payload))
            $claims = $payloadJson | ConvertFrom-Json
            Write-Host "    App ID: $($claims.appid)"
            Write-Host "    Roles: $($claims.roles -join ', ')" -ForegroundColor Cyan
        }
        
        $sharepointToken = $response.access_token
    } else {
        Write-Host "  ✗ FAILED: No access token received" -ForegroundColor Red
        exit 1
    }
} catch {
    Write-Host "  ✗ FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ErrorDetails.Message) {
        $errorDetails = $_.ErrorDetails.Message | ConvertFrom-Json
        Write-Host "    Error: $($errorDetails.error)" -ForegroundColor Red
        Write-Host "    Description: $($errorDetails.error_description)" -ForegroundColor Red
        
        if ($errorDetails.error -eq "invalid_client") {
            Write-Host "`n  💡 TIP: Check that your Client Secret is correct and not expired." -ForegroundColor Yellow
        }
        if ($errorDetails.error_description -like "*AADSTS65001*") {
            Write-Host "`n  💡 TIP: You need to grant admin consent for SharePoint API permissions." -ForegroundColor Yellow
        }
        if ($errorDetails.error_description -like "*AADSTS70011*") {
            Write-Host "`n  💡 TIP: You need to add SharePoint API permissions to your app registration." -ForegroundColor Yellow
            Write-Host "    Go to: Azure Portal → App registrations → API permissions → Add SharePoint → Sites.FullControl.All" -ForegroundColor Yellow
        }
    }
    exit 1
}

# Test 2: Get token using OAuth2 v2.0 endpoint (for Graph)
Write-Host "`n[2/3] Testing Microsoft Graph API token (OAuth 2.0 v2.0)..." -ForegroundColor Yellow

try {
    $body = @{
        grant_type    = "client_credentials"
        client_id     = $ClientId
        client_secret = $ClientSecret
        scope         = "https://graph.microsoft.com/.default"
    }

    $tokenUrl = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"
    $response = Invoke-RestMethod -Method Post -Uri $tokenUrl -Body $body -ContentType "application/x-www-form-urlencoded" -ErrorAction Stop

    if ($response.access_token) {
        Write-Host "  ✓ SUCCESS: Got Graph access token!" -ForegroundColor Green
        Write-Host "    Expires in: $($response.expires_in) seconds ($([int]($response.expires_in/60)) minutes)"
        
        $graphToken = $response.access_token
    } else {
        Write-Host "  ✗ FAILED: No access token received" -ForegroundColor Red
    }
} catch {
    Write-Host "  ✗ FAILED: $($_.Exception.Message)" -ForegroundColor Red
}

# Test 3: Try to access SharePoint site with the token
Write-Host "`n[3/3] Testing SharePoint site access..." -ForegroundColor Yellow

try {
    $headers = @{
        Authorization = "Bearer $sharepointToken"
        Accept = "application/json;odata=verbose"
    }
    
    $siteUrl = "$SharePointUrl/_api/web"
    $siteInfo = Invoke-RestMethod -Uri $siteUrl -Headers $headers -Method Get -ErrorAction Stop
    
    Write-Host "  ✓ SUCCESS: Connected to SharePoint site!" -ForegroundColor Green
    Write-Host "    Site Title: $($siteInfo.d.Title)"
    Write-Host "    Site URL: $($siteInfo.d.Url)"
    Write-Host "    Created: $($siteInfo.d.Created)"
    
} catch {
    $statusCode = $_.Exception.Response.StatusCode.value__
    Write-Host "  ✗ FAILED: HTTP $statusCode - $($_.Exception.Message)" -ForegroundColor Red
    
    if ($statusCode -eq 401) {
        Write-Host "`n  💡 TROUBLESHOOTING 401 UNAUTHORIZED:" -ForegroundColor Yellow
        Write-Host "    1. Check Azure Portal → App registrations → API permissions" -ForegroundColor Yellow
        Write-Host "    2. Ensure you have 'SharePoint' API (not just Microsoft Graph)" -ForegroundColor Yellow
        Write-Host "    3. Add permission: Sites.FullControl.All (Application permission)" -ForegroundColor Yellow
        Write-Host "    4. Click 'Grant admin consent' and wait 5 minutes" -ForegroundColor Yellow
    }
    if ($statusCode -eq 403) {
        Write-Host "`n  💡 TROUBLESHOOTING 403 FORBIDDEN:" -ForegroundColor Yellow
        Write-Host "    1. Admin consent may not be granted" -ForegroundColor Yellow
        Write-Host "    2. The app may need higher permissions" -ForegroundColor Yellow
    }
}

Write-Host "`n=== Test Complete ===" -ForegroundColor Cyan
Write-Host "`nIf all tests passed, your app is configured correctly for Migration.SiteSnapshotBuilder!" -ForegroundColor Green
Write-Host "If tests failed, follow the troubleshooting tips above.`n" -ForegroundColor Yellow
