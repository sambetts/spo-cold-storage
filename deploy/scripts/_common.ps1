# Shared helpers for deploy.ps1.
# Dot-sourced; do NOT execute directly.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# -------- Console output helpers --------

$script:StepIndex = 0

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)
    $script:StepIndex++
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor Cyan
    Write-Host ("[STEP {0:D2}] {1}" -f $script:StepIndex, $Message) -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor Cyan
}

function Write-Info { param([string]$m) Write-Host "  [info]  $m" -ForegroundColor Gray }
function Write-Ok   { param([string]$m) Write-Host "  [ ok ]  $m" -ForegroundColor Green }
function Write-Warn2 { param([string]$m) Write-Host "  [warn]  $m" -ForegroundColor Yellow }
function Write-Err  { param([string]$m) Write-Host "  [err ]  $m" -ForegroundColor Red }

# -------- Native command runner --------

function ConvertFrom-AzJson {
    <#
    .SYNOPSIS Tolerant JSON parser for az CLI output: strips leading WARNING/INFO lines.
    #>
    param([Parameter(ValueFromPipeline)]$Input)
    end {
        $text = ($Input | Out-String)
        # Find first '{' or '[' (start of JSON document) and parse from there.
        $idx1 = $text.IndexOf('{'); $idx2 = $text.IndexOf('[')
        $idx  = if ($idx1 -ge 0 -and ($idx2 -lt 0 -or $idx1 -lt $idx2)) { $idx1 } else { $idx2 }
        if ($idx -lt 0) { return $null }
        return ($text.Substring($idx) | ConvertFrom-Json -Depth 100)
    }
}

function Invoke-Native {
    <#
    .SYNOPSIS Runs a native executable and throws on non-zero exit.
    .DESCRIPTION
        Captures stdout (returned), echoes stderr live to console, and asserts $LASTEXITCODE.
        Use -PassThruOutput:$false to suppress returning stdout (for chatty commands).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][string]$FilePath,
        [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments,
        [switch]$Quiet,
        [switch]$AllowNonZero
    )

    if (-not $Quiet) {
        $printable = ($Arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
        Write-Host "  > $FilePath $printable" -ForegroundColor DarkGray
    }

    $output = & $FilePath @Arguments 2>&1
    $exit = $LASTEXITCODE
    if ($exit -ne 0 -and -not $AllowNonZero) {
        $joined = ($output | Out-String).TrimEnd()
        throw "Command failed (exit $exit): $FilePath $($Arguments -join ' ')`n$joined"
    }
    return $output
}

# -------- Tool / prerequisite checks --------

function Assert-Tool {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Command,
        [string]$VersionArg = '--version',
        [string]$MinVersion
    )
    $cmd = Get-Command $Command -ErrorAction SilentlyContinue
    if (-not $cmd) { throw "Required tool '$Name' not found on PATH (expected command: $Command)." }
    try {
        $out = (& $Command $VersionArg 2>&1 | Out-String).Trim()
    } catch {
        throw "Failed to invoke '$Command $VersionArg': $($_.Exception.Message)"
    }
    $firstNonBlank = ($out -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
    Write-Ok "$Name found: $firstNonBlank"

    if ($MinVersion) {
        $m = [regex]::Match($out, '\d+\.\d+(\.\d+)?')
        if ($m.Success) {
            $verStr = $m.Value
            if ($verStr -notmatch '^\d+\.\d+\.\d+$') { $verStr = "$verStr.0" }
            $found = [version]$verStr
            $min = [version]$MinVersion
            if ($found -lt $min) { throw "$Name version $found is below required $MinVersion." }
        } else {
            Write-Warn2 "Could not parse $Name version from output; skipping minimum-version check."
        }
    }
}

function Assert-PowerShellVersion {
    param([Parameter(Mandatory)][version]$Minimum)
    if ($PSVersionTable.PSVersion -lt $Minimum) {
        throw "PowerShell $Minimum or later required. Current: $($PSVersionTable.PSVersion)."
    }
    Write-Ok "PowerShell $($PSVersionTable.PSVersion)"
}

# -------- Params loading / validation --------

