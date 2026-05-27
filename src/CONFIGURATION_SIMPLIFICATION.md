# Configuration Simplification Summary

## Changes Made for Minimal Configuration Support

### Problem
The application required many configuration values that weren't actually needed for the SiteSnapshotBuilder when using ClientSecret authentication mode.

### Solution
Made configuration fields optional when they're not required for core snapshot building functionality.

## Modified Files

### 1. `Entities/Configuration/Config.cs`
**Changed:**
- `KeyVaultUrl` - Changed from `[ConfigValue]` to `[ConfigValue(true)]` (optional)
  - Only required when using Certificate authentication mode
- `BlobContainerName` - Changed from `[ConfigValue]` to `[ConfigValue(true)]` (optional)
  - Only required for migration operations (not snapshot building)

### 2. `Entities/Configuration/ConnectionStrings.cs`
**Changed:**
- `Storage` - Changed from `[ConfigValue]` to `[ConfigValue(true)]` (optional)
  - Only required for migration operations (not snapshot building)
- `ServiceBus` - Changed from `[ConfigValue]` to `[ConfigValue(true)]` (optional)
  - Only required for distributed migration operations

**Unchanged:**
- `SQLConnectionString` - Still required (database is essential)

### 3. `Entities/Configuration/DevConfig.cs`
**Added:**
- `ResetDb` property with `[ConfigValue(true)]` (optional, defaults to false)

### 4. `Migration.SiteSnapshotBuilder/appsettings.json`
**Updated with minimal configuration:**
```json
{
  "BaseServerAddress": "https://contoso.sharepoint.com",
  "ConnectionStrings": {
    "SQLConnectionString": "Server=localhost;Database=ContosoSPO;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True"
  },
  "AzureAd": {
    "TenantId": "11111111-1111-1111-1111-111111111111",
    "Secret": "<client-secret>",
    "ClientID": "22222222-2222-2222-2222-222222222222",
    "AuthenticationMode": "ClientSecret"
  }
}
```

## What's Now Optional

These configuration values are **no longer required** for SiteSnapshotBuilder:

| Configuration | Previous | Now | Reason |
|---------------|----------|-----|--------|
| `KeyVaultUrl` | Required | Optional | Only needed for Certificate auth mode |
| `BlobContainerName` | Required | Optional | Only needed for file migration operations |
| `SearchIndexName` | Optional | Optional | No change - always optional |
| `ConnectionStrings:Storage` | Required | Optional | Only needed for file migration operations |
| `ConnectionStrings:ServiceBus` | Required | Optional | Only needed for distributed operations |
| `AppInsightsInstrumentationKey` | Optional | Optional | No change - always optional |

## What's Still Required

These values **must** be provided:

| Configuration | Reason |
|---------------|--------|
| `BaseServerAddress` | SharePoint tenant URL |
| `ConnectionStrings:SQLConnectionString` | Database connection |
| `AzureAd:TenantId` | Azure AD authentication |
| `AzureAd:ClientID` | Azure AD authentication |
| `AzureAd:Secret` | Azure AD authentication |
| `AzureAd:AuthenticationMode` | Optional, defaults to "Certificate" if not specified |

## Benefits

1. **Simpler Setup** - Only 6 configuration values needed instead of 10+
2. **No Certificate Complexity** - ClientSecret mode doesn't require Key Vault setup
3. **Clearer Purpose** - Configuration values are only required when actually used
4. **Better Developer Experience** - Matches the PowerShell script simplicity
5. **Backward Compatible** - Existing configurations still work

## Testing

✅ Build succeeded with no errors
✅ Configuration loads correctly with minimal values
✅ Backward compatibility maintained for existing configs

## Usage Example

### Minimal Configuration (New)
```json
{
  "BaseServerAddress": "https://yourtenant.sharepoint.com",
  "ConnectionStrings": {
    "SQLConnectionString": "Server=localhost;Database=YourDB;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True"
  },
  "AzureAd": {
    "TenantId": "your-tenant-id",
    "Secret": "your-client-secret",
    "ClientID": "your-client-id",
    "AuthenticationMode": "ClientSecret"
  }
}
```

### Full Configuration (Still Supported)
```json
{
  "BaseServerAddress": "https://yourtenant.sharepoint.com",
  "KeyVaultUrl": "https://your-keyvault.vault.azure.net/",
  "BlobContainerName": "spexports",
  "ConnectionStrings": {
    "SQLConnectionString": "...",
    "Storage": "...",
    "ServiceBus": "..."
  },
  "AzureAd": {
    "TenantId": "your-tenant-id",
    "Secret": "your-keyvault-secret",
    "ClientID": "your-client-id",
    "AuthenticationMode": "Certificate",
    "CertificateName": "AzureAutomationSPOAccess"
  }
}
```

## Documentation

Created comprehensive documentation:
- `README.md` in Migration.SiteSnapshotBuilder - Quick start guide
- `AUTHENTICATION_MODES.md` - Detailed auth mode documentation
- `CHANGES_SUMMARY.md` - Technical implementation details

## Next Steps

To use the simplified configuration:

1. Copy the minimal configuration example above
2. Replace the placeholder values with your actual credentials
3. Run `dotnet run` from the Migration.SiteSnapshotBuilder directory
4. The application will connect using client secrets - no certificates needed!
