<#
.SYNOPSIS
    Sets up the SharePoint side of the SPO Cold Storage solution: AAD app
    registration, certificate, SPA config, SPFx package build & deploy.

.DESCRIPTION
    Sibling script to deploy.ps1. Run AFTER (or alongside) deploy.ps1 — most phases
    only need params.json and an Azure CLI login, but the Cert phase writes to the
    Key Vault that deploy.ps1's Infra phase provisioned.

    Phases (run with -Phase, default 'All'):
      Prereqs    Validate PnP.PowerShell, az ad ext, node, gulp availability.
      AadApp     Create/update the AAD app registration, expose `access_as_user`
                 API scope, add SPA redirect, set SharePoint Sites.FullControl.All
                 application permission + Graph User.Read delegated, request
                 admin consent. Writes the resulting clientId / SP objectId back
                 into params.json.
      Cert       Source = generate|file|keyvault. Generates a self-signed cert
                 (or uses a supplied PFX), uploads it as a Key Vault certificate,
                 and attaches the public key to the AAD app registration.
      SpaConfig  Writes src/Web/web.client/.env.production with the AAD values.
      Spfx       npm install + gulp bundle --ship + gulp package-solution --ship
                 in src/SPFx/spfx-cold-storage. Produces sharepoint/solution/*.sppkg.
      SpfxDeploy Uses PnP.PowerShell to upload the .sppkg to the tenant App Catalog
                 (publish=true) and install it on the target site.

.PARAMETER ParamsFile
    Path to params.json (default: deploy/params.json, same as deploy.ps1).

.PARAMETER Phase
    Which phase to run. Default 'All'.

.PARAMETER PfxPassword
    SecureString password for an existing PFX file (when certificate.source = 'file').
    Falls back to $env:SPOCS_PFX_PASSWORD or interactive prompt.

.PARAMETER SkipConfirm
    Skip confirmation prompts.

.EXAMPLE
    ./deploy/deploy-spo.ps1 -Phase All

.EXAMPLE
    ./deploy/deploy-spo.ps1 -Phase Cert  # rotate cert only

.NOTES
    Requirements: PowerShell 7.2+, Azure CLI, PnP.PowerShell module (auto-installed),
    Node.js 22 for the SPFx 1.22 build, gulp-cli.
#>
[CmdletBinding()]
param(
    [string]$ParamsFile = (Join-Path $PSScriptRoot 'params.json'),

    [ValidateSet('All','Prereqs','AadApp','Cert','SpaConfig','Spfx','SpfxDeploy')]
    [string]$Phase = 'All',

    [SecureString]$PfxPassword,

    [ValidateSet('Interactive','DeviceLogin','Certificate')]
    [string]$SpfxAuthMode = 'Certificate',

    [switch]$SkipConfirm
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
$env:AZURE_CORE_ONLY_SHOW_ERRORS = 'true'

# --- Paths ---
$DeployRoot  = $PSScriptRoot
$RepoRoot    = Split-Path -Parent $DeployRoot
$ScriptsRoot = Join-Path $DeployRoot 'scripts'
$SrcRoot     = Join-Path $RepoRoot   'src'
$SpaDir      = Join-Path $SrcRoot    'Web/web.client'
$SpfxDir     = Join-Path $SrcRoot    'SPFx/spfx-cold-storage'
$LocalDir    = Join-Path $DeployRoot '.local'   # gitignored; cert artifacts land here

. (Join-Path $ScriptsRoot '_common.ps1')

$global:Params  = $null
$global:Outputs = $null
$global:DeployStart = Get-Date

# ========================================================
# Helpers
# ========================================================

function Get-Params {
    if (-not $global:Params) { $global:Params = Read-Params -Path $ParamsFile }
    return $global:Params
}

function Save-Params {
    # Round-trip params.json preserving order, then write back. Use Newtonsoft via
    # PowerShell built-in ConvertTo-Json. We deliberately load the raw JSON (not the
    # validated typed object) so unknown / future fields are preserved.
    $raw = Get-Content -LiteralPath $ParamsFile -Raw | ConvertFrom-Json -Depth 30
    foreach ($p in $args) { }   # placeholder; updates happen via param refs above
    ($raw | ConvertTo-Json -Depth 30) | Set-Content -LiteralPath $ParamsFile -Encoding utf8
}

function Update-ParamsFile {
    <#
    .SYNOPSIS Mutates params.json by path (dot-notation), preserving everything else.
    .EXAMPLE Update-ParamsFile @{ 'azureAd.clientId' = $newId; 'azureAd.servicePrincipalObjectId' = $sp }
    #>
    param([Parameter(Mandatory)][hashtable]$Updates)
    $raw = Get-Content -LiteralPath $ParamsFile -Raw | ConvertFrom-Json -Depth 30
    foreach ($key in $Updates.Keys) {
        $parts = $key -split '\.'
        $node = $raw
        for ($i = 0; $i -lt $parts.Length - 1; $i++) {
            if (-not $node.PSObject.Properties[$parts[$i]]) {
                Add-Member -InputObject $node -NotePropertyName $parts[$i] -NotePropertyValue ([pscustomobject]@{})
            }
            $node = $node.$($parts[$i])
        }
        $leaf = $parts[-1]
        if ($node.PSObject.Properties[$leaf]) {
            $node.$leaf = $Updates[$key]
        } else {
            Add-Member -InputObject $node -NotePropertyName $leaf -NotePropertyValue $Updates[$key]
        }
    }
    ($raw | ConvertTo-Json -Depth 30) | Set-Content -LiteralPath $ParamsFile -Encoding utf8
    Write-Ok "Updated params.json: $($Updates.Keys -join ', ')"
}

function Ensure-LocalDir {
    if (-not (Test-Path $LocalDir)) { New-Item -ItemType Directory -Path $LocalDir | Out-Null }
}

function Hydrate-Outputs {
    if ($global:Outputs) { return }
    $p = Get-Params
    Write-Info 'Hydrating bicep outputs from latest deployment…'
    $latest = (Invoke-Native az 'deployment' 'group' 'list' '-g' $p.resourceGroupName `
        '--query' "[?contains(name,'spocs-')] | sort_by(@, &properties.timestamp) | [-1]" `
        '-o' 'json' -Quiet) | ConvertFrom-AzJson
    if (-not $latest) { throw "No 'spocs-*' deployment found in $($p.resourceGroupName). Run deploy.ps1 -Phase Infra first." }
    $global:Outputs = @{}
    foreach ($k in $latest.properties.outputs.PSObject.Properties.Name) {
        $global:Outputs[$k] = $latest.properties.outputs.$k.value
    }
    Write-Ok "Loaded outputs from deployment $($latest.name)."
}

# ========================================================
# Phase: Prereqs
# ========================================================

function Invoke-Phase-Prereqs {
    Write-Step 'Prereqs: tooling for SharePoint / SPFx setup'
    Assert-PowerShellVersion -Minimum '7.2'
    Assert-Tool -Name 'Azure CLI' -Command 'az' -VersionArg 'version' -MinVersion '2.55'
    Assert-Tool -Name 'Node.js'   -Command 'node' -VersionArg '--version'

    # Node 22 is required for SPFx 1.22 — warn if mismatched (don't block; user may
    # be using an nvm-like shim that auto-switches per directory).
    $nodeVer = (& node --version).Trim().TrimStart('v')
    $majorNode = [int]($nodeVer -split '\.')[0]
    if ($majorNode -ne 22) {
        Write-Warn2 "Node $nodeVer detected; SPFx 1.22 requires Node 22.x. The Spfx phase may fail. Consider 'nvm use 22'."
    } else {
        Write-Ok "Node $nodeVer (SPFx 1.22-compatible)."
    }

    # PnP.PowerShell — auto-install in CurrentUser scope if missing
    if (-not (Get-Module -ListAvailable -Name PnP.PowerShell)) {
        Write-Info 'Installing PnP.PowerShell (CurrentUser scope, one-time)…'
        Install-Module -Name PnP.PowerShell -Scope CurrentUser -Force -AllowClobber -ErrorAction Stop | Out-Null
    }
    Import-Module PnP.PowerShell -ErrorAction Stop -DisableNameChecking
    $pnp = (Get-Module PnP.PowerShell).Version
    Write-Ok "PnP.PowerShell $pnp loaded."
}

# ========================================================
# Phase: AadApp
# ========================================================

function Invoke-AzRestPatch {
    <#
    .SYNOPSIS Sends a JSON-body PATCH via `az rest`, using @file to avoid cmd.exe parsing
              issues with braces/colons/semicolons and forcing array fields via [string[]] casts.
    #>
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][object]$Body
    )
    $tmp = New-TemporaryFile
    try {
        ($Body | ConvertTo-Json -Depth 10 -Compress) | Set-Content -LiteralPath $tmp -Encoding utf8
        Invoke-Native az 'rest' '--method' 'PATCH' `
            '--uri' $Uri `
            '--headers' 'Content-Type=application/json' `
            '--body' "@$tmp" '-o' 'none' -Quiet | Out-Null
    } finally {
        Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue
    }
}

function Invoke-Phase-AadApp {
    Write-Step 'AadApp: create/update AAD app registration + permissions'
    $p = Get-Params

    $displayName = if ($p.PSObject.Properties['aadApp'] -and $p.aadApp.PSObject.Properties['displayName']) {
        $p.aadApp.displayName
    } else { 'spo-coldstorage' }

    # SPA redirect URI is derived from the planned Web App name in params, NOT from a bicep
    # output. This lets AadApp run BEFORE the Azure-side Infra phase exists. If the resource
    # group has been deployed, we also accept the bicep-output hostname (handles renames).
    $webAppHost = "$($p.naming.webApp).azurewebsites.net"
    if ($global:Outputs -and $global:Outputs['webAppHostname']) {
        $webAppHost = $global:Outputs['webAppHostname']
    }
    $replyUri   = "https://$webAppHost"
    $extraUris  = if ($p.PSObject.Properties['aadApp'] -and $p.aadApp.PSObject.Properties['additionalRedirectUris']) {
        @($p.aadApp.additionalRedirectUris)
    } else { @() }
    # Force string[] so ConvertTo-Json keeps it as an array even when only 1 entry.
    [string[]]$allSpaRedirects = @(@($replyUri) + $extraUris | Sort-Object -Unique)

    # Look up by clientId if provided in params, else by displayName
    $clientId = $p.azureAd.clientId
    $existing = $null
    if ($clientId -and $clientId -ne '00000000-0000-0000-0000-000000000000') {
        $existing = (Invoke-Native az 'ad' 'app' 'list' '--app-id' $clientId '-o' 'json' -Quiet) | ConvertFrom-AzJson
        if ($existing) { $existing = $existing | Select-Object -First 1 }
    }
    if (-not $existing) {
        $found = (Invoke-Native az 'ad' 'app' 'list' '--display-name' $displayName '-o' 'json' -Quiet) | ConvertFrom-AzJson
        if ($found -and $found.Count -gt 0) { $existing = $found[0] }
    }

    if ($existing) {
        Write-Ok "Found AAD app '$($existing.displayName)' (appId=$($existing.appId))."
        $clientId = $existing.appId
        $appObjectId = $existing.id
    } else {
        Write-Info "Creating AAD app '$displayName'…"
        $created = (Invoke-Native az 'ad' 'app' 'create' `
            '--display-name' $displayName `
            '--sign-in-audience' 'AzureADMyOrg' `
            '-o' 'json' -Quiet) | ConvertFrom-AzJson
        $clientId    = $created.appId
        $appObjectId = $created.id
        Write-Ok "Created AAD app (appId=$clientId)."
    }

    # Ensure service principal exists
    $sp = (Invoke-Native az 'ad' 'sp' 'list' '--filter' "appId eq '$clientId'" '-o' 'json' -Quiet) | ConvertFrom-AzJson
    if (-not $sp -or $sp.Count -eq 0) {
        Write-Info 'Creating service principal for the app…'
        $sp = @((Invoke-Native az 'ad' 'sp' 'create' '--id' $clientId '-o' 'json' -Quiet) | ConvertFrom-AzJson)
    }
    $spObjectId = $sp[0].id
    Write-Ok "Service principal objectId = $spObjectId"

    # Identifier URI = api://<clientId> (idempotent)
    $identifierUri = "api://$clientId"
    Invoke-Native az 'ad' 'app' 'update' '--id' $clientId `
        '--identifier-uris' $identifierUri -Quiet | Out-Null
    Write-Ok "Set Application ID URI = $identifierUri"

    # SPA redirect URIs — set via spa.redirectUris (NO direct az flag; use Graph PATCH).
    Invoke-AzRestPatch -Uri "https://graph.microsoft.com/v1.0/applications/$appObjectId" -Body @{
        spa = @{ redirectUris = $allSpaRedirects }
    }
    Write-Ok "SPA redirect URIs: $($allSpaRedirects -join ', ')"

    # Expose API scope `access_as_user` (idempotent: only add if missing)
    $current = (Invoke-Native az 'ad' 'app' 'show' '--id' $clientId '-o' 'json' -Quiet) | ConvertFrom-AzJson
    $hasScope = $current.api.oauth2PermissionScopes | Where-Object { $_.value -eq 'access_as_user' }
    if (-not $hasScope) {
        Write-Info "Adding API scope 'access_as_user'…"
        $scopeId = [Guid]::NewGuid().ToString()
        Invoke-AzRestPatch -Uri "https://graph.microsoft.com/v1.0/applications/$appObjectId" -Body @{
            api = @{
                oauth2PermissionScopes = @(
                    @{
                        id                      = $scopeId
                        adminConsentDescription = 'Allow the app to call the SPO Cold Storage Web API on behalf of the signed-in user.'
                        adminConsentDisplayName = 'Call SPO Cold Storage API'
                        userConsentDescription  = 'Allow this app to call the SPO Cold Storage Web API as you.'
                        userConsentDisplayName  = 'Call SPO Cold Storage API'
                        value                   = 'access_as_user'
                        type                    = 'User'
                        isEnabled               = $true
                    }
                )
                requestedAccessTokenVersion = 2
            }
        }
        Write-Ok "Added API scope: $identifierUri/access_as_user"
    } else {
        Write-Ok 'API scope access_as_user already present.'
    }

    # API permissions (idempotent: az ad app permission add no-ops if present)
    $graphAppId       = '00000003-0000-0000-c000-000000000000'
    $sharepointAppId  = '00000003-0000-0ff1-ce00-000000000000'
    $graphUserReadId  = 'e1fe6dd8-ba31-4d61-89e7-88639da4683d=Scope'         # delegated
    $spoFullCtrlAppId = '678536fe-1083-478a-9c59-b99265e6b0d3=Role'          # application

    Write-Info 'Adding API permissions (Graph User.Read + SharePoint Sites.FullControl.All)…'
    Invoke-Native az 'ad' 'app' 'permission' 'add' `
        '--id' $clientId '--api' $graphAppId '--api-permissions' $graphUserReadId `
        '-o' 'none' -Quiet -AllowNonZero | Out-Null
    Invoke-Native az 'ad' 'app' 'permission' 'add' `
        '--id' $clientId '--api' $sharepointAppId '--api-permissions' $spoFullCtrlAppId `
        '-o' 'none' -Quiet -AllowNonZero | Out-Null

    Write-Info 'Requesting admin consent (requires Global/Privileged Role Admin)…'
    try {
        Invoke-Native az 'ad' 'app' 'permission' 'admin-consent' '--id' $clientId `
            '-o' 'none' -Quiet | Out-Null
        Write-Ok 'Admin consent granted.'
    } catch {
        Write-Warn2 "admin-consent failed — you may need to grant consent in the portal: $($_.Exception.Message)"
    }

    # Write back to params.json
    Update-ParamsFile @{
        'azureAd.clientId'                  = $clientId
        'azureAd.tenantId'                  = $p.subscription.tenantId
        'azureAd.servicePrincipalObjectId'  = $spObjectId
    }

    # Generate a client secret if the app has none, and persist it to deploy/.local/
    # so the Azure-side Secrets phase can pick it up automatically. This avoids the
    # otherwise easy-to-miss manual step "now go create a secret in the portal".
    Ensure-LocalDir
    $secretFile = Join-Path $LocalDir 'aad-client-secret.txt'
    $existingCreds = (Invoke-Native az 'ad' 'app' 'credential' 'list' `
        '--id' $clientId '-o' 'json' -Quiet -AllowNonZero) | ConvertFrom-AzJson
    $hasPasswordCred = $existingCreds | Where-Object { -not $_.PSObject.Properties['type'] -or $_.type -ne 'AsymmetricX509Cert' }
    if (-not $hasPasswordCred -and -not (Test-Path $secretFile)) {
        Write-Info "Creating a 2-year client secret for the app (no existing password credential found)…"
        $cred = (Invoke-Native az 'ad' 'app' 'credential' 'reset' `
            '--id' $clientId `
            '--display-name' 'deploy-spo-auto' `
            '--years' '2' `
            '--append' `
            '-o' 'json' -Quiet) | ConvertFrom-AzJson
        Set-Content -LiteralPath $secretFile -Value $cred.password -NoNewline -Encoding ascii
        Write-Ok "Client secret written to $secretFile (gitignored). deploy.ps1 -Phase Secrets will read this automatically."
    } elseif (Test-Path $secretFile) {
        Write-Ok "Existing client secret found at $secretFile — reusing."
    } else {
        Write-Warn2 "App has existing client secrets but $secretFile is missing. deploy.ps1 -Phase Secrets will need -AzureAdClientSecret or `$env:SPOCS_AAD_CLIENT_SECRET."
    }
}

# ========================================================
# Phase: Cert
# ========================================================

function Get-PfxPassword {
    if ($PfxPassword) { return $PfxPassword }
    if ($env:SPOCS_PFX_PASSWORD) {
        return (ConvertTo-SecureString -String $env:SPOCS_PFX_PASSWORD -AsPlainText -Force)
    }
    return (Read-Host 'Enter PFX password (input hidden)' -AsSecureString)
}

function Invoke-Phase-Cert {
    Write-Step 'Cert: provision certificate for SharePoint app-only auth'
    $p = Get-Params
    Hydrate-Outputs
    Ensure-LocalDir

    $kvName   = $global:Outputs['keyVaultName']
    $certName = $p.azureAd.certificateName
    if (-not $certName) { throw 'azureAd.certificateName is empty in params.json.' }
    $appObjectId = (Invoke-Native az 'ad' 'app' 'list' '--app-id' $p.azureAd.clientId `
        '--query' '[0].id' '-o' 'tsv' -Quiet).Trim()
    if (-not $appObjectId) { throw "AAD app with clientId $($p.azureAd.clientId) not found. Run -Phase AadApp first." }

    $certConfig = if ($p.PSObject.Properties['certificate']) { $p.certificate } else { [pscustomobject]@{ source='generate'; subject="CN=$certName"; validityYears=2 } }
    $source = if ($certConfig.PSObject.Properties['source']) { $certConfig.source } else { 'generate' }

    $publicCer = Join-Path $LocalDir "$certName.cer"

    switch ($source) {
        'generate' {
            $subject       = if ($certConfig.PSObject.Properties['subject']) { $certConfig.subject } else { "CN=$certName" }
            $validityYears = if ($certConfig.PSObject.Properties['validityYears']) { [int]$certConfig.validityYears } else { 2 }
            $pfxPath       = Join-Path $LocalDir "$certName.pfx"
            $password      = Get-PfxPassword

            Write-Info "Generating self-signed cert $subject (valid $validityYears year(s))…"
            $cert = New-SelfSignedCertificate `
                -Subject $subject `
                -CertStoreLocation 'Cert:\CurrentUser\My' `
                -KeyExportPolicy Exportable `
                -KeySpec Signature `
                -KeyLength 2048 `
                -KeyAlgorithm RSA `
                -HashAlgorithm SHA256 `
                -NotAfter (Get-Date).AddYears($validityYears)
            Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $password | Out-Null
            Export-Certificate    -Cert $cert -FilePath $publicCer -Type CERT | Out-Null
            Remove-Item ("Cert:\CurrentUser\My\$($cert.Thumbprint)") -Force
            Write-Ok "PFX saved to $pfxPath; public key to $publicCer"

            Grant-KvDeployerRole -ResourceGroup $p.resourceGroupName -KeyVaultName $kvName -Role CertificatesOfficer -SubscriptionId $p.subscription.id
            Upload-CertToKeyVault -KeyVault $kvName -CertName $certName -PfxPath $pfxPath -Password $password
        }
        'file' {
            $pfxPath = $certConfig.pfxPath
            if (-not $pfxPath -or -not (Test-Path $pfxPath)) {
                throw "certificate.source='file' but certificate.pfxPath is empty or missing: '$pfxPath'"
            }
            $password = Get-PfxPassword
            Write-Info "Using user-supplied PFX: $pfxPath"
            # Export public key for upload to AAD app
            $tmpStore = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $pfxPath,
                [System.Net.NetworkCredential]::new('', $password).Password,
                [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
            )
            [System.IO.File]::WriteAllBytes($publicCer, $tmpStore.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
            $tmpStore.Dispose()
            Grant-KvDeployerRole -ResourceGroup $p.resourceGroupName -KeyVaultName $kvName -Role CertificatesOfficer -SubscriptionId $p.subscription.id
            Upload-CertToKeyVault -KeyVault $kvName -CertName $certName -PfxPath $pfxPath -Password $password
        }
        'keyvault' {
            Write-Info "Source=keyvault: assuming cert '$certName' already exists in $kvName."
            Grant-KvDeployerRole -ResourceGroup $p.resourceGroupName -KeyVaultName $kvName -Role CertificatesOfficer -SubscriptionId $p.subscription.id
            $kvCert = (Invoke-Native az 'keyvault' 'certificate' 'show' '--vault-name' $kvName `
                '--name' $certName '-o' 'json' -Quiet -AllowNonZero) | ConvertFrom-AzJson
            if (-not $kvCert) { throw "Cert '$certName' not found in Key Vault '$kvName'." }
            Write-Ok "Cert present in KV (thumbprint $($kvCert.x509ThumbprintHex))."
            # Get public key bytes for AAD upload
            [System.IO.File]::WriteAllBytes($publicCer, [Convert]::FromBase64String($kvCert.cer))
        }
        default { throw "Unknown certificate.source '$source'. Use generate|file|keyvault." }
    }

    # Attach public key to AAD app manifest (keyCredentials)
    Write-Info "Attaching cert public key to AAD app $($p.azureAd.clientId)…"
    $b64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($publicCer))
    $cert509 = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($publicCer)
    $startIso = $cert509.NotBefore.ToUniversalTime().ToString('o')
    $endIso   = $cert509.NotAfter.ToUniversalTime().ToString('o')

    Invoke-AzRestPatch -Uri "https://graph.microsoft.com/v1.0/applications/$appObjectId" -Body @{
        keyCredentials = @(
            @{
                type        = 'AsymmetricX509Cert'
                usage       = 'Verify'
                key         = $b64
                displayName = $certName
                startDateTime = $startIso
                endDateTime   = $endIso
            }
        )
    }
    Write-Ok "Public key attached to AAD app (thumbprint $($cert509.Thumbprint))."
}

function Upload-CertToKeyVault {
    param(
        [Parameter(Mandatory)][string]$KeyVault,
        [Parameter(Mandatory)][string]$CertName,
        [Parameter(Mandatory)][string]$PfxPath,
        [Parameter(Mandatory)][SecureString]$Password
    )
    $plain = [System.Net.NetworkCredential]::new('', $Password).Password
    Write-Info "Uploading $CertName to Key Vault $KeyVault…"
    try {
        # Retry on Forbidden — RBAC propagation for cert import can lag behind grant.
        $maxAttempts = 24
        for ($i = 1; $i -le $maxAttempts; $i++) {
            try {
                Invoke-Native az 'keyvault' 'certificate' 'import' `
                    '--vault-name' $KeyVault `
                    '--name' $CertName `
                    '--file' $PfxPath `
                    '--password' $plain `
                    '-o' 'none' -Quiet | Out-Null
                Write-Ok "Cert uploaded to KV (attempt $i)."
                return
            } catch {
                $msg = $_.Exception.Message
                if ($i -eq $maxAttempts -or $msg -notmatch 'Forbidden|ForbiddenByRbac') { throw }
                Write-Info "RBAC for cert import not yet propagated (attempt $i/$maxAttempts); waiting 10s…"
                Start-Sleep -Seconds 10
            }
        }
    } finally {
        $plain = $null
    }
}

# ========================================================
# Phase: SpaConfig
# ========================================================

function Invoke-Phase-SpaConfig {
    Write-Step 'SpaConfig: write src/Web/web.client/.env.production'
    $p = Get-Params
    Hydrate-Outputs
    if (-not (Test-Path $SpaDir)) { throw "SPA dir not found: $SpaDir" }

    $clientId = $p.azureAd.clientId
    $tenantId = $p.azureAd.tenantId
    if (-not $clientId -or $clientId -eq '00000000-0000-0000-0000-000000000000') {
        throw 'azureAd.clientId is not set. Run -Phase AadApp first.'
    }
    $webHost = $global:Outputs['webAppHostname']

    $envPath = Join-Path $SpaDir '.env.production'
    $content = @"
# Auto-generated by deploy/deploy-spo.ps1 (Phase SpaConfig). Do not edit by hand.
VITE_MSAL_CLIENT_ID=$clientId
VITE_MSAL_AUTHORITY=https://login.microsoftonline.com/$tenantId
VITE_MSAL_SCOPES=api://$clientId/access_as_user
VITE_MSAL_STORAGE_SCOPES=https://storage.azure.com/user_impersonation
VITE_TEAMSFX_START_LOGIN_PAGE_URL=https://$webHost/auth-start.html
"@
    Set-Content -LiteralPath $envPath -Value $content -Encoding utf8
    Write-Ok "Wrote $envPath"
    Write-Info 'Rebuild + redeploy the web app to pick this up:  ./deploy/deploy.ps1 -Phase App -SkipConfirm'
}

# ========================================================
# Phase: Spfx (build)
# ========================================================

function Invoke-Phase-Spfx {
    Write-Step 'Spfx: build .sppkg package'
    if (-not (Test-Path $SpfxDir)) { throw "SPFx dir not found: $SpfxDir" }
    $p = Get-Params

    # Render elements.xml from elements.xml.template, substituting deployment-specific
    # values for the ListView CommandSet's ClientSideComponentProperties.
    $elementsTemplate = Join-Path $SpfxDir 'sharepoint/assets/elements.xml.template'
    $elementsOut      = Join-Path $SpfxDir 'sharepoint/assets/elements.xml'
    if (Test-Path $elementsTemplate) {
        $webHost = if ($global:Outputs -and $global:Outputs['webAppHostname']) {
            $global:Outputs['webAppHostname']
        } else {
            "$($p.naming.webApp).azurewebsites.net"
        }
        $apiBaseUrl = "https://$webHost"
        # IMPORTANT: aadHttpClientFactory.getClient(resource) expects the AAD app's
        # *identifierUri* (e.g. `api://<clientId>`) - NOT a scope URL like
        # `api://<clientId>/access_as_user`. Passing the scope makes AAD reject the
        # token request with AADSTS500011 / invalid_resource because there is no
        # service principal whose identifierUris match that string.
        $apiAppIdUri = "api://$($p.azureAd.clientId)"
        if (-not $p.azureAd.clientId -or $p.azureAd.clientId -eq '00000000-0000-0000-0000-000000000000') {
            throw 'azureAd.clientId is not set in params.json. Run -Phase AadApp first.'
        }
        $rendered = (Get-Content -LiteralPath $elementsTemplate -Raw) `
            -replace '\{\{APP_BASE_URL\}\}', $apiBaseUrl `
            -replace '\{\{APP_ID_URI\}\}',   $apiAppIdUri
        Set-Content -LiteralPath $elementsOut -Value $rendered -Encoding utf8
        Write-Ok "Rendered elements.xml: apiBaseUrl=$apiBaseUrl, apiAppIdUri=$apiAppIdUri"
    } else {
        Write-Warn2 "elements.xml.template not found; using elements.xml as-is."
    }

    Push-Location $SpfxDir
    try {
        if (-not (Test-Path 'node_modules')) {
            Write-Info 'npm install (cold install — this takes a few minutes)…'
            try {
                Invoke-Native npm 'install' '--no-audit' '--no-fund' | Out-Null
            } catch {
                throw @"
npm install failed in $SpfxDir.

This usually means the SPFx project pins package versions that no longer exist on the
public npm registry. The project targets @microsoft/sp-* 1.22.2 (Node 22). Make sure
package.json in src/SPFx/spfx-cold-storage/ references a published SPFx version before
re-running this phase.

Original error:
$($_.Exception.Message)
"@
            }
        } else {
            Write-Info 'node_modules present; skipping npm install.'
        }
        # Clean stale build output first. Without this, dist/ accumulates bundles from
        # previous builds and gulp package-solution ships ALL of them — leaving multiple
        # hashed copies of each component in the .sppkg. That corrupts the tenant's
        # component-manifest registration ("Cannot destructure property 'id' of undefined"
        # at load) so the extension silently fails to render.
        Write-Info 'gulp clean (remove stale build artifacts)…'
        Invoke-Native npx 'gulp' 'clean' | Out-Null
        Write-Info 'gulp bundle --ship…'
        try {
            Invoke-Native npx 'gulp' 'bundle' '--ship' | Out-Null
        } catch {
            throw @"
gulp bundle failed in $SpfxDir.

If the error mentions:
  * 'customActions' or '{guid}' schema validation — strip the curly braces around
    the GUID keys in config/serve.json (SPFx schema requires bare GUIDs).
  * '@microsoft/rush-stack-compiler-X.Y' not found — the SPFx version installed does
    not match the toolchain expected by gulp-core-build-typescript. Align the
    @microsoft/sp-build-web version in package.json with the rush-stack-compiler
    version available, or upgrade the whole SPFx project to a current version.

Original error:
$($_.Exception.Message)
"@
        }
        Write-Info 'gulp package-solution --ship…'
        Invoke-Native npx 'gulp' 'package-solution' '--ship' | Out-Null
    } finally { Pop-Location }

    $sppkg = Get-ChildItem -LiteralPath (Join-Path $SpfxDir 'sharepoint/solution') -Filter '*.sppkg' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $sppkg) { throw 'No .sppkg produced — check gulp output.' }
    Write-Ok "Built $($sppkg.Name) ($([math]::Round($sppkg.Length/1KB,1)) KB)"
}

# ========================================================
# Phase: SpfxDeploy
# ========================================================

function Invoke-Phase-SpfxDeploy {
    Write-Step 'SpfxDeploy: upload .sppkg to App Catalog + install on target site'
    $p = Get-Params

    if (-not $p.PSObject.Properties['sharePoint']) { throw 'params.sharePoint section missing.' }
    $appCatalogUrl = if ($p.sharePoint.PSObject.Properties['appCatalogUrl']) { $p.sharePoint.appCatalogUrl } else { $null }
    if (-not $appCatalogUrl) { throw "params.sharePoint.appCatalogUrl is required for SpfxDeploy. Example: 'https://contoso.sharepoint.com/sites/apps'" }
    $targetSiteRel = if ($p.sharePoint.PSObject.Properties['targetSiteRelativeUrl']) { $p.sharePoint.targetSiteRelativeUrl } else { $null }
    $targetSiteUrl = if ($targetSiteRel) { ($p.sharePoint.baseServerAddress.TrimEnd('/')) + $targetSiteRel } else { $null }

    $sppkg = Get-ChildItem -LiteralPath (Join-Path $SpfxDir 'sharepoint/solution') -Filter '*.sppkg' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $sppkg) { throw 'No .sppkg found. Run -Phase Spfx first.' }
    Write-Info "Using package: $($sppkg.FullName)"

    Import-Module PnP.PowerShell -ErrorAction Stop -DisableNameChecking

    # Pick an auth method based on $SpfxAuthMode.
    # - Interactive    : opens a browser; default. Works for humans on a workstation.
    # - DeviceLogin    : prints a device code to the console; works headless / over SSH.
    # - Certificate    : app-only auth using the PFX from deploy/.local/<certName>.pfx
    #                    (created by the Cert phase). Requires Sites.FullControl.All
    #                    application permission on the SP API + admin consent — both
    #                    granted by the AadApp phase.
    function Connect-Spo { param([string]$Url)
        Write-Info "Connecting PnP to $Url via $SpfxAuthMode…"
        # Wrap the Connect call in a retry for cert auth — AAD's confidential-client cert
        # propagation can lag by 30-90s after Cert phase attaches the public key, surfacing
        # as AADSTS700027 "certificate not registered on application".
        $maxAttempts = if ($SpfxAuthMode -eq 'Certificate') { 12 } else { 1 }
        for ($i = 1; $i -le $maxAttempts; $i++) {
            try {
                switch ($SpfxAuthMode) {
                    'Interactive' {
                        Connect-PnPOnline -Url $Url -Interactive -ClientId $p.azureAd.clientId -ErrorAction Stop
                    }
                    'DeviceLogin' {
                        Connect-PnPOnline -Url $Url -DeviceLogin -ClientId $p.azureAd.clientId -ErrorAction Stop
                    }
                    'Certificate' {
                        $certPath = Join-Path $LocalDir "$($p.azureAd.certificateName).pfx"
                        if (-not (Test-Path $certPath)) {
                            # Auto-download from the deployment Key Vault. The KV "secret"
                            # twin of a certificate carries the full PFX (PKCS#12) base64-
                            # encoded with no password — perfect for headless re-runs on a
                            # fresh machine that wasn't the one that originally ran -Phase Cert.
                            $kvName = $null
                            if ($global:Outputs -and $global:Outputs['keyVaultName']) {
                                $kvName = $global:Outputs['keyVaultName']
                            } elseif ($p.naming.PSObject.Properties['keyVault']) {
                                $kvName = $p.naming.keyVault
                            }
                            if ($kvName) {
                                Write-Info "PFX not found at $certPath — downloading $($p.azureAd.certificateName) from $kvName…"
                                $b64 = (Invoke-Native az 'keyvault' 'secret' 'show' '--vault-name' $kvName '--name' $p.azureAd.certificateName '--query' 'value' '-o' 'tsv' -Quiet -AllowNonZero) -join ''
                                if ($b64) {
                                    New-Item -ItemType Directory -Force (Split-Path $certPath) | Out-Null
                                    [IO.File]::WriteAllBytes($certPath, [Convert]::FromBase64String($b64.Trim()))
                                    Write-Ok "Downloaded PFX: $certPath ($((Get-Item $certPath).Length) bytes)"
                                }
                            }
                            if (-not (Test-Path $certPath)) {
                                throw "Certificate auth selected but $certPath does not exist and could not be downloaded from Key Vault. Run -Phase Cert with source=generate, or place your PFX there manually."
                            }
                        }
                        # KV-downloaded PFXs have no password; only call Get-PfxPassword
                        # (which prompts) if the file actually needs one.
                        $needsPw = $false
                        try {
                            $null = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certPath, '', [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
                        } catch { $needsPw = $true }
                        if ($needsPw) {
                            $pfxPw = Get-PfxPassword
                            Connect-PnPOnline -Url $Url `
                                -ClientId $p.azureAd.clientId `
                                -Tenant   $p.azureAd.tenantId `
                                -CertificatePath $certPath `
                                -CertificatePassword $pfxPw `
                                -ErrorAction Stop
                        } else {
                            Connect-PnPOnline -Url $Url `
                                -ClientId $p.azureAd.clientId `
                                -Tenant   $p.azureAd.tenantId `
                                -CertificatePath $certPath `
                                -ErrorAction Stop
                        }
                    }
                    default { throw "Unknown -SpfxAuthMode '$SpfxAuthMode'. Use Interactive|DeviceLogin|Certificate." }
                }
                return
            } catch {
                $msg = $_.Exception.Message
                if ($i -lt $maxAttempts -and $msg -match 'AADSTS700027|certificate.*not registered') {
                    Write-Info "AAD cert not yet propagated (attempt $i/$maxAttempts); waiting 15s…"
                    Start-Sleep -Seconds 15
                    continue
                }
                throw
            }
        }
    }

    Connect-Spo -Url $appCatalogUrl

    Write-Info 'Uploading + publishing app (Overwrite + Publish + Force)…'
    # NOTE: Intentionally NO -SkipFeatureDeployment — we want feature activation per-site
    # so that Install-PnPApp deploys the elements.xml CustomActions to the site's web.
    # SharePoint's tenant-wide extensions path (skipFeatureDeployment=true) requires a
    # different XML schema and isn't reliable for ListView Command Sets.
    $app = Add-PnPApp -Path $sppkg.FullName -Scope Tenant -Overwrite -Publish -Force
    Write-Ok "App published: id=$($app.Id) title=$($app.Title) version=$($app.AppCatalogVersion)"

    if ($targetSiteUrl) {
        Write-Info "Installing app on $targetSiteUrl…"
        Disconnect-PnPOnline -ErrorAction SilentlyContinue
        Connect-Spo -Url $targetSiteUrl
        try {
            Install-PnPApp -Identity $app.Id -ErrorAction Stop
            Write-Ok "Installed on $targetSiteUrl"
        } catch {
            if ($_.Exception.Message -match 'already installed|InstallInProgress|already exists') {
                Write-Info 'App already installed on site — upgrading to latest catalog version…'
                try {
                    Update-PnPApp -Identity $app.Id -ErrorAction Stop
                    Write-Ok "Upgraded to latest version on $targetSiteUrl"
                } catch {
                    if ($_.Exception.Message -match 'no.+update|already.+latest|nothing to update') {
                        Write-Ok 'Site already on the latest catalog version.'
                    } else { throw }
                }
            } else { throw }
        }
    } else {
        Write-Warn2 'sharePoint.targetSiteRelativeUrl not set; skipping site-level install.'
    }

    Disconnect-PnPOnline -ErrorAction SilentlyContinue
}

# ========================================================
# Main
# ========================================================

function Run-Phase { param([string]$Name)
    switch ($Name) {
        'Prereqs'    { Invoke-Phase-Prereqs }
        'AadApp'     { Invoke-Phase-AadApp }
        'Cert'       { Invoke-Phase-Cert }
        'SpaConfig'  { Invoke-Phase-SpaConfig }
        'Spfx'       { Invoke-Phase-Spfx }
        'SpfxDeploy' { Invoke-Phase-SpfxDeploy }
        default      { throw "Unknown phase: $Name" }
    }
}

try {
    $phases = if ($Phase -eq 'All') {
        @('Prereqs','AadApp','Cert','SpaConfig','Spfx','SpfxDeploy')
    } else {
        if ($Phase -ne 'Prereqs') {
            $null = Get-Params
            Assert-AzLogin -ExpectedSubscriptionId $global:Params.subscription.id -ExpectedTenantId $global:Params.subscription.tenantId
        }
        @($Phase)
    }
    if ($phases -contains 'AadApp' -and -not $SkipConfirm) {
        $ans = Read-Host "About to create/update an AAD app registration and request admin consent in tenant $($global:Params.subscription.tenantId). Continue? (yes/no)"
        if ($ans -notmatch '^(y|yes)$') { throw 'Aborted by user.' }
    }
    foreach ($ph in $phases) { Run-Phase -Name $ph }

    $elapsed = (Get-Date) - $global:DeployStart
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor Green
    Write-Host ("SPO setup complete in {0:N0}m {1:N0}s." -f $elapsed.TotalMinutes, $elapsed.Seconds) -ForegroundColor Green
    Write-Host ('=' * 78) -ForegroundColor Green
}
catch {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor Red
    Write-Host "SPO SETUP FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ('=' * 78) -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkRed
    exit 1
}
