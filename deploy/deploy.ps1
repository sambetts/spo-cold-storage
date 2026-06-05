<#
.SYNOPSIS
    Deploys the SPO Cold Storage solution to Azure (single Windows App Service hosting
    Web.Server + WebJobs).

.DESCRIPTION
    Phases (run with -Phase, default 'All'):
      Prereqs   Validate local tools, az login, subscription, providers.
      Validate  Parse + strictly validate deploy/params.json.
      Infra     Run bicep what-if then deploy main.bicep.
      Secrets   Push the AAD client secret into Key Vault (prompted/-AzureAdClientSecret/env var).
      Sql       Grant the Web App MSI db_owner on the SQL database (T-SQL via Entra token).
      App       dotnet publish Web.Server + 3 workers, assemble zip with App_Data/jobs layout,
                deploy to the Web App, set app settings (Key Vault references), restart.
      Smoke     Verify the Web App responds, container/log-stream OK, secrets resolve.

.PARAMETER ParamsFile
    Path to params.json. Defaults to deploy/params.json (gitignored), resolved relative to this script.

.PARAMETER Phase
    Which phase(s) to run. Default 'All'.

.PARAMETER AzureAdClientSecret
    The AAD app registration client secret as SecureString. If not supplied, falls back to
    the SPOCS_AAD_CLIENT_SECRET environment variable; if that is also empty, prompted.
    Only required for the Secrets phase. Ignored otherwise.

.PARAMETER SkipConfirm
    Skip the deployment summary confirmation prompt.

.PARAMETER WhatIfPreview
    For Infra phase: run `az deployment group what-if` and exit without deploying.

.EXAMPLE
    ./deploy/deploy.ps1 -Phase All

.EXAMPLE
    ./deploy/deploy.ps1 -Phase Infra -WhatIfPreview

.EXAMPLE
    ./deploy/deploy.ps1 -Phase App -SkipConfirm

.NOTES
    Requirements: PowerShell 7.2+, Azure CLI 2.55+, .NET SDK 10+, SqlServer PS module
    (auto-installed in CurrentUser scope on first Sql phase run).
