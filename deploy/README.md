# SPO Cold Storage — Deployment

Automated deployment to Azure for the SPO Cold Storage solution.

The whole thing runs as a single **Windows App Service** that hosts both the
Web.Server API/SPA and the worker apps (as WebJobs).

## Layout

```
deploy/
  deploy.ps1                  Azure-side orchestrator (infra + app code)
  deploy-spo.ps1              SharePoint-side orchestrator (AAD + cert + SPFx)
  params.example.json         Copy → params.json and fill in (gitignored)
  params.json                 Real values — NOT tracked
  README.md                   This file
  .local/                     Generated cert PFX, etc. — NOT tracked
  bicep/
    main.bicep                All Azure resources (single file)
  scripts/
    _common.ps1               Shared PowerShell helpers (dot-sourced)
```

## Two scripts, two scopes

| Script | Scope |
|---|---|
| `deploy.ps1` | Azure resources, Web.Server, WebJobs, app settings, SQL access. |
| `deploy-spo.ps1` | AAD app registration, certificate (generate/file/KV), SPA env config, SPFx build & deploy to SharePoint App Catalog. |

You can run them independently. Typical first-time order is:

1. `deploy-spo.ps1 -Phase AadApp` &mdash; creates the AAD app, writes `clientId`/`servicePrincipalObjectId` back into `params.json`.
2. `deploy.ps1` &mdash; provisions Azure with the now-known AAD identifiers.
3. `deploy-spo.ps1 -Phase Cert` &mdash; uploads cert to the Key Vault created in step 2.
4. `deploy-spo.ps1 -Phase SpaConfig` then `deploy.ps1 -Phase App` &mdash; re-publish the SPA with the AAD `clientId` baked into `.env.production`.
5. `deploy-spo.ps1 -Phase Spfx, SpfxDeploy` &mdash; build + ship the SPFx package to the App Catalog.

## Prerequisites

- PowerShell **7.2+**
- Azure CLI **2.55+** (`az`) — `az login` to the target tenant first
- .NET SDK **10.0+**
- Node.js LTS (the Web.Server csproj builds the React SPA)
- Git
- A pre-existing Azure AD app registration for the API (see main repo `README.md`).
  Note its **Client ID**, **Tenant ID**, and the **Service Principal Object ID**.
- A pre-existing Entra group (recommended) or user that will be SQL Entra admin.
  Note its **Object ID** and **display name**. You must be a member of this group
  for the `Sql` phase to be able to grant the Web App MSI db_owner.
- The AAD app registration's **client secret** value (provided at runtime —
  never written to params.json).

## First-time setup

1. Copy the params template:

   ```powershell
   Copy-Item deploy/params.example.json deploy/params.json
   ```

2. Edit `deploy/params.json` with your subscription, tenant, region, resource
   names, and the Entra IDs above.

3. Log in to Azure:

   ```powershell
   az login --tenant <your-tenant-id>
   az account set --subscription <subscription-id>
   ```

4. Run the full deployment:

   ```powershell
   ./deploy/deploy.ps1
   ```

   You'll be prompted for the AAD client secret. Alternatively:

   ```powershell
   $env:SPOCS_AAD_CLIENT_SECRET = '<value>'
   ./deploy/deploy.ps1 -SkipConfirm
   ```

## Phases (deploy.ps1 — Azure)

Run a single phase with `-Phase <name>`. Phases are idempotent.

| Phase    | What it does |
|----------|--------------|
| Prereqs  | Verifies local tooling versions, az login, registers resource providers. |
| Validate | Parses + strictly validates `params.json`; checks Azure resource-name rules and global uniqueness. |
| Infra    | Creates the resource group (if missing), runs `bicep what-if`, deploys `main.bicep`. |
| Secrets  | Writes the AAD client secret to Key Vault (the other secrets are populated by Bicep itself from listKeys output). |
| Sql      | Connects to Azure SQL as the Entra admin and grants the Web App MSI `db_owner` on the app DB. |
| App      | `dotnet publish` Web.Server + the 3 workers, assembles a single zip with `App_Data\jobs\continuous\Migration.Migrator` + `App_Data\jobs\triggered\{Migration.Indexer,Migration.SiteSnapshotBuilder}`, sets App Service app settings (Key Vault references), zip-deploys, restarts. |
| Smoke    | Probes the web app URL and lists WebJobs / app settings as a sanity check. |

