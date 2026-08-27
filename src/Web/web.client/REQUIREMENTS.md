# Azure AD Configuration Requirements

> **The SPA does not talk to Azure Blob Storage directly.** Browsing and downloading
> cold-storage content is proxied through the API, which reads blobs with the Web App's
> **managed identity**, and access is authorized by the app's own per-container ACLs
> (`ContainerAccessService`). The SPA therefore needs **only** this app's own API scope —
> no Azure Storage delegated permission and no per-user data-plane RBAC.
>
> Do **not** grant users `Storage Blob Data Reader` to make the SPA work: it grants standing
> read access to every blob in the account and bypasses the per-container ACLs. See
> `deploy/README.md` → *Grant end-users storage access*, which is only needed if you
> separately want users to open the storage account directly (Storage Explorer / Azure portal).

## App Registration Setup

### API Permissions

1. **This app's Web API**
   - Permission: `access_as_user` (Delegated)
   - Scope: `api://<client-id>/access_as_user`

That is the only permission the SPA needs. The AAD app is created for you by
`deploy/deploy-spo.ps1 -Phase AadApp`.

### Configuration Steps

1. Navigate to Azure Portal → Microsoft Entra ID → App Registrations
2. Select your application
3. Go to **API Permissions** and confirm the delegated scope for this app's own API is present
4. If required by your organization, click **Grant admin consent**

### Storage Account Configuration

The deploy configures this for you. For reference, the storage account runs with:

- **Shared Key Access**: Disabled (key-based authentication not permitted)
- **Microsoft Entra Authentication**: Enabled
- **RBAC**: `Storage Blob Data Contributor` for the **Web App** and **Function** managed
  identities. End users need no role assignment for the SPA to work.

## Environment Variables

`deploy/deploy-spo.ps1 -Phase SpaConfig` writes `.env.production` for you. For local
development, copy `.env template.local` to `.env.development` and fill in:

```
VITE_MSAL_CLIENT_ID=<client-id>
VITE_MSAL_AUTHORITY=https://login.microsoftonline.com/<tenant-id>
VITE_MSAL_SCOPES=api://<client-id>/access_as_user
VITE_TEAMSFX_START_LOGIN_PAGE_URL=https://localhost:5173/auth-start.html
```

> Note: `VITE_MSAL_SCOPES` is baked in **at build time**. Rebuilding the SPA without a current
> `.env.production` makes it the literal string `"undefined"` and MSAL fails with
> `ClientConfigurationError: url_parse_error`. Always run the `SpaConfig` phase before an
> `App` deploy.

## Authentication Flow

1. User authenticates via MSAL and acquires a token for this app's API scope
2. The SPA calls the API with that token
3. The API checks the caller against the per-container ACLs
4. The API reads the blob with its **managed identity** and streams the bytes back — no SAS
   token is issued and no storage token is ever held by the browser