#>
[CmdletBinding()]
param(
    [string]$ParamsFile = (Join-Path $PSScriptRoot 'params.json'),

    [ValidateSet('All','Prereqs','Validate','Infra','Secrets','Sql','App','Smoke')]
    [string]$Phase = 'All',

    [SecureString]$AzureAdClientSecret,

    [switch]$SkipConfirm,

    [switch]$WhatIfPreview
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

# Silence az CLI WARNING lines (e.g. "A new Bicep release is available…") that otherwise
# contaminate stdout when we 2>&1 it for capture+parse.
$env:AZURE_CORE_ONLY_SHOW_ERRORS = 'true'

# --- Path constants ---
# This script lives at <repo>/deploy/deploy.ps1.
$DeployRoot     = $PSScriptRoot
$RepoRoot       = Split-Path -Parent $DeployRoot
$ScriptsRoot    = Join-Path $DeployRoot 'scripts'
$BicepRoot      = Join-Path $DeployRoot 'bicep'
$BicepMain      = Join-Path $BicepRoot  'main.bicep'
$SrcRoot        = Join-Path $RepoRoot   'src'
$WebProject     = Join-Path $SrcRoot    'Web/Web.Server/Web.Server.csproj'
$WorkerProjects = @(
    @{ Name = 'Migration.Migrator';            Csproj = 'Migration.Migrator/Migration.Migrator.csproj';                       Kind = 'continuous'; Singleton = $true  }
    @{ Name = 'Migration.Indexer';             Csproj = 'Migration.Indexer/Migration.Indexer.csproj';                         Kind = 'triggered';  Singleton = $false }
    @{ Name = 'Migration.SiteSnapshotBuilder'; Csproj = 'Migration.SiteSnapshotBuilder/Migration.SiteSnapshotBuilder.csproj'; Kind = 'triggered';  Singleton = $false }
)
$Providers = @(
    'Microsoft.Web', 'Microsoft.Storage', 'Microsoft.KeyVault',
    'Microsoft.ServiceBus', 'Microsoft.Sql', 'Microsoft.Search',
    'Microsoft.Insights', 'Microsoft.OperationalInsights',
    'Microsoft.Authorization', 'Microsoft.Network'
)

# --- Load helpers ---
. (Join-Path $ScriptsRoot '_common.ps1')

$global:DeployStart = Get-Date
$global:Outputs     = $null   # bicep outputs
$global:Params      = $null

# ========================================================
# Phase implementations
# ========================================================

function Invoke-Phase-Prereqs {
    Write-Step 'Prereqs: tooling, az login, providers'
    Assert-PowerShellVersion -Minimum '7.2'
    Assert-Tool -Name 'Azure CLI'  -Command 'az'     -VersionArg 'version' -MinVersion '2.55'
    Assert-Tool -Name '.NET SDK'   -Command 'dotnet' -VersionArg '--version' -MinVersion '10.0'
    Assert-Tool -Name 'Git'        -Command 'git'    -VersionArg '--version'
    # Bicep CLI is bundled with az; just verify it can be invoked.
    Invoke-Native az 'bicep' 'version' -Quiet | Out-Null
    Write-Ok 'Bicep CLI available via az.'
}

function Invoke-Phase-Validate {
    Write-Step "Validate: parse + check params from $ParamsFile"
    $global:Params = Read-Params -Path $ParamsFile
    Write-Ok 'Params validated.'

    Assert-AzLogin     -ExpectedSubscriptionId $global:Params.subscription.id `
                       -ExpectedTenantId       $global:Params.subscription.tenantId
    Assert-AzLocation  -Location               $global:Params.location
    Register-AzProviders -Providers $Providers

    # Global-uniqueness preflight (best effort).
    Write-Info 'Checking global name availability…'
    $checks = @(
        @{ Type='storage';    Name=$global:Params.naming.storageAccount },
        @{ Type='webApp';     Name=$global:Params.naming.webApp },
        @{ Type='keyVault';   Name=$global:Params.naming.keyVault },
        @{ Type='serviceBus'; Name=$global:Params.naming.serviceBus },
        @{ Type='search';     Name=$global:Params.naming.search },
        @{ Type='sqlServer';  Name=$global:Params.naming.sqlServer }
    )
    foreach ($c in $checks) {
        $ok = Test-AzNameAvailable -Type $c.Type -Name $c.Name -ResourceGroup $global:Params.resourceGroupName
        if (-not $ok) {
            throw "Resource name '$($c.Name)' (type $($c.Type)) is not globally available. Choose a unique name in params.naming."
        }
        Write-Ok ("Name available: {0,-12} {1}" -f $c.Type, $c.Name)
    }

    Show-DeploymentPlan
}

function Show-DeploymentPlan {
    $p = $global:Params
    Write-Host ''
    Write-Host 'Deployment plan' -ForegroundColor White
    Write-Host '---------------'
    Write-Host "  Subscription : $($p.subscription.id)"
    Write-Host "  Tenant       : $($p.subscription.tenantId)"
    Write-Host "  Location     : $($p.location)"
    Write-Host "  Resource grp : $($p.resourceGroupName)"
    Write-Host '  Resources    :'
    foreach ($k in $p.naming.PSObject.Properties.Name) {
        Write-Host ("    {0,-18} {1}" -f $k, $p.naming.$k)
    }
    Write-Host "  Web URL      : https://$($p.naming.webApp).azurewebsites.net"
    Write-Host ''
    if (-not $SkipConfirm) {
        $ans = Read-Host 'Proceed? (yes/no)'
        if ($ans -notmatch '^(y|yes)$') { throw 'Aborted by user.' }
    }
}

function Get-OrPromptAadSecret {
    if ($AzureAdClientSecret) { return $AzureAdClientSecret }
    if ($env:SPOCS_AAD_CLIENT_SECRET) {
        Write-Info 'Using AAD client secret from $env:SPOCS_AAD_CLIENT_SECRET.'
        return (ConvertTo-SecureString -String $env:SPOCS_AAD_CLIENT_SECRET -AsPlainText -Force)
    }
    # Fallback: deploy-spo.ps1 -Phase AadApp writes the secret it generates to this file.
    $localSecretFile = Join-Path $DeployRoot '.local/aad-client-secret.txt'
    if (Test-Path $localSecretFile) {
        Write-Info "Using AAD client secret from $localSecretFile (generated by deploy-spo.ps1 -Phase AadApp)."
        $raw = (Get-Content -LiteralPath $localSecretFile -Raw).Trim()
        if ($raw) { return (ConvertTo-SecureString -String $raw -AsPlainText -Force) }
    }
    Write-Info 'AAD client secret not provided; prompting (input hidden).'
    $s = Read-Host 'Enter the Azure AD application client secret' -AsSecureString
    if (-not $s -or $s.Length -eq 0) { throw 'AAD client secret is required for the Secrets phase.' }
    return $s
}

function Invoke-Phase-Infra {
    Write-Step 'Infra: ensure RG, bicep what-if, deploy'
    $p = $global:Params

    # Resource group (idempotent)
    $rgExists = (Invoke-Native az 'group' 'exists' '-n' $p.resourceGroupName -Quiet).Trim() -eq 'true'
    if (-not $rgExists) {
        Write-Info "Creating resource group $($p.resourceGroupName) in $($p.location)…"
        Invoke-Native az 'group' 'create' '-n' $p.resourceGroupName '-l' $p.location `
            '--tags' (Convert-TagsToCliArgs $p) -Quiet | Out-Null
    } else {
        Write-Ok "Resource group $($p.resourceGroupName) already exists."
    }

    $deployIp = Get-PublicIp
    Write-Info "Detected public IP for SQL firewall: $deployIp"

    # Build bicep parameters file in temp
    $bicepParams = @{
        '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'
        parameters = @{
            location               = @{ value = $p.location }
            tags                   = @{ value = (Get-Tags $p) }
            naming                 = @{ value = (ConvertTo-PlainObject $p.naming) }
            sku                    = @{ value = (ConvertTo-PlainObject $p.sku) }
            azureAd                = @{ value = (ConvertTo-PlainObject $p.azureAd) }
            sql                    = @{ value = (ConvertTo-PlainObject $p.sql) }
            sharePoint             = @{ value = (ConvertTo-PlainObject $p.sharePoint) }
            deployClientIpAddress  = @{ value = $deployIp }
            storageUserDataReaderPrincipals = @{ value = @(Get-StorageUserReaderOids $p) }
            storageUserDataReaderTypes      = @{ value = @(Get-StorageUserReaderTypes $p) }
            # aadClientSecret intentionally omitted here; pushed in Secrets phase.
        }
    }
    $tmpParams = New-TemporaryFile
    try {
        ($bicepParams | ConvertTo-Json -Depth 20) | Set-Content -LiteralPath $tmpParams -Encoding utf8
        Write-Info "Bicep parameters written to $tmpParams"

        # Build (validate syntax) first
        Invoke-Native az 'bicep' 'build' '--file' $BicepMain '--stdout' -Quiet | Out-Null
        Write-Ok 'Bicep template syntax valid.'

        # What-if
        Write-Info 'Running deployment what-if (preview of changes)…'
        Invoke-Native az 'deployment' 'group' 'what-if' `
            '-g' $p.resourceGroupName `
            '--template-file' $BicepMain `
            '--parameters' "@$tmpParams" `
            '--result-format' 'ResourceIdOnly' | Out-Host

        if ($WhatIfPreview) { Write-Ok 'WhatIfPreview specified; stopping after what-if.'; return }

        if (-not $SkipConfirm) {
            $ans = Read-Host 'Apply this deployment? (yes/no)'
            if ($ans -notmatch '^(y|yes)$') { throw 'Aborted by user before bicep deploy.' }
        }

        $name = "spocs-$((Get-Date).ToString('yyyyMMddHHmmss'))"
        Write-Info "Deploying bicep (name=$name)…"
        $deploy = (Invoke-Native az 'deployment' 'group' 'create' `
            '-g' $p.resourceGroupName `
            '-n' $name `
            '--template-file' $BicepMain `
            '--parameters' "@$tmpParams" `
            '-o' 'json') | ConvertFrom-AzJson
        $global:Outputs = @{}
        foreach ($k in $deploy.properties.outputs.PSObject.Properties.Name) {
            $global:Outputs[$k] = $deploy.properties.outputs.$k.value
        }
        Write-Ok 'Bicep deployment complete.'
        Show-Outputs
    } finally {
        Remove-Item -LiteralPath $tmpParams -ErrorAction SilentlyContinue
    }
}

function Show-Outputs {
    if (-not $global:Outputs) { return }
    Write-Host ''
    Write-Host 'Deployment outputs:' -ForegroundColor White
    foreach ($k in ($global:Outputs.Keys | Sort-Object)) {
        $v = [string]$global:Outputs[$k]
        if ($v.Length -gt 100) { $v = $v.Substring(0,97) + '…' }
        Write-Host ("  {0,-32} {1}" -f $k, $v)
    }
}

function Get-Tags { param($p) if ($p.PSObject.Properties['tags']) { return (ConvertTo-PlainObject $p.tags) } else { return @{} } }

function Convert-TagsToCliArgs { param($p) (Get-Tags $p).GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" } }

function Get-StorageUserReaderOids {
    param($p)
    if (-not $p.PSObject.Properties['storage']) { return @() }
    if (-not $p.storage.PSObject.Properties['userDataReaders']) { return @() }
    return @($p.storage.userDataReaders | ForEach-Object {
        if ($_ -is [pscustomobject]) { $_.objectId } else { [string]$_ }
    } | Where-Object { $_ })
}

function Get-StorageUserReaderTypes {
    param($p)
    if (-not $p.PSObject.Properties['storage']) { return @() }
    if (-not $p.storage.PSObject.Properties['userDataReaders']) { return @() }
    return @($p.storage.userDataReaders | ForEach-Object {
        if ($_ -is [pscustomobject] -and $_.PSObject.Properties['type']) { $_.type } else { 'User' }
    })
}

function ConvertTo-PlainObject {
    param($obj)
    if ($null -eq $obj) { return $null }
    if ($obj -is [pscustomobject]) {
        $h = [ordered]@{}
        foreach ($p in $obj.PSObject.Properties) {
            if ($p.Name.StartsWith('_')) { continue }   # drop _comment-style keys
            $h[$p.Name] = ConvertTo-PlainObject $p.Value
        }
        return $h
    }
    if ($obj -is [System.Collections.IDictionary]) {
        $h = [ordered]@{}
        foreach ($k in $obj.Keys) {
            if ([string]$k -like '_*') { continue }
            $h[$k] = ConvertTo-PlainObject $obj[$k]
        }
        return $h
    }
    if ($obj -is [System.Collections.IEnumerable] -and -not ($obj -is [string])) {
        return @($obj | ForEach-Object { ConvertTo-PlainObject $_ })
    }
    return $obj
}

function Ensure-OutputsLoaded {
    # If we're running a later phase standalone, hydrate outputs from the deployed RG.
    if ($global:Outputs) { return }
    $p = $global:Params
    Write-Info 'Hydrating outputs from latest deployment in resource group…'
    $latest = (Invoke-Native az 'deployment' 'group' 'list' '-g' $p.resourceGroupName `
        '--query' "[?contains(name,'spocs-')] | sort_by(@, &properties.timestamp) | [-1]" `
        '-o' 'json' -Quiet) | ConvertFrom-AzJson
    if (-not $latest) { throw "No 'spocs-*' deployment found in $($p.resourceGroupName). Run -Phase Infra first." }
    $global:Outputs = @{}
    foreach ($k in $latest.properties.outputs.PSObject.Properties.Name) {
        $global:Outputs[$k] = $latest.properties.outputs.$k.value
    }
    Write-Ok "Loaded outputs from deployment $($latest.name)."
}

function Invoke-Phase-Secrets {
    Write-Step 'Secrets: push AAD client secret into Key Vault'
    Ensure-OutputsLoaded
    $kv = $global:Outputs['keyVaultName']

    # Self-grant Key Vault Secrets Officer on this vault so the deploying user can push secrets.
    # (RBAC-mode KVs don't give the vault creator any default rights.)
    $me = (Invoke-Native az 'ad' 'signed-in-user' 'show' '--query' 'id' '-o' 'tsv' -Quiet).Trim()
    if (-not $me) { throw 'Could not resolve current signed-in user object ID.' }
    $kvScope = "/subscriptions/$($global:Params.subscription.id)/resourceGroups/$($global:Params.resourceGroupName)/providers/Microsoft.KeyVault/vaults/$kv"
    $secretsOfficer = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

    $existing = (Invoke-Native az 'role' 'assignment' 'list' `
        '--assignee' $me `
        '--scope' $kvScope `
        '--role' $secretsOfficer `
        '--query' '[].id' '-o' 'tsv' -Quiet -AllowNonZero) -join ''
    if (-not $existing) {
        Write-Info "Granting Key Vault Secrets Officer to deployer ($me) on $kv…"
        Invoke-Native az 'role' 'assignment' 'create' `
            '--assignee-object-id' $me `
            '--assignee-principal-type' 'User' `
            '--role' $secretsOfficer `
            '--scope' $kvScope `
            '-o' 'none' -Quiet | Out-Null
        Write-Info 'Waiting for RBAC propagation (up to 90s)…'
        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 5
            $probe = (Invoke-Native az 'keyvault' 'secret' 'list' '--vault-name' $kv `
                '--maxresults' '1' '-o' 'tsv' -Quiet -AllowNonZero) -join ''
            if ($LASTEXITCODE -eq 0) { Write-Ok 'RBAC propagated.'; break }
        }
    } else {
        Write-Ok 'Deployer already has Secrets Officer on this vault.'
    }

    $sec = Get-OrPromptAadSecret
    $plain = [System.Net.NetworkCredential]::new('', $sec).Password
    if (-not $plain) { throw 'AAD client secret resolved to empty.' }
    try {
        # Retry on Forbidden: RBAC propagation for set can lag behind list/read.
        $maxAttempts = 24
        for ($i = 1; $i -le $maxAttempts; $i++) {
            $stderr = $null
            try {
                Invoke-Native az 'keyvault' 'secret' 'set' `
                    '--vault-name' $kv `
                    '--name' 'aad-client-secret' `
                    '--value' $plain `
                    '-o' 'none' -Quiet | Out-Null
                Write-Ok "Secret 'aad-client-secret' written to Key Vault $kv (attempt $i)."
                break
            } catch {
                $stderr = $_.Exception.Message
                if ($i -eq $maxAttempts -or $stderr -notmatch 'Forbidden|ForbiddenByRbac') { throw }
                Write-Info "RBAC for setSecret not yet propagated (attempt $i/$maxAttempts); waiting 10s…"
                Start-Sleep -Seconds 10
            }
        }
    } finally {
        $plain = $null
    }
}

function Invoke-Phase-Sql {
    Write-Step 'Sql: grant Web App MSI db_owner via Entra-token T-SQL'
    Ensure-OutputsLoaded
    $p = $global:Params
    $serverFqdn  = $global:Outputs['sqlServerFqdn']
    $dbName      = $global:Outputs['sqlDatabaseName']
    $sqlServer   = $global:Outputs['sqlServerName']
    $webAppName  = $global:Outputs['webAppName']

    # IMPORTANT: Azure SQL derives the SID for an MSI/Service Principal from the SP's
    # appId, NOT from the principal (object) id. The simplest correct way is
    # `CREATE USER … FROM EXTERNAL PROVIDER`, which looks up the SP by display name
    # and uses the right SID automatically. The Web App MSI's SP is named after the
    # Web App itself, so this resolves unambiguously.
    $userName = $webAppName
    $sqlScript = @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$userName')
BEGIN
    CREATE USER [$userName] FROM EXTERNAL PROVIDER;
END
IF NOT EXISTS (
    SELECT 1 FROM sys.database_role_members rm
    JOIN sys.database_principals r ON rm.role_principal_id = r.principal_id
    JOIN sys.database_principals m ON rm.member_principal_id = m.principal_id
    WHERE r.name = N'db_owner' AND m.name = N'$userName'
)
BEGIN
    ALTER ROLE db_owner ADD MEMBER [$userName];
END
SELECT name, type_desc, CONVERT(VARCHAR(256), sid, 1) AS sid_hex
FROM sys.database_principals WHERE name = N'$userName';
"@

    # Ensure deploy IP is in the firewall (bicep already did this, but guard against drift).
    $deployIp = Get-PublicIp
    Write-Info "Ensuring SQL firewall rule for deploy IP $deployIp…"
    Invoke-Native az 'sql' 'server' 'firewall-rule' 'create' `
        '-g' $p.resourceGroupName '-s' $sqlServer `
        '-n' 'AllowDeployClientIp' `
        '--start-ip-address' $deployIp '--end-ip-address' $deployIp `
        '-o' 'none' -Quiet -AllowNonZero | Out-Null

    Write-Info "Connecting to $serverFqdn / $dbName as Entra admin…"
    try {
        Invoke-AzureSqlCommand -ServerFqdn $serverFqdn -Database $dbName -Sql $sqlScript | Format-Table | Out-String | Write-Host
        Write-Ok "Granted db_owner to MSI '$userName' (SID derived from appId via FROM EXTERNAL PROVIDER)."
    } catch {
        throw "SQL grant failed: $($_.Exception.Message)`nEnsure the deploying user is a member of the Entra SQL admin group/user '$($p.sql.entraAdminLogin)'."
    } finally {
        if ($p.PSObject.Properties['network'] -and $p.network.PSObject.Properties['removeDeployIpAfterSqlGrants'] -and $p.network.removeDeployIpAfterSqlGrants) {
            Write-Info 'Removing deploy IP from SQL firewall…'
            Invoke-Native az 'sql' 'server' 'firewall-rule' 'delete' `
                '-g' $p.resourceGroupName '-s' $sqlServer `
                '-n' 'AllowDeployClientIp' `
                '-o' 'none' -Quiet -AllowNonZero | Out-Null
        }
    }
}

# Kept for reference; not used anymore — SQL SIDs for MSIs use appId, not principalId.
# function ConvertTo-SqlSidFromGuid { ... }

function Invoke-Phase-App {
    Write-Step 'App: publish + package + deploy to App Service'
    Ensure-OutputsLoaded
    $p = $global:Params
    $publishRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("spocs-publish-" + [Guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Path $publishRoot | Out-Null
    Write-Info "Publishing into $publishRoot"

    try {
        # Build SPA first (the Web.Server csproj relies on the SpaProxy/esproj at publish time).
        $spaDir = Join-Path $SrcRoot 'Web/web.client'
        if (Test-Path $spaDir) {
            Write-Info 'Building SPA (npm ci && npm run build)…'
            Push-Location $spaDir
            try {
                if (-not (Test-Path 'node_modules')) {
                    Invoke-Native npm 'ci' '--no-audit' '--no-fund' | Out-Null
                } else {
                    Write-Info 'node_modules present; skipping npm ci.'
                }
                Invoke-Native npm 'run' 'build' | Out-Null
            } finally { Pop-Location }
        }

        # Publish Web.Server self-contained (win-x64). Self-contained ships the .NET 10
        # runtime in the publish output, so it runs regardless of which version the
        # App Service netFrameworkVersion knob is set to.
        Write-Info 'dotnet publish Web.Server (Release, win-x64, self-contained)…'
        $webPublish = Join-Path $publishRoot 'web'
        Invoke-Native dotnet 'publish' $WebProject `
            '-c' 'Release' `
            '-r' 'win-x64' `
            '--self-contained' 'true' `
            '-p:PublishReadyToRun=false' `
            '-o' $webPublish | Out-Null

        # Ensure App_Data/jobs hierarchy
        $jobsRoot = Join-Path $webPublish 'App_Data/jobs'
        $contRoot = Join-Path $jobsRoot   'continuous'
        $trigRoot = Join-Path $jobsRoot   'triggered'
        New-Item -ItemType Directory -Force -Path $contRoot, $trigRoot | Out-Null

        foreach ($w in $WorkerProjects) {
            $kindRoot = if ($w.Kind -eq 'continuous') { $contRoot } else { $trigRoot }
            $dest = Join-Path $kindRoot $w.Name
            $csprojPath = Join-Path $SrcRoot $w.Csproj
            if (-not (Test-Path $csprojPath)) { throw "Worker csproj not found: $csprojPath" }
            Write-Info "dotnet publish $($w.Name) → $dest"
            Invoke-Native dotnet 'publish' $csprojPath `
                '-c' 'Release' `
                '-r' 'win-x64' `
                '--self-contained' 'true' `
                '-p:PublishReadyToRun=false' `
                '-o' $dest | Out-Null

            # run.cmd — what the WebJobs SDK invokes
            $exeName = "$($w.Name).exe"
            $runCmd  = "@echo off`r`ncd /d %~dp0`r`n$exeName`r`n"
            Set-Content -LiteralPath (Join-Path $dest 'run.cmd') -Value $runCmd -NoNewline -Encoding ascii

            # settings.job
            $settings = @{}
            if ($w.Singleton) { $settings.is_singleton = $true }
            ($settings | ConvertTo-Json -Compress) | Set-Content -LiteralPath (Join-Path $dest 'settings.job') -Encoding ascii
        }

        # Zip the whole thing
        $zip = Join-Path ([System.IO.Path]::GetTempPath()) ("spocs-deploy-" + (Get-Date).ToString('yyyyMMddHHmmss') + '.zip')
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Write-Info "Zipping → $zip"
        # Compress-Archive is slow for large publishes; use [System.IO.Compression.ZipFile] for speed.
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($webPublish, $zip, 'Optimal', $false)
        Write-Ok "Zip created ($([math]::Round((Get-Item $zip).Length / 1MB,1)) MB)"

        # App settings (idempotent set)
        Set-AppSettings -ResourceGroup $p.resourceGroupName -WebAppName $global:Outputs['webAppName']

        # Zip-deploy
        Write-Info 'Deploying zip to App Service…'
        Invoke-Native az 'webapp' 'deploy' `
            '--resource-group' $p.resourceGroupName `
            '--name' $global:Outputs['webAppName'] `
            '--src-path' $zip `
            '--type' 'zip' `
            '--async' 'false' `
            '-o' 'none' | Out-Null
        Write-Ok 'Zip deployed.'

        # Restart to make sure WebJobs pick up the new binaries
        Invoke-Native az 'webapp' 'restart' `
            '-g' $p.resourceGroupName '-n' $global:Outputs['webAppName'] '-o' 'none' -Quiet | Out-Null
        Write-Ok 'Web app restarted.'

        Remove-Item -LiteralPath $zip -ErrorAction SilentlyContinue
    } finally {
        Remove-Item -LiteralPath $publishRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Set-AppSettings {
    param(
        [Parameter(Mandatory)][string]$ResourceGroup,
        [Parameter(Mandatory)][string]$WebAppName
    )
    Write-Info 'Configuring App Service application settings (Key Vault references)…'

    $o = $global:Outputs
    $kvName = $o['keyVaultName']
    $kvSecretUri = { param($name) "@Microsoft.KeyVault(SecretUri=https://$kvName.vault.azure.net/secrets/$name/)" }

    $sqlConn = "Server=tcp:$($o['sqlServerFqdn']),1433;Initial Catalog=$($o['sqlDatabaseName']);Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;Authentication=Active Directory Managed Identity;"

    # Double-underscore convention works on both Windows and Linux.
    $settings = [ordered]@{
        'WEBSITE_LOAD_USER_PROFILE'             = '1'
        'WEBSITE_RUN_FROM_PACKAGE'              = '0'
        # Route DNS lookups through the Azure-provided resolver inside the VNet so that
        # *.database.windows.net, *.blob.core.windows.net, *.vaultcore.azure.net, etc.
        # resolve to the private endpoint IPs (via the linked privatelink. zones) rather
        # than the public IPs. Required because vnetRouteAllEnabled is left false (so
        # internet-bound traffic to AAD / SharePoint Online / App Insights doesn't
        # need a NAT Gateway).
        'WEBSITE_DNS_SERVER'                    = '168.63.129.16'
        'ASPNETCORE_ENVIRONMENT'                = 'Production'
        'BaseServerAddress'                     = $o['baseServerAddress']
        'KeyVaultUrl'                           = $o['keyVaultUri'].TrimEnd('/')
        'BlobContainerName'                     = $o['blobContainerName']
        'AppInsightsInstrumentationKey'         = $o['appInsightsConnectionString']
        'APPLICATIONINSIGHTS_CONNECTION_STRING' = $o['appInsightsConnectionString']
        'AzureAd__ClientID'                     = $o['aadClientId']
        'AzureAd__TenantId'                     = $o['aadTenantId']
        'AzureAd__CertificateName'              = $o['aadCertificateName']
        'AzureAd__AuthenticationMode'           = 'Certificate'
        'AzureAd__Secret'                       = (& $kvSecretUri 'aad-client-secret')
        'ConnectionStrings__SQLConnectionString' = $sqlConn
        'ConnectionStrings__Storage'            = (& $kvSecretUri 'storage-connection-string')
        'ConnectionStrings__ServiceBus'         = (& $kvSecretUri 'servicebus-connection-string')
        'Search__ServiceName'                   = $o['searchServiceName']
        'Search__IndexName'                     = $global:Params.naming.searchIndex
        'Search__QueryKey'                      = (& $kvSecretUri 'search-query-key')
        'Search__AdminKey'                      = (& $kvSecretUri 'search-admin-key')
    }

    # Write to a temp JSON file and use az's @file syntax — avoids cmd.exe parsing semicolons
    # and parens in values (App Insights conn strings + KV references both have them).
    $tmp = New-TemporaryFile
    try {
        # az expects an array of {name,value,slotSetting} objects when using --settings @file.json.
        $payload = @($settings.GetEnumerator() | ForEach-Object {
            [ordered]@{ name = $_.Key; value = [string]$_.Value; slotSetting = $false }
        })
        ($payload | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $tmp -Encoding utf8
        Invoke-Native az 'webapp' 'config' 'appsettings' 'set' `
            '-g' $ResourceGroup '-n' $WebAppName `
            '--settings' "@$tmp" `
            '-o' 'none' -Quiet | Out-Null
        Write-Ok ("Set {0} app settings." -f $settings.Count)
    } finally {
        Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue
    }
}

function Invoke-Phase-Smoke {
    Write-Step 'Smoke: basic post-deploy checks'
    Ensure-OutputsLoaded
    $p = $global:Params
    $url = "https://$($global:Outputs['webAppHostname'])"
    Write-Info "Probing $url (expect 401/302 — the app is auth-protected)…"
    try {
        $resp = Invoke-WebRequest -Uri $url -MaximumRedirection 0 -UseBasicParsing -SkipHttpErrorCheck -TimeoutSec 60
        Write-Ok "Web app responded HTTP $($resp.StatusCode)."
        if ($resp.StatusCode -ge 500) {
            Write-Warn2 "5xx response — check App Service logs: az webapp log tail -g $($p.resourceGroupName) -n $($global:Outputs['webAppName'])"
        }
    } catch {
        Write-Warn2 "Probe failed: $($_.Exception.Message)"
    }

    Write-Info 'Listing WebJobs…'
    Invoke-Native az 'webapp' 'webjob' 'continuous' 'list' `
        '-g' $p.resourceGroupName '-n' $global:Outputs['webAppName'] '-o' 'table' -AllowNonZero | Out-Host
    Invoke-Native az 'webapp' 'webjob' 'triggered' 'list' `
        '-g' $p.resourceGroupName '-n' $global:Outputs['webAppName'] '-o' 'table' -AllowNonZero | Out-Host

    Write-Info 'Verifying Key Vault references resolved…'
    $refs = (Invoke-Native az 'webapp' 'config' 'appsettings' 'list' `
        '-g' $p.resourceGroupName '-n' $global:Outputs['webAppName'] `
        '-o' 'json' -Quiet) | ConvertFrom-AzJson
    $kvRefs = $refs | Where-Object { $_.value -like '@Microsoft.KeyVault*' }
    Write-Info "$($kvRefs.Count) Key Vault references configured."
    # The deeper check (resolution status) lives under sourceControls/configreferences; surface a hint:
    Write-Info "Inspect resolution: az webapp config appsettings list -g $($p.resourceGroupName) -n $($global:Outputs['webAppName']) (then portal → Configuration → Key Vault references status)."
}

# ========================================================
# Main orchestration
# ========================================================

function Run-Phase {
    param([string]$Name)
    switch ($Name) {
        'Prereqs'  { Invoke-Phase-Prereqs }
        'Validate' { Invoke-Phase-Validate }
        'Infra'    { Invoke-Phase-Infra }
        'Secrets'  { Invoke-Phase-Secrets }
        'Sql'      { Invoke-Phase-Sql }
        'App'      { Invoke-Phase-App }
        'Smoke'    { Invoke-Phase-Smoke }
        default    { throw "Unknown phase: $Name" }
    }
}

try {
    $phases = if ($Phase -eq 'All') {
        @('Prereqs','Validate','Infra','Secrets','Sql','App','Smoke')
    } else {
        # For standalone phases, ensure params are at least loaded for context (except phases
        # that do this themselves, or Prereqs which is pre-params).
        if ($Phase -notin 'Prereqs','Validate') {
            $global:Params = Read-Params -Path $ParamsFile
            Assert-AzLogin -ExpectedSubscriptionId $global:Params.subscription.id -ExpectedTenantId $global:Params.subscription.tenantId
        }
        @($Phase)
    }
    foreach ($ph in $phases) { Run-Phase -Name $ph }

    $elapsed = (Get-Date) - $global:DeployStart
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor Green
    Write-Host ("Deployment complete in {0:N0}m {1:N0}s." -f $elapsed.TotalMinutes, $elapsed.Seconds) -ForegroundColor Green
    Write-Host ('=' * 78) -ForegroundColor Green
    if ($global:Outputs -and $global:Outputs.ContainsKey('webAppHostname')) {
        Write-Host "App URL: https://$($global:Outputs['webAppHostname'])" -ForegroundColor Green
    }
}
catch {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor Red
    Write-Host "DEPLOYMENT FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ('=' * 78) -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkRed
    exit 1
}
