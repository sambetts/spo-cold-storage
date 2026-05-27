# Azure AD Configuration Requirements

## App Registration Setup

To enable Azure Storage access with Azure AD authentication, ensure your Azure AD app registration has the following configuration:

### API Permissions

1. **Azure Storage**
   - Permission: `user_impersonation` (Delegated)
   - Scope: `https://storage.azure.com/user_impersonation`

2. **Custom API** (if applicable)
   - Permission: `access`
   - Scope: `api://5935d0e4-7401-45cf-a3a4-f8cd973b4447/access`

### Configuration Steps

1. Navigate to Azure Portal → Azure Active Directory → App Registrations
2. Select your application (Client ID: `5935d0e4-7401-45cf-a3a4-f8cd973b4447`)
3. Go to **API Permissions**
4. Click **Add a permission**
5. Select **Azure Storage**
6. Choose **Delegated permissions**
7. Check `user_impersonation`
8. Click **Add permissions**
9. If required by your organization, click **Grant admin consent**

### Storage Account Configuration

Ensure the Azure Storage account has:
- **Shared Key Access**: Disabled (key-based authentication not permitted)
- **Azure AD Authentication**: Enabled
- **RBAC Roles**: Assign appropriate roles to users/groups:
  - `Storage Blob Data Reader` - for read-only access
  - `Storage Blob Data Contributor` - for read/write access

### User Permissions

Users accessing the application must have one of the following RBAC roles assigned on the storage account:
- Storage Blob Data Reader
- Storage Blob Data Contributor
- Storage Blob Data Owner

## Environment Variables

The application requires the following environment variables in `.env.local`:

```
VITE_MSAL_CLIENT_ID=5935d0e4-7401-45cf-a3a4-f8cd973b4447
VITE_MSAL_AUTHORITY=https://login.microsoftonline.com/organizations
VITE_MSAL_SCOPES=api://5935d0e4-7401-45cf-a3a4-f8cd973b4447/access https://storage.azure.com/user_impersonation
VITE_TEAMSFX_START_LOGIN_PAGE_URL=https://localhost:5173/auth-start.html
```

## Authentication Flow

1. User authenticates via MSAL with multiple scopes
2. Access token includes both custom API and Azure Storage permissions
3. Token is used to create a `BlobServiceClient` with Azure AD credentials
4. All blob operations use Azure AD authentication instead of SAS tokens
