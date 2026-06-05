# SPO Cold Storage — Deployment

Automated end-to-end deployment to a single **Windows App Service** hosting the
Web.Server (ASP.NET Core API + React SPA) and three workers as WebJobs.

Two PowerShell orchestrators in this folder:

| Script | Owns |
|---|---|
| `deploy.ps1` | Azure side &mdash; Bicep infra, Key Vault secrets, SQL access, app publish + zip deploy. |
| `deploy-spo.ps1` | SharePoint side &mdash; AAD app registration + cert, SPA env config, SPFx build + tenant App Catalog upload. |

Both are **phase-based** and **idempotent** &mdash; re-run safely. Each reads `deploy/params.json` (gitignored).

---

## Quick start (first time, end-to-end)

### 1. Install prerequisites

| Tool | Version | Check |
|---|---|---|
| PowerShell | 7.2+ | `$PSVersionTable.PSVersion` |
| Azure CLI | 2.55+ | `az version` |
| .NET SDK | 10.0+ | `dotnet --version` |
| Node.js | 18.17+ (SPFx 1.22 needs <21) | `node --version` |
| Git | any | `git --version` |

PnP.PowerShell is auto-installed on first run.

### 2. Sign in to Azure

```powershell
az login --tenant <your-tenant-id>
az account set --subscription <your-subscription-id>
```

The user you sign in as must:
* be **Owner** (or have RBAC write rights) on the target subscription / resource group,
* be a member of the Entra group you'll list as SQL admin in `params.json`,
* have permission to create AAD apps and grant tenant admin consent (Global Administrator, Privileged Role Administrator, or Application Administrator).

### 3. Create `params.json`

```powershell
Copy-Item deploy/params.example.json deploy/params.json
```

Edit `deploy/params.json` and fill in:

| Field | Example | Notes |
|---|---|---|
| `subscription.id`, `subscription.tenantId` | GUIDs | `az account show` |
| `location` | `westus3` | Region where Azure SQL is allowed for your sub |
| `resourceGroupName` | `rg-spocs-prod` | Created if missing |
| `naming.*` | see template | Globally-unique names get checked up front |
| `azureAd.clientId`, `tenantId`, `servicePrincipalObjectId` | leave as zeros | **Auto-populated by step 4 below** |
| `sql.entraAdminObjectId`, `entraAdminLogin` | your group OID + UPN | The deploying user MUST be a member |
| `sharePoint.baseServerAddress` | `https://contoso.sharepoint.com` | Your SPO root |
| `sharePoint.appCatalogUrl` | `https://contoso.sharepoint.com/sites/appcatalog` | Confirm with `Get-PnPTenantAppCatalogUrl` |
| `sharePoint.targetSiteRelativeUrl` | `/sites/ColdStorage` | Where the SPFx web part installs |
| `storage.userDataReaders` | `[{ "objectId": "<entra-group-oid>", "type": "Group" }]` | Members get `Storage Blob Data Reader` so the SPA can browse blobs |

### 4. Provision the AAD app

```powershell
./deploy/deploy-spo.ps1 -Phase AadApp
```

`AadApp` creates the app registration, exposes the `access_as_user` API scope, adds Graph `User.Read` + SharePoint `Sites.FullControl.All`, requests admin consent, **generates a 2-year client secret** (saved to `deploy/.local/aad-client-secret.txt`, gitignored), and writes the IDs back into `params.json`.

The SPA redirect URI is set to `https://<naming.webApp>.azurewebsites.net` &mdash; this URL doesn't have to exist yet (it'll get created in step 5).

### 5. Provision Azure + deploy the app

```powershell
./deploy/deploy.ps1
```

This runs all phases in order: `Prereqs`, `Validate`, `Infra`, `Secrets`, `Sql`, `App`, `Smoke`. The `Secrets` phase picks up the AAD client secret from `deploy/.local/aad-client-secret.txt` automatically.

Takes ~6&ndash;10 minutes the first time (Azure SQL + App Service Plan are the slowest). You'll see a "Deployment plan" summary and one `yes/no` confirmation; add `-SkipConfirm` to skip it.

At the end you'll see:

```
App URL: https://app-spocs-prod-XXX.azurewebsites.net
```

### 6. Upload the SharePoint cert (now that Key Vault exists)

```powershell
./deploy/deploy-spo.ps1 -Phase Cert
```