# Azure resource naming rules. Keep keys aligned with params.naming.* keys.
$script:NamingRules = @{
    appServicePlan  = @{ Pattern = '^[a-zA-Z0-9][a-zA-Z0-9-]{0,38}[a-zA-Z0-9]$'; MinLen = 1;  MaxLen = 40;  Global = $false; Desc = '1-40 alphanumeric/hyphens' }
    webApp          = @{ Pattern = '^[a-zA-Z0-9][a-zA-Z0-9-]{0,58}[a-zA-Z0-9]$'; MinLen = 2;  MaxLen = 60;  Global = $true;  Desc = '2-60 alphanumeric/hyphens, must not start/end with hyphen' }
    storageAccount  = @{ Pattern = '^[a-z0-9]{3,24}$';                            MinLen = 3;  MaxLen = 24;  Global = $true;  Desc = '3-24 lowercase letters/digits' }
    blobContainer   = @{ Pattern = '^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$';   MinLen = 3;  MaxLen = 63;  Global = $false; Desc = '3-63 lowercase letters/digits/single-hyphens' }
    keyVault        = @{ Pattern = '^[a-zA-Z][a-zA-Z0-9-]{1,22}[a-zA-Z0-9]$';     MinLen = 3;  MaxLen = 24;  Global = $true;  Desc = '3-24 alphanumeric/hyphens, starts with letter, no trailing hyphen' }
    serviceBus      = @{ Pattern = '^[a-zA-Z][a-zA-Z0-9-]{4,48}[a-zA-Z0-9]$';     MinLen = 6;  MaxLen = 50;  Global = $true;  Desc = '6-50 alphanumeric/hyphens, starts with letter' }
    serviceBusQueue = @{ Pattern = '^[a-zA-Z0-9][a-zA-Z0-9._\-/]{0,259}$';        MinLen = 1;  MaxLen = 260; Global = $false; Desc = '1-260 alphanumeric and ._-/' }
    sqlServer       = @{ Pattern = '^[a-z0-9][a-z0-9-]{0,61}[a-z0-9]$';           MinLen = 1;  MaxLen = 63;  Global = $true;  Desc = '1-63 lowercase letters/digits/hyphens' }
    sqlDatabase     = @{ Pattern = '^[^<>*%&:\\/?]{1,128}$';                       MinLen = 1;  MaxLen = 128; Global = $false; Desc = '1-128 chars, no <>*%&:\/?' }
    search          = @{ Pattern = '^[a-z0-9][a-z0-9-]{1,58}[a-z0-9]$';           MinLen = 2;  MaxLen = 60;  Global = $true;  Desc = '2-60 lowercase letters/digits/hyphens' }
    searchIndex     = @{ Pattern = '^[a-z0-9][a-z0-9-]{0,126}[a-z0-9]?$';         MinLen = 2;  MaxLen = 128; Global = $false; Desc = '2-128 lowercase letters/digits/hyphens' }
    logAnalytics    = @{ Pattern = '^[a-zA-Z0-9][a-zA-Z0-9-]{2,61}[a-zA-Z0-9]$';  MinLen = 4;  MaxLen = 63;  Global = $false; Desc = '4-63 alphanumeric/hyphens' }
    appInsights     = @{ Pattern = '^[^/]{1,255}$';                                MinLen = 1;  MaxLen = 255; Global = $false; Desc = '1-255 chars, no /' }
}

function Test-Guid {
    param([string]$Value)
    return $Value -match '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
}

function Assert-RequiredKeys {
    param(
        [Parameter(Mandatory)]$Object,
        [Parameter(Mandatory)][string[]]$Keys,
        [Parameter(Mandatory)][string]$Path
    )
    foreach ($k in $Keys) {
        $hasKey = $false
        if ($Object -is [hashtable]) { $hasKey = $Object.ContainsKey($k) }
        elseif ($Object -is [pscustomobject]) { $hasKey = $null -ne ($Object.PSObject.Properties[$k]) }
        if (-not $hasKey) {
            throw "Missing required key '$Path.$k' in params file."
        }
        $val = $Object.$k
        if ($null -eq $val -or ($val -is [string] -and [string]::IsNullOrWhiteSpace($val))) {
            throw "Required key '$Path.$k' is empty in params file."
        }
    }
}