## Phases (deploy-spo.ps1 — SharePoint)

| Phase      | What it does |
|------------|--------------|
| Prereqs    | Checks PnP.PowerShell (auto-installs if missing), Node 18.x (SPFx 1.19 requirement). |
| AadApp     | Creates / updates the AAD app registration. Adds SPA redirect URI = `https://<webApp>.azurewebsites.net`, exposes API scope `access_as_user`, adds Microsoft Graph `User.Read` (delegated) + SharePoint `Sites.FullControl.All` (application) permissions, requests admin consent. Writes the resulting `clientId` + `servicePrincipalObjectId` back into `params.json`. |
| Cert       | Three modes per `certificate.source`: <br/>• **generate** &mdash; New self-signed cert in `Cert:\CurrentUser\My`, exports to `deploy/.local/<name>.pfx` (gitignored) using a password from `-PfxPassword` / `$env:SPOCS_PFX_PASSWORD` / interactive prompt. <br/>• **file** &mdash; Uses your existing PFX from `certificate.pfxPath`. <br/>• **keyvault** &mdash; Reads an existing cert in the Key Vault under `azureAd.certificateName`. <br/>All modes upload the cert to KV (under `azureAd.certificateName`) and attach the public key to the AAD app's `keyCredentials`. Self-grants the deployer Key Vault Certificates Officer first (RBAC mode). |
| SpaConfig  | Writes `src/Web/web.client/.env.production` with `VITE_MSAL_*` values resolved from `params.json` + bicep outputs. Re-run `deploy.ps1 -Phase App` afterwards to rebuild the SPA. |
| Spfx       | `npm install` + `gulp bundle --ship` + `gulp package-solution --ship` in `src/SPFx/spfx-cold-storage`. Produces a `.sppkg` under `sharepoint/solution/`. |
| SpfxDeploy | PnP.PowerShell login to `sharePoint.appCatalogUrl`, uploads the `.sppkg` with `Add-PnPApp -Scope Tenant -Overwrite -Publish -SkipFeatureDeployment -Force`, then connects to `sharePoint.targetSiteRelativeUrl` and runs `Install-PnPApp`. Auth method is selectable via `-SpfxAuthMode`. |

### SpfxDeploy authentication

The `-SpfxAuthMode` switch controls how PnP authenticates to SharePoint:

| Mode | When to use | How |
|---|---|---|
| `Interactive` (default) | Running from a developer workstation with a browser | Opens a browser sign-in window |
| `DeviceLogin` | Running headless / over SSH / in CI | Prints a one-time device code; you sign in on a separate machine |
| `Certificate` | Fully automated / re-runnable | App-only auth using the PFX created by the Cert phase. Requires `SharePoint Sites.FullControl.All` (granted automatically by AadApp). The PFX is expected at `deploy/.local/<azureAd.certificateName>.pfx`. |

```powershell
./deploy/deploy-spo.ps1 -Phase SpfxDeploy -SpfxAuthMode Certificate -SkipConfirm
```

### Important: app catalog URL

The default app catalog URL on a fresh tenant is `https://<tenant>.sharepoint.com/sites/appcatalog` (not `/sites/apps`). Check yours with:

```powershell
Connect-PnPOnline -Url https://<tenant>.sharepoint.com -Interactive
Get-PnPTenantAppCatalogUrl
```

### Important: AAD app display name must match `webApiPermissionRequests.resource`

`src/SPFx/spfx-cold-storage/config/package-solution.json` declares:

```json
"webApiPermissionRequests": [
  { "resource": "SPO Cold Storage Web API", "scope": "access_as_user" }
]
```

For SharePoint to grant the SPFx solution permission to call the API, the `resource` string must exactly match the **display name** of the AAD app's service principal. Keep `aadApp.displayName` in `params.json` set to `"SPO Cold Storage Web API"` (or change the package-solution.json string to match whatever name you want — both files must agree).

Useful flags:

- `-WhatIfPreview` — Infra phase only: shows `az deployment group what-if` output and exits.
- `-SkipConfirm` — Skip the interactive confirmation prompts.
- `-ParamsFile path/to/other.json` — Use a non-default params file.

## Post-deploy: manual steps that can't be automated

After every phase has succeeded, two things still need a human:

### 1. Approve the SPFx API permission request

When `SpfxDeploy` uploads the `.sppkg`, the `webApiPermissionRequests` block in `package-solution.json` registers a pending request to call the AAD API ("SPO Cold Storage Web API", scope `access_as_user`). SharePoint will not grant this automatically — a SharePoint Administrator must approve it.

```
https://<tenant>-admin.sharepoint.com/_layouts/15/online/AdminHome.aspx#/webApiPermissionManagement
```

Or via SPO PowerShell:

```powershell
# Approve all pending requests for our resource
Get-PnPTenantServicePrincipalPermissionRequests |
    Where-Object { $_.Resource -eq 'SPO Cold Storage Web API' } |
    ForEach-Object { Approve-PnPTenantServicePrincipalPermissionRequest -RequestId $_.Id -Force }
```

Until this is approved, the SPFx Migrate / Restore commands will fail with a 401 when they call the Web API via `AadHttpClient`.

### 2. Grant end-users `Storage Blob Data Reader` on the storage account

The SPA calls Azure Blob Storage **directly** from the browser using a user-scoped token (scope `https://storage.azure.com/user_impersonation`). Without data-plane RBAC on the storage account, users hit `AuthorizationPermissionMismatch` when trying to browse cold-storage files.

**Recommended (declarative)** — list the user/group object IDs in `params.json` and re-run Infra; the Bicep template will create the role assignments:

```jsonc
"storage": {
  "userDataReaders": [
    { "objectId": "<entra-group-object-id>", "type": "Group" }
  ]
}
```

```powershell
./deploy/deploy.ps1 -Phase Infra -SkipConfirm
```

**Or ad-hoc (one user)**:

```powershell
$me = az ad signed-in-user show --query id -o tsv
az role assignment create `
  --assignee-object-id $me --assignee-principal-type User `
  --role 'Storage Blob Data Reader' `
  --scope '/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Storage/storageAccounts/<name>'