`Cert` generates a self-signed cert (or uses your own &mdash; see [Cert phase](#cert)), uploads it to the Key Vault created in step 5, and attaches the public key to the AAD app's `keyCredentials`. The PFX lands in `deploy/.local/<certName>.pfx`.

You'll be prompted for a PFX password unless you set `$env:SPOCS_PFX_PASSWORD` first. **Save this password somewhere safe** &mdash; you'll need it again for `SpfxDeploy` with cert auth.

### 7. Build + deploy the SPFx solution

```powershell
./deploy/deploy-spo.ps1 -Phase SpaConfig
./deploy/deploy.ps1 -Phase App -SkipConfirm     # rebuild SPA with AAD config baked in
./deploy/deploy-spo.ps1 -Phase Spfx
./deploy/deploy-spo.ps1 -Phase SpfxDeploy        # uses interactive browser login by default
```

For an automated / headless `SpfxDeploy`, use the cert from step 6:

```powershell
$env:SPOCS_PFX_PASSWORD = '<the PFX password from step 6>'
./deploy/deploy-spo.ps1 -Phase SpfxDeploy -SpfxAuthMode Certificate -SkipConfirm
```

### 8. Two manual steps that can't be automated

After `SpfxDeploy` succeeds, two things still need a human in the SharePoint Admin Centre:

**a. Approve the SPFx API permission request** &mdash; the SPFx package requested permission to call your Web API. SharePoint won't auto-approve.

```
https://<tenant>-admin.sharepoint.com/_layouts/15/online/AdminHome.aspx#/webApiPermissionManagement
```

Or via PnP:

```powershell
Get-PnPTenantServicePrincipalPermissionRequests |
    Where-Object { $_.Resource -eq 'SPO Cold Storage Web API' } |
    ForEach-Object { Approve-PnPTenantServicePrincipalPermissionRequest -RequestId $_.Id -Force }
```

Without this, SPFx Migrate / Restore commands fail with HTTP 401.

**b. (If you didn't put a group in `storage.userDataReaders`)** &mdash; grant your end-users `Storage Blob Data Reader` on the storage account. The SPA calls Azure Blob Storage **directly** from the browser, so users need data-plane RBAC. See [Grant end-users storage access](#grant-end-users-storage-access) below.

That's it. Visit the App URL, sign in, add a root SharePoint URL, and verify the WebJobs are running:

```powershell
az webapp webjob continuous list -g <rg> -n <webApp> -o table
az webapp webjob triggered  list -g <rg> -n <webApp> -o table
```

---

## TL;DR (one-shot script form)

```powershell
Copy-Item deploy/params.example.json deploy/params.json   # edit it
./deploy/deploy-spo.ps1 -Phase AadApp -SkipConfirm        # 1 min
./deploy/deploy.ps1                       -SkipConfirm    # 6-10 min
./deploy/deploy-spo.ps1 -Phase Cert       -SkipConfirm    # 1 min
./deploy/deploy-spo.ps1 -Phase SpaConfig  -SkipConfirm    # instant
./deploy/deploy.ps1 -Phase App            -SkipConfirm    # 3-5 min (rebuild SPA)
./deploy/deploy-spo.ps1 -Phase Spfx       -SkipConfirm    # 2-3 min
./deploy/deploy-spo.ps1 -Phase SpfxDeploy -SkipConfirm    # 1 min (interactive sign-in)
# … then the two manual SP Admin steps above
```

---

## Layout

```
deploy/
  deploy.ps1                  Azure orchestrator
  deploy-spo.ps1              SharePoint orchestrator
  params.example.json         Copy → params.json
  params.json                 Real values — NOT tracked
  README.md                   This file
  .local/                     Generated secrets, cert PFX — NOT tracked
  bicep/main.bicep            All Azure resources, one file
  scripts/_common.ps1         Shared helpers (dot-sourced by both scripts)
```

## Phase reference

### `deploy.ps1` (Azure)

| Phase | What it does |
|---|---|
| `Prereqs` | Verifies local tooling, az login, registers resource providers. |
| `Validate` | Strict params validation; Azure-name rule checks; global uniqueness pre-flight. |
| `Infra` | Creates the resource group if missing; runs `bicep what-if`; deploys `main.bicep`. |
| `Secrets` | Writes the AAD client secret to Key Vault. Other secrets are seeded by Bicep `listKeys`. |
| `Sql` | Connects to Azure SQL as the Entra admin; grants the Web App MSI `db_owner` via `CREATE USER … FROM EXTERNAL PROVIDER`. |
| `App` | `dotnet publish` Web.Server + 3 workers; assembles a zip with `App_Data/jobs/continuous/Migration.Migrator` + `App_Data/jobs/triggered/{Migration.Indexer,Migration.SiteSnapshotBuilder}`; sets app settings (Key Vault refs); zip-deploys; restarts. |
| `Smoke` | HTTP-probes the web app; lists WebJobs. |

Useful flags: `-Phase <name>` (single phase), `-WhatIfPreview` (Infra dry-run), `-SkipConfirm`, `-ParamsFile path/to/other.json`.

### `deploy-spo.ps1` (SharePoint)

| Phase | What it does |
|---|---|
| `Prereqs` | Checks PnP.PowerShell (auto-installs CurrentUser scope) + Node version. |
| `AadApp` | Creates/updates the AAD app registration. Adds SPA redirect = `https://<webApp>.azurewebsites.net`, exposes `access_as_user` API scope, adds Graph `User.Read` + SharePoint `Sites.FullControl.All`, requests admin consent. **Generates a 2-year client secret if none exists** (saved to `deploy/.local/aad-client-secret.txt`). Writes `clientId` + `servicePrincipalObjectId` back to `params.json`. |
| <a id="cert"></a>`Cert` | Three modes per `certificate.source`: <ul><li>**`generate`** &mdash; New self-signed cert; PFX → `deploy/.local/<name>.pfx`.</li><li>**`file`** &mdash; Uses `certificate.pfxPath` from params.</li><li>**`keyvault`** &mdash; Cert already in KV; just attaches the public key to the AAD app.</li></ul>Self-grants the deployer `Key Vault Certificates Officer` (RBAC) first. PFX password from `-PfxPassword`, `$env:SPOCS_PFX_PASSWORD`, or interactive prompt. |
| `SpaConfig` | Writes `src/Web/web.client/.env.production` with `VITE_MSAL_*` resolved from `params.json` + bicep outputs. **You must run `deploy.ps1 -Phase App` afterwards to rebuild and re-deploy the SPA.** |
| `Spfx` | `npm install` + `gulp bundle --ship` + `gulp package-solution --ship` in `src/SPFx/spfx-cold-storage`. Produces `sharepoint/solution/spfx-cold-storage.sppkg`. |
| `SpfxDeploy` | PnP.PowerShell login → `Add-PnPApp -Scope Tenant -Overwrite -Publish -SkipFeatureDeployment -Force` → `Install-PnPApp` on the target site. Auth via `-SpfxAuthMode Interactive\|DeviceLogin\|Certificate`. |

## Post-deploy notes

### Grant end-users storage access

The SPA calls Azure Blob Storage **directly** with a user-scoped token (`https://storage.azure.com/user_impersonation`). Without RBAC, users see `AuthorizationPermissionMismatch` when browsing cold-storage files.

**Recommended** &mdash; list user/group object IDs in `params.json` and re-run Infra:

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

**Ad-hoc one user** (if you didn't pre-populate `userDataReaders`):

```powershell
$me = az ad signed-in-user show --query id -o tsv
az role assignment create `
  --assignee-object-id $me --assignee-principal-type User `
  --role 'Storage Blob Data Reader' `
  --scope '/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Storage/storageAccounts/<name>'
```

After granting, **sign out and back in** to the web app to refresh the cached token (storage tokens last ~1h).

### (Optional) Attach the field customizer to a column

The Spfx package ships a `ListView Command Set` (auto-attaches) and a `Field Customizer` (does NOT auto-attach). To bind the cold-storage status field customizer to a document-library column:

```powershell
Connect-PnPOnline -Url https://<tenant>.sharepoint.com/sites/ColdStorage -Interactive
Set-PnPField -List 'Documents' -Identity 'ColdStorageStatus' -Values @{
    ClientSideComponentId = 'bcc81765-0e17-4bd7-a1a5-68a72cb5a016'
}
```

Use the GUID from `src/SPFx/spfx-cold-storage/src/extensions/coldStorageStatusField/ColdStorageStatusFieldCustomizer.manifest.json`.

## What gets provisioned

- Log Analytics workspace + Application Insights (workspace-based)
- **Virtual Network** (/22 by default) with two subnets:
  - `snet-app` (/24) — delegated to `Microsoft.Web/serverFarms`; the Web App's outbound RFC1918 traffic is routed through it.
  - `snet-pe` (/24) — hosts the private endpoints; private-endpoint network policies disabled.
- **Private DNS zones** linked to the VNet so the App Service resolves data-plane hostnames to private IPs:
  - `privatelink.blob.core.windows.net`, `privatelink.vaultcore.azure.net`, `privatelink.database.windows.net`, `privatelink.servicebus.windows.net`, `privatelink.search.windows.net`
- **Private endpoints** (one per data-plane service) wired into `snet-pe` and registered into the matching DNS zone via `privateDnsZoneGroups`:
  - Storage (`blob`), Key Vault (`vault`), Azure SQL (`sqlServer`), AI Search (`searchService`), Service Bus (`namespace` — **only when `sku.serviceBus` ≠ `Basic`**, because Basic doesn't support Private Link).
- Windows App Service Plan + Web App (system-assigned MSI, AlwaysOn, integrated with `snet-app`; `WEBSITE_DNS_SERVER=168.63.129.16` so DNS resolves the privatelink zones)
- Storage Account (TLS 1.2, no anonymous blob access) + blob container, CORS for the Web App
- Key Vault (RBAC mode, soft-delete on, purge protection on) seeded with: `aad-client-secret`, `storage-connection-string`, `servicebus-connection-string`, `search-query-key`, `search-admin-key`
- Service Bus namespace + `filediscovery` queue (5 min lock, max delivery 1000)
- Azure SQL Server (Entra-only auth) + Database
- Azure AI Search (basic SKU by default)
- RBAC for the Web App MSI: Key Vault Secrets User, Storage Blob Data Contributor, Service Bus Sender + Receiver, Search Index Data Contributor + Service Contributor
- RBAC for the AAD app SP: Key Vault Secrets User + Certificates User
- RBAC for entries in `storage.userDataReaders`: Storage Blob Data Reader

### Network design — why a VNet + private endpoints?

Tenants increasingly run governance policies (Microsoft's own MCAPS, Azure Policy `Deny`/`DeployIfNotExists` initiatives, customer landing-zone baselines, …) that flip `publicNetworkAccess` to `Disabled` on data-plane services minutes after they're created. The Bicep template intentionally **does not** disable those public endpoints itself — but it provisions the VNet, private endpoints, and DNS so that *if* a policy disables a public endpoint later, the Web App and WebJobs continue to reach SQL / Key Vault / Storage / Search via the private endpoint.

The Web App uses **regional VNet integration with `vnetRouteAllEnabled=false`**. This routes only RFC1918 traffic through `snet-app` (so private-endpoint IPs are reachable), while internet-bound traffic (AAD, SharePoint Online, App Insights ingestion) continues to use the App Service platform's outbound IPs. No NAT Gateway needed.

DNS resolution for the privatelink. zones is handled by setting `WEBSITE_DNS_SERVER=168.63.129.16` (Azure's recursive resolver, which honours private DNS zones linked to the VNet the App Service is integrated with). Hostnames like `<sqlServer>.database.windows.net` resolve to the PE's RFC1918 IP, and connections to that IP are routed through `snet-app`.

> **Service Bus on Basic SKU has no private endpoint** because Azure Service Bus only supports Private Link on Standard and Premium tiers. If a governance policy disables Service Bus public access on a Basic namespace, the WebJobs lose Service Bus connectivity. Upgrade `sku.serviceBus` to `Standard` (or higher) in `params.json` to get the private path.

### Worker layout

| Worker | WebJob type | Why |
|---|---|---|
| `Migration.Migrator` | continuous | Listens on Service Bus indefinitely. Singleton via `settings.job`. |
| `Migration.Indexer` | triggered | Exits after crawling. Continuous would re-launch in a loop. |
| `Migration.SiteSnapshotBuilder` | triggered | Same — exits after a snapshot. |

## Secrets

- No secrets in `params.json` (gitignored anyway).
- AAD client secret &mdash; auto-generated by `deploy-spo.ps1 -Phase AadApp` into `deploy/.local/aad-client-secret.txt`. `deploy.ps1 -Phase Secrets` reads it from there (or from `-AzureAdClientSecret` / `$env:SPOCS_AAD_CLIENT_SECRET`).
- Storage / Service Bus / Search keys &mdash; pulled by Bicep `listKeys()` straight into Key Vault.
- SQL &mdash; no password; `Authentication=Active Directory Managed Identity` and the MSI is granted `db_owner` by the `Sql` phase.
- All runtime app settings reference Key Vault via `@Microsoft.KeyVault(SecretUri=…)`.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Bicep fails with `*AlreadyExists` for a globally-unique name (storage, KV, Search, SQL, Service Bus, Web App) | Edit `naming.*` in `params.json` and re-run `-Phase Infra`. |
| Bicep fails with `ProvisioningDisabled` for Azure SQL | Your subscription is region-restricted. Switch `location` to one where SQL is allowed (try `westus3`, `northeurope`, etc.). |
| `Sql` phase: "Login failed for user" | The deploying user isn't a member of the group in `sql.entraAdminObjectId`. Add yourself (or use a user that IS a member) and re-run `-Phase Sql`. |
| Web App returns **HTTP 500.30**, `AppServiceAppLogs` show **SqlException 47073** "Connection was denied because Deny Public Network Access is set to Yes" | A governance/Azure Policy disabled `publicNetworkAccess` on the SQL server after Bicep set it to `Enabled`. The fix is the private endpoint that Bicep already provisions — re-run `-Phase Infra` to make sure the VNet + PE + DNS resources exist, then `-Phase App` to set `WEBSITE_DNS_SERVER=168.63.129.16` and restart. Verify with `nslookup <sqlServer>.database.windows.net` from the Web App's Kudu / SSH console — you should get an RFC1918 IP. The same fix applies to Storage / Key Vault / AI Search / Service Bus (Standard SKU). |
| Web App returns 500.30, logs show **Service Bus** connectivity errors and `sku.serviceBus` is `Basic` | Basic-tier Service Bus doesn't support Private Link. Either re-enable Service Bus public access on the namespace, or upgrade `sku.serviceBus` to `Standard` and re-run `-Phase Infra` so the SB private endpoint gets provisioned. |
| Web app starts but throws `ConfigurationMissingException` | A Key Vault reference isn't resolving. Portal → App Service → Configuration → Application settings, look for red "Key Vault reference" badges. Usually means the Secrets phase wasn't run, or the Web App MSI doesn't yet have Key Vault Secrets User (re-run `-Phase Infra`). |
| Web app: `AuthorizationPermissionMismatch` when browsing blobs | The signed-in user has no `Storage Blob Data Reader`. See [Grant end-users storage access](#grant-end-users-storage-access). Sign out + back in after granting. |
| Sign-in fails with `AADSTS700016` | You ran `SpaConfig` but didn't re-run `deploy.ps1 -Phase App`. The deployed SPA bundle still has the old client ID baked in. Re-run the App phase. |
| `WebJobs not visible` in the portal | `az webapp webjob continuous list -g <rg> -n <webApp> -o table`. If empty, confirm the App phase succeeded; the zip must contain `App_Data\jobs\…\<JobName>\run.cmd`. |
| SPFx commands return 401 from `AadHttpClient` | The webApiPermissionRequest from the SPFx package needs admin approval — see [step 7a](#7-two-manual-steps-that-cant-be-automated). |
| `Add-PnPApp` prompts interactively despite `-Overwrite` | Add `-Force`. The script does this; if you're running PnP commands manually, you need it too. |
| `SpfxDeploy` fails with `AADSTS700027: The certificate with identifier used to sign the client assertion is not registered on application` | AAD takes 30&ndash;90s to propagate a newly-registered cert. The script retries automatically (up to 12 × 15s) when using `-SpfxAuthMode Certificate`. If you're invoking PnP manually, just wait a minute and retry. |

## Tearing down

```powershell
az group delete -n <resourceGroupName> --yes --no-wait
```

Key Vault has purge protection on, so the name is soft-deleted for 30 days &mdash; you cannot purge it before then. **Change `naming.keyVault` in `params.json` if you need to redeploy in the next 30 days**, otherwise wait.

To also clean up the AAD app the SPO script created:

```powershell
$appId = (Get-Content deploy/params.json | ConvertFrom-Json).azureAd.clientId
az ad app delete --id $appId
Remove-Item deploy/.local -Recurse -Force
```