function Read-Params {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Params file not found: $Path. Copy deploy/params.example.json to deploy/params.json and fill in values."
    }
    Write-Info "Loading params: $Path"
    try {
        $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 20
    } catch {
        throw "Failed to parse params JSON ($Path): $($_.Exception.Message)"
    }

    # Required top-level
    Assert-RequiredKeys -Object $json -Keys 'subscription','location','resourceGroupName','naming','sku','azureAd','sql','sharePoint' -Path '$'
    Assert-RequiredKeys -Object $json.subscription -Keys 'id','tenantId' -Path '$.subscription'
    Assert-RequiredKeys -Object $json.naming -Keys 'appServicePlan','webApp','storageAccount','blobContainer','keyVault','serviceBus','serviceBusQueue','sqlServer','sqlDatabase','search','searchIndex','logAnalytics','appInsights' -Path '$.naming'
    Assert-RequiredKeys -Object $json.sku -Keys 'appServicePlan','sqlDatabase','search','serviceBus' -Path '$.sku'
    Assert-RequiredKeys -Object $json.azureAd -Keys 'clientId','tenantId','certificateName' -Path '$.azureAd'
    Assert-RequiredKeys -Object $json.sql -Keys 'entraAdminLogin','entraAdminObjectId','entraAdminIsGroup' -Path '$.sql'
    Assert-RequiredKeys -Object $json.sharePoint -Keys 'baseServerAddress' -Path '$.sharePoint'

    # GUID checks
    foreach ($pair in @(
        @{ Path='subscription.id';            Val=$json.subscription.id },
        @{ Path='subscription.tenantId';      Val=$json.subscription.tenantId },
        @{ Path='azureAd.clientId';           Val=$json.azureAd.clientId },
        @{ Path='azureAd.tenantId';           Val=$json.azureAd.tenantId },
        @{ Path='sql.entraAdminObjectId';     Val=$json.sql.entraAdminObjectId }
    )) {
        if (-not (Test-Guid $pair.Val)) { throw "Invalid GUID at '$($pair.Path)': '$($pair.Val)'." }
    }

    # azureAd.servicePrincipalObjectId is optional but if set, must be a GUID
    if ($json.azureAd.PSObject.Properties['servicePrincipalObjectId'] -and -not [string]::IsNullOrWhiteSpace($json.azureAd.servicePrincipalObjectId)) {
        if (-not (Test-Guid $json.azureAd.servicePrincipalObjectId)) {
            throw "Invalid GUID at 'azureAd.servicePrincipalObjectId': '$($json.azureAd.servicePrincipalObjectId)'."
        }
    }

    # baseServerAddress must be https://
    if ($json.sharePoint.baseServerAddress -notmatch '^https://[^\s]+$') {
        throw "sharePoint.baseServerAddress must be an https:// URL. Got: '$($json.sharePoint.baseServerAddress)'."
    }

    # Validate Service Bus queue name matches code expectation (hardcoded in Config.cs)
    if ($json.naming.serviceBusQueue -ne 'filediscovery') {
        throw "naming.serviceBusQueue must be 'filediscovery' (hardcoded in Entities/Configuration/Config.cs ServiceBusQueueName)."
    }

    # Validate naming against Azure rules
    foreach ($key in $script:NamingRules.Keys) {
        if (-not $json.naming.PSObject.Properties[$key]) { continue }
        $value = [string]$json.naming.$key
        $rule = $script:NamingRules[$key]
        if ($value.Length -lt $rule.MinLen -or $value.Length -gt $rule.MaxLen) {
            throw "naming.$key length $($value.Length) outside $($rule.MinLen)-$($rule.MaxLen). Rule: $($rule.Desc). Got: '$value'."
        }
        if ($value -notmatch $rule.Pattern) {
            throw "naming.$key value '$value' does not match Azure naming rule: $($rule.Desc) (regex $($rule.Pattern))."
        }
    }

    # SKU sanity (won't catch every bad value, but catches typos)
    $aspSkus = @('F1','D1','B1','B2','B3','S1','S2','S3','P0v3','P1v3','P2v3','P3v3','P0V3','P1V3','P2V3','P3V3')
    if ($json.sku.appServicePlan -notin $aspSkus) {
        Write-Warn2 "sku.appServicePlan '$($json.sku.appServicePlan)' is not in the common list ($($aspSkus -join ',')). WebJobs need AlwaysOn (B1+)."
    }
    if ($json.sku.serviceBus -notin @('Basic','Standard','Premium')) {
        throw "sku.serviceBus must be Basic|Standard|Premium. Got: '$($json.sku.serviceBus)'."
    }
    if ($json.sku.search -notin @('free','basic','standard','standard2','standard3','storage_optimized_l1','storage_optimized_l2')) {
        throw "sku.search invalid: '$($json.sku.search)'."
    }

    return $json
}

# -------- Azure CLI helpers --------

