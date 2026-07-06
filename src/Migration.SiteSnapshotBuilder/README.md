# Migration.SiteSnapshotBuilder - Quick Start

## Minimal Configuration (Client Secret Mode)

To run the SiteSnapshotBuilder with **no certificates required**, you only need these settings:

### appsettings.json

```json
{
  "BaseServerAddress": "https://yourtenant.sharepoint.com",
  "ConnectionStrings": {
    "SQLConnectionString": "Server=localhost;Database=YourDatabaseName;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True"
  },
  "AzureAd": {
    "TenantId": "your-tenant-id",
    "Secret": "your-client-secret",
    "ClientID": "your-client-id",
    "AuthenticationMode": "ClientSecret"
  }
}
```

### Configuration Values Explained

| Setting | Description | Example |
|---------|-------------|---------|
| `BaseServerAddress` | Your SharePoint root URL | `https://contoso.sharepoint.com` |
| `ConnectionStrings:SQLConnectionString` | SQL Server connection string | `Server=localhost;Database=...` |
| `AzureAd:TenantId` | Azure AD Tenant ID (GUID) | `11111111-...` |
| `AzureAd:ClientID` | App Registration Client ID | `22222222-...` |
| `AzureAd:Secret` | App Registration Client Secret | `<client-secret>` |
| `AzureAd:AuthenticationMode` | Set to `"ClientSecret"` | `"ClientSecret"` |

### Optional Settings (Not Required for Snapshot Builder)

These are automatically optional and don't need to be in your config:

- ❌ `KeyVaultUrl` - Only needed for Certificate mode
- ❌ `BlobContainerName` - Only needed for migration operations
- ❌ `SearchIndexName` - Only needed for search indexing
- ❌ `ConnectionStrings:Storage` - Only needed for migration operations
- ❌ `ConnectionStrings:ServiceBus` - Only needed for distributed operations
- ❌ `AppInsightsInstrumentationKey` - Optional telemetry

## Azure AD App Registration Setup

Your app registration needs these **application permissions** (admin consent required):

### Microsoft Graph API
- ✅ `Sites.Read.All` - Read all site collections
- ✅ `Files.Read.All` - Read files in all site collections

### SharePoint API
- ✅ `Sites.FullControl.All` - Full control of all site collections

### Create a Client Secret
1. Go to Azure Portal → App Registrations → Your App
2. Navigate to **Certificates & secrets**
3. Click **New client secret**
4. Copy the **Value** (not the Secret ID) - this is your `AzureAd:Secret`

## Running the Application

```powershell
cd Migration.SiteSnapshotBuilder
dotnet run
```

## What It Does

The SiteSnapshotBuilder will:
1. Connect to Microsoft Graph using your client secret
2. Enumerate all site collections in your tenant
3. Scan each site for files and metadata
4. Store the snapshot data in your SQL database

## Database Setup

The application will automatically create the database schema on first run. Just make sure:
- SQL Server is running
- The connection string is correct
- The SQL Server user has permission to create databases (or the database already exists)

## Troubleshooting

### "KeyVaultUrl cannot be null or empty"
- Make sure `AuthenticationMode` is set to `"ClientSecret"` (not `"Certificate"`)

### Authentication Error AADSTS7000215
- Check that you're using the client secret **value**, not the secret ID
- The secret might have expired - generate a new one

### Database Connection Error
- Verify SQL Server is running
- Check the connection string format
- Ensure the database name doesn't have special characters

### "Sites.Read.All permission required"
- Grant admin consent for the API permissions in your app registration
- Wait a few minutes for permissions to propagate

## Alternative: Using Environment Variables

You can also set configuration via environment variables (useful for CI/CD):

```powershell
$env:BaseServerAddress = "https://yourtenant.sharepoint.com"
$env:ConnectionStrings__SQLConnectionString = "Server=localhost;..."
$env:AzureAd__TenantId = "your-tenant-id"
$env:AzureAd__ClientID = "your-client-id"
$env:AzureAd__Secret = "your-client-secret"
$env:AzureAd__AuthenticationMode = "ClientSecret"

dotnet run
```

Note: Double underscores (`__`) are used to represent nested configuration sections in environment variables.
