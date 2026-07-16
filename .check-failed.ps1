$ErrorActionPreference = 'Stop'
$app = 'app-spocs-clean-4bf5'
$armToken = az account get-access-token --resource https://management.core.windows.net/ --query accessToken -o tsv

$inner = @'
$ErrorActionPreference='Stop'
$t = (Invoke-RestMethod -Uri ($env:IDENTITY_ENDPOINT + '?resource=https://database.windows.net/&api-version=2019-08-01') -Headers @{ 'X-IDENTITY-HEADER' = $env:IDENTITY_HEADER }).access_token
$c = New-Object System.Data.SqlClient.SqlConnection
$c.ConnectionString = 'Server=tcp:sql-spocs-clean-4bf5.database.windows.net,1433;Database=spocoldstorage;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
$c.AccessToken = $t
$c.Open()
$cmd = $c.CreateCommand()
$cmd.CommandText = "SELECT status, COUNT(*) c FROM dbo.migration_job_items GROUP BY status ORDER BY c DESC"
$r = $cmd.ExecuteReader()
$map = @{0='Queued';10='Validating';11='ValidationFailed';20='MigrationInProgress';21='CopiedToColdStorage';22='CopyToColdStorageFailed';23='PostCopyValidation';24='DeletePending';25='DeleteFailed';26='PlaceholderCreating';27='PlaceholderFailed';30='ColdStorageMigrationCompleted';40='RestoreInProgress';41='RestoredToSharePoint';42='RestoreFailed';43='PostRestoreValidation';44='PlaceholderRemoving';45='PlaceholderRemoveFailed';50='RestoreCompleted';60='CompletedWithWarning';70='RetryScheduled';80='Cancelled';81='Skipped'}
while($r.Read()){ $s=[int]$r['status']; $n=$map[$s]; if(-not $n){$n="?$s"}; Write-Output ("{0,-30} {1}" -f $n, $r['c']) }
$r.Close(); $c.Close()
'@
$enc = [System.Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($inner))
$body = @{ command = "powershell -NoProfile -EncodedCommand $enc"; dir = 'site\wwwroot' } | ConvertTo-Json
$resp = Invoke-RestMethod -Method Post -Uri "https://$app.scm.azurewebsites.net/api/command" -Headers @{ Authorization = "Bearer $armToken" } -ContentType 'application/json' -Body $body
Write-Output "--- item status counts ---"
Write-Output $resp.Output