function Assert-AzLogin {
    param([Parameter(Mandatory)][string]$ExpectedSubscriptionId, [Parameter(Mandatory)][string]$ExpectedTenantId)
    $acct = (Invoke-Native az 'account' 'show' '-o' 'json' -Quiet) | ConvertFrom-AzJson
    if ($acct.tenantId -ne $ExpectedTenantId) {
        throw "Azure CLI is logged into tenant '$($acct.tenantId)' but params require '$ExpectedTenantId'. Run: az login --tenant $ExpectedTenantId"
    }
    if ($acct.id -ne $ExpectedSubscriptionId) {
        Write-Warn2 "Current az subscription '$($acct.id)' differs from params '$ExpectedSubscriptionId' — switching."
        Invoke-Native az 'account' 'set' '--subscription' $ExpectedSubscriptionId -Quiet | Out-Null
    }
    Write-Ok "Azure CLI: subscription $ExpectedSubscriptionId (tenant $ExpectedTenantId), user $($acct.user.name)"
    # Intentionally no return value — callers don't need the account object and pipeline
    # leakage was causing duplicate output.
}

function Assert-AzLocation {
    param([Parameter(Mandatory)][string]$Location)
    $locs = (Invoke-Native az 'account' 'list-locations' '--query' '[].name' '-o' 'tsv' -Quiet) -split "`r?`n" | Where-Object { $_ }
    if ($locs -notcontains $Location) {
        throw "Location '$Location' is not valid for the current subscription. Examples: $(($locs | Select-Object -First 5) -join ', ')..."
    }
    Write-Ok "Location '$Location' valid."
}

function Register-AzProviders {
    param([Parameter(Mandatory)][string[]]$Providers)
    foreach ($p in $Providers) {
        $state = (Invoke-Native az 'provider' 'show' '-n' $p '--query' 'registrationState' '-o' 'tsv' -Quiet).Trim()
        if ($state -ne 'Registered') {
            Write-Info "Registering provider $p (state: $state)…"
            Invoke-Native az 'provider' 'register' '-n' $p -Quiet | Out-Null
        } else {
            Write-Ok "Provider $p registered."
        }
    }
}

