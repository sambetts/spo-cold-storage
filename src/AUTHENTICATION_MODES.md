# Authentication Modes Configuration

The SPO Cold Storage solution now supports **two authentication modes** for SharePoint and Microsoft Graph access:

1. **Certificate-based authentication** (default, original behavior)
2. **Client Secret authentication** (no certificates required)

## Configuration

Add the `AuthenticationMode` setting to your `appsettings.json` under the `AzureAd` section:

### Option 1: Client Secret Authentication (Recommended for Development)

No certificates or Key Vault required. Similar to the PowerShell script approach.

```json
{
  "AzureAd": {
    "TenantId": "your-tenant-id",
    "ClientID": "your-client-id",
    "Secret": "your-client-secret",
    "AuthenticationMode": "ClientSecret"
  },
  "BaseServerAddress": "https://yourtenant.sharepoint.com"
}
```

**Note:** When using `ClientSecret` mode, the `KeyVaultUrl` configuration is **not required**.

### Option 2: Certificate Authentication (Production/Secure)

Uses certificate from Azure Key Vault (original behavior).

```json
{
  "AzureAd": {
    "TenantId": "your-tenant-id",
    "ClientID": "your-client-id",
    "Secret": "your-key-vault-access-secret",
    "AuthenticationMode": "Certificate",
    "CertificateName": "AzureAutomationSPOAccess"
  },
  "BaseServerAddress": "https://yourtenant.sharepoint.com",
  "KeyVaultUrl": "https://your-keyvault.vault.azure.net/"
}
```

## Configuration Properties

| Property | Required | Default | Description |
|----------|----------|---------|-------------|
| `AuthenticationMode` | No | `"Certificate"` | Authentication method: `"Certificate"` or `"ClientSecret"` |
| `CertificateName` | No | `"AzureAutomationSPOAccess"` | Certificate name in Key Vault (only used with Certificate mode) |
| `KeyVaultUrl` | Conditional | - | Required only when `AuthenticationMode` is `"Certificate"` |
| `TenantId` | Yes | - | Azure AD Tenant ID |
| `ClientID` | Yes | - | Azure AD Application (Client) ID |
| `Secret` | Yes | - | Client secret (for Key Vault access in Certificate mode, or for direct auth in ClientSecret mode) |

## Azure AD App Registration Requirements

### For Client Secret Mode:
1. Create an app registration in Azure AD
2. Create a client secret (Certificates & secrets)
3. Grant API permissions:
   - **SharePoint**: `Sites.FullControl.All` (application permission)
   - **Microsoft Graph**: `Sites.Read.All`, `Files.Read.All` (application permissions)
4. Admin consent required for application permissions

### For Certificate Mode:
1. Create an app registration in Azure AD
2. Upload a certificate to the app registration
3. Store the certificate in Azure Key Vault
4. Grant the same API permissions as above
5. Provide Key Vault access to your application

## Migration Path

If you're currently using **certificate authentication** and want to switch to **client secrets**:

1. Generate a new client secret in your Azure AD app registration
2. Update `appsettings.json`:
   - Set `AuthenticationMode` to `"ClientSecret"`
   - Update `Secret` to be your client secret value (not the Key Vault access secret)
   - Optionally remove `KeyVaultUrl` and `CertificateName`
3. Test the connection
4. No code changes required!

## Security Considerations

- **Client Secret mode**: Simpler setup, but the secret is a shared credential. Use secure storage (Azure Key Vault, environment variables, or user secrets for development).
- **Certificate mode**: More secure, uses certificate-based authentication with private keys stored in Key Vault. Recommended for production.

## Backward Compatibility

- If `AuthenticationMode` is not specified, the system defaults to `"Certificate"` mode (original behavior)
- Existing configurations will continue to work without changes
- The `CertificateName` defaults to `"AzureAutomationSPOAccess"` if not specified

## Example: appsettings.Development.json

For local development without certificates:

```json
{
  "AzureAd": {
    "TenantId": "your-tenant-id",
    "ClientID": "your-app-client-id",
    "Secret": "your-client-secret-value",
    "AuthenticationMode": "ClientSecret"
  },
  "BaseServerAddress": "https://yourtenant.sharepoint.com",
  "BlobContainerName": "spexports",
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=SPOColdStorage;Trusted_Connection=True;"
  }
}
```

## Troubleshooting

### "KeyVaultUrl cannot be null or empty when using certificate authentication"
- Solution: Either add `KeyVaultUrl` to your config, or switch to `ClientSecret` mode

### Authentication fails with AADSTS7000215
- The client secret value is incorrect
- Make sure you're using the secret **value**, not the secret **ID**

### Authentication fails with AADSTS700016
- The ClientID is not registered in the tenant
- Verify the ClientID matches your app registration

## How It Works

The authentication logic is in `Migration.Engine/Utils/AuthUtils.cs`:

- `UseCertificateAuth` → Retrieves certificate from Key Vault, builds MSAL app with `.WithCertificate()`
- `UseClientSecretAuth` → Builds MSAL app directly with `.WithClientSecret()`

Both modes use the same MSAL (Microsoft Authentication Library) flow, just with different credential types.