```

After granting the role, **sign out and back in** to refresh the cached token (storage scope tokens last ~1h).

### 3. Rebuild + redeploy the web app after `SpaConfig`

`SpaConfig` writes `src/Web/web.client/.env.production` but **the SPA bundle on the running App Service still has the old values baked in**. Re-run the Azure-side `App` phase to rebuild + zip-deploy:

```powershell
./deploy/deploy.ps1 -Phase App -SkipConfirm
```

If you skipped this, the in-browser sign-in flow will try to authenticate against the placeholder client ID and fail with `AADSTS700016`.

### 4. (Optional) Add the field customizer to a column

`Spfx` ships both a ListView Command Set (auto-attaches) and a Field Customizer (does NOT auto-attach to any column). To make the cold-storage status visible on a document library column, run on the target site:

```powershell
Connect-PnPOnline -Url https://<tenant>.sharepoint.com/sites/ColdStorage -Interactive
Set-PnPField -List 'Documents' -Identity 'ColdStorageStatus' -Values @{
    ClientSideComponentId = 'bcc81765-0e17-4bd7-a1a5-68a72cb5a016'
}
```

Replace the GUID with the value of `id` from `src/SPFx/spfx-cold-storage/src/extensions/coldStorageStatusField/ColdStorageStatusFieldCustomizer.manifest.json` if you've regenerated it.

## What gets provisioned

- Log Analytics workspace + Application Insights (workspace-based)
- Windows App Service Plan + Web App (system-assigned managed identity, AlwaysOn)
- Storage Account (TLS 1.2, no anonymous blob access) + the blob container, with CORS for the Web App hostname
- Key Vault (RBAC mode, soft-delete on, purge protection on) seeded with:
  - `aad-client-secret` (pushed by Secrets phase)
  - `storage-connection-string`, `servicebus-connection-string`,
    `search-query-key`, `search-admin-key` (pulled at Bicep deploy via `listKeys`)
- Service Bus namespace (Standard) + the `filediscovery` queue (5 min lock, max delivery 1000)
- Azure SQL Server (Entra-only auth) + Database
- Azure AI Search (basic SKU by default)
- RBAC role assignments for the Web App MSI:
  - Key Vault Secrets User (so `@Microsoft.KeyVault` references resolve)
  - Storage Blob Data Contributor
  - Service Bus Data Sender + Receiver
  - Search Index Data Contributor + Service Contributor
- RBAC role assignments for the AAD app registration SP (so workers can pull
  the SharePoint cert from Key Vault at runtime):
  - Key Vault Secrets User + Certificates User

## Worker layout (WebJobs)

| Worker                          | WebJob type | Why |
|---------------------------------|-------------|-----|
| `Migration.Migrator`            | continuous  | Listens on Service Bus indefinitely. Marked singleton in `settings.job` so it doesn't spin up multiple instances on scale-out plans. |
| `Migration.Indexer`             | triggered   | Exits after crawling. Continuous would re-launch in a loop. Run on demand from the portal/Kudu, or via Azure Logic Apps on a schedule. |
| `Migration.SiteSnapshotBuilder` | triggered   | Same — exits after a snapshot. |

## Secrets handling

- **No secrets are written to `params.json`.** `params.json` is gitignored.
- The AAD client secret is supplied via `-AzureAdClientSecret` (SecureString),
  `$env:SPOCS_AAD_CLIENT_SECRET`, or an interactive `Read-Host -AsSecureString`
  prompt.
- All runtime secrets are stored in Key Vault. The Web App reads them via
  `@Microsoft.KeyVault(SecretUri=…)` references resolved by its managed identity.
- The SQL connection string uses `Authentication=Active Directory Default`
  (no password); the Web App MSI is granted `db_owner` by the `Sql` phase.

## Troubleshooting

- **Bicep fails on a name collision** — globally unique names (storage, KV,
  Service Bus, SQL, Search, Web App) were already taken. Edit `naming.*` in
  `params.json` and re-run `-Phase Infra`.
- **`Sql` phase: "Login failed for user"** — the deploying user is not a member
  of the Entra group set as SQL admin. Add yourself (or use a user account that
  is) and re-run `-Phase Sql`.
- **App starts but throws ConfigurationMissingException** — a Key Vault
  reference isn't resolving. In the portal: App Service → Configuration →
  Application settings, look for red "Key Vault reference" badges. Usually means
  the Web App MSI doesn't have Key Vault Secrets User (re-run `-Phase Infra`)
  or the secret hasn't been written yet (re-run `-Phase Secrets`).
- **WebJobs not visible** — Web App → WebJobs blade, or
  `az webapp webjob continuous list -g <rg> -n <web>`. If empty, confirm the
  `dotnet publish` step succeeded and that the zip contains
  `App_Data\jobs\…\<JobName>\run.cmd`.

## Tearing down

```powershell
az group delete -n <resourceGroupName> --yes --no-wait
```

Key Vault has purge protection enabled and will be soft-deleted for 30 days.
If you need to reuse the same KV name immediately, purge it:

```powershell
az keyvault purge -n <keyVaultName>
```