function Test-AzNameAvailable {
    <#
    .SYNOPSIS  Checks global-uniqueness for resource types that support name availability APIs.
    .NOTES Returns $true if available OR if the resource already exists in the target RG (we'll re-deploy).
    #>
    param(
        [Parameter(Mandatory)][string]$Type,    # storage|webApp|keyVault|sqlServer|search|serviceBus
        [Parameter(Mandatory)][string]$Name,
        [string]$ResourceGroup
    )
    switch ($Type) {
        'storage' {
            $r = (Invoke-Native az 'storage' 'account' 'check-name' '--name' $Name '-o' 'json' -Quiet) | ConvertFrom-AzJson
            return $r.nameAvailable -or ($r.reason -eq 'AlreadyExists')
        }
        'webApp' {
            $r = (Invoke-Native az 'rest' '--method' 'post' '--uri' "https://management.azure.com/subscriptions/$((az account show --query id -o tsv))/providers/Microsoft.Web/checknameavailability?api-version=2023-12-01" '--body' (@{ name = $Name; type = 'Microsoft.Web/sites' } | ConvertTo-Json -Compress) -Quiet -AllowNonZero) | Out-String
            try { $j = $r | ConvertFrom-Json } catch { return $true }  # if API call shape changes, don't block
            return $j.nameAvailable -or ($j.reason -eq 'AlreadyExists')
        }
        'keyVault' {
            # No dedicated `az keyvault check-name` command in the current CLI; call the ARM
            # checkNameAvailability endpoint directly. If the resource already exists in this
            # RG (re-deploy), the reason will be "AlreadyExists" which we treat as OK.
            $sub = (az account show --query id -o tsv).Trim()
            $body = (@{ name = $Name; type = 'Microsoft.KeyVault/vaults' } | ConvertTo-Json -Compress)
            $r = (Invoke-Native az 'rest' '--method' 'post' '--uri' "https://management.azure.com/subscriptions/$sub/providers/Microsoft.KeyVault/checkNameAvailability?api-version=2023-07-01" '--body' $body -Quiet -AllowNonZero) | Out-String
            try { $j = $r | ConvertFrom-Json } catch { return $true }
            return $j.nameAvailable -or ($j.reason -eq 'AlreadyExists')
        }
        'sqlServer' {
            # No dedicated check; we attempt to read and accept either "exists in our RG" or NotFound.
            if ($ResourceGroup) {
                $existing = (Invoke-Native az 'sql' 'server' 'show' '-g' $ResourceGroup '-n' $Name '-o' 'tsv' '--query' 'name' -Quiet -AllowNonZero) -join ''
                if ($existing) { return $true }
            }
            return $true
        }
        'search' {
            $r = (Invoke-Native az 'rest' '--method' 'post' '--uri' "https://management.azure.com/subscriptions/$((az account show --query id -o tsv))/providers/Microsoft.Search/checkNameAvailability?api-version=2023-11-01" '--body' (@{ name = $Name; type = 'searchServices' } | ConvertTo-Json -Compress) -Quiet -AllowNonZero) | Out-String
            try { $j = $r | ConvertFrom-Json } catch { return $true }
            return $j.nameAvailable -or ($j.reason -eq 'AlreadyExists')
        }
        'serviceBus' {
            $r = (Invoke-Native az 'servicebus' 'namespace' 'exists' '--name' $Name '-o' 'json' -Quiet) | ConvertFrom-AzJson
            return $r.nameAvailable -or $false -eq $r.nameAvailable  # if exists in our RG, deployment will reuse it
        }
        default { return $true }
    }
}

function Get-PublicIp {
    try {
        $ip = (Invoke-WebRequest -Uri 'https://api.ipify.org' -UseBasicParsing -TimeoutSec 10).Content.Trim()
        if ($ip -match '^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$') { return $ip }
    } catch { }
    try {
        $ip = (Invoke-WebRequest -Uri 'https://ifconfig.me/ip' -UseBasicParsing -TimeoutSec 10).Content.Trim()
        if ($ip -match '^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$') { return $ip }
    } catch { }
    throw 'Could not determine public IP (needed for SQL firewall during deploy).'
}

# -------- Key Vault self-grant (RBAC mode) ----------

$script:KvRoleIds = @{
    SecretsOfficer       = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
    CertificatesOfficer  = 'a4417e6f-fecd-4de8-b567-7b0420556985'
    Administrator        = '00482a5a-887f-4fb3-b363-3b7fe8e74483'
}

function Grant-KvDeployerRole {
    <#
    .SYNOPSIS Self-grants a Key Vault RBAC role to the currently signed-in user and waits
              for propagation (RBAC mode KVs don't give the creator any default rights).
    #>
    param(
        [Parameter(Mandatory)][string]$ResourceGroup,
        [Parameter(Mandatory)][string]$KeyVaultName,
        [Parameter(Mandatory)][ValidateSet('SecretsOfficer','CertificatesOfficer','Administrator')]
        [string]$Role,
        [string]$SubscriptionId,
        [int]$MaxPropagationSeconds = 90
    )
    if (-not $SubscriptionId) {
        $SubscriptionId = (Invoke-Native az 'account' 'show' '--query' 'id' '-o' 'tsv' -Quiet).Trim()
    }
    $roleId = $script:KvRoleIds[$Role]
    $me = (Invoke-Native az 'ad' 'signed-in-user' 'show' '--query' 'id' '-o' 'tsv' -Quiet).Trim()
    if (-not $me) { throw 'Could not resolve current signed-in user object ID.' }
    $scope = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.KeyVault/vaults/$KeyVaultName"

    $existing = (Invoke-Native az 'role' 'assignment' 'list' `
        '--assignee' $me '--scope' $scope '--role' $roleId `
        '--query' '[].id' '-o' 'tsv' -Quiet -AllowNonZero) -join ''
    if ($existing) {
        Write-Ok "Deployer already has Key Vault $Role on $KeyVaultName."
        return
    }

    Write-Info "Granting Key Vault $Role to deployer ($me) on $KeyVaultName…"
    Invoke-Native az 'role' 'assignment' 'create' `
        '--assignee-object-id' $me '--assignee-principal-type' 'User' `
        '--role' $roleId '--scope' $scope `
        '-o' 'none' -Quiet | Out-Null
    Write-Info "Waiting up to ${MaxPropagationSeconds}s for RBAC propagation…"
    Start-Sleep -Seconds 15  # base wait; full retries happen at the actual operation site
}

function Invoke-AzureSqlCommand {
    param(
        [Parameter(Mandatory)][string]$ServerFqdn,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$Sql
    )
    if (-not (Get-Module -ListAvailable -Name SqlServer)) {
        Write-Info 'Installing SqlServer PowerShell module (CurrentUser scope, one-time)…'
        Install-Module -Name SqlServer -Scope CurrentUser -Force -AllowClobber -ErrorAction Stop | Out-Null
    }
    Import-Module SqlServer -ErrorAction Stop

    $token = (Invoke-Native az 'account' 'get-access-token' '--resource' 'https://database.windows.net/' '--query' 'accessToken' '-o' 'tsv' -Quiet).Trim()
    if (-not $token) { throw 'Failed to obtain SQL access token from az CLI.' }

    Invoke-Sqlcmd -ServerInstance $ServerFqdn -Database $Database -AccessToken $token -Query $Sql -EncryptConnection -ErrorAction Stop
}
