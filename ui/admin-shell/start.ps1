#Requires -Version 7.0
<#
.SYNOPSIS
  Windows port of ui/admin-shell/start.sh — starts stand-in + Auth.Issuer + Admin.Api + Node shell on loopback.
.NOTES
  Dev-only. Requires ASPNETCORE_ENVIRONMENT=Development (defaulted here), local Postgres, dotnet, node, psql.
  State/logs/secrets live under $env:TEMP\ff-db (not the repo). Stop any process already on 18080/18081/18090/18091/5088 first.
#>
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $Root

if (-not $env:ASPNETCORE_ENVIRONMENT) { $env:ASPNETCORE_ENVIRONMENT = 'Development' }
if ($env:ASPNETCORE_ENVIRONMENT -ne 'Development') {
  Write-Error 'admin shell refused to start: ASPNETCORE_ENVIRONMENT must be Development'
  exit 2
}

$FfDb = Join-Path $env:TEMP 'ff-db'
New-Item -ItemType Directory -Force -Path $FfDb | Out-Null

function Test-Listening([int]$p) {
  $null -ne (Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalAddress -in @('127.0.0.1', '::1', '0.0.0.0', '::') } |
    Select-Object -First 1)
}

# Prefer the Linux start.sh ports; if a prior stand-in still owns 18080, use the Windows alternate set.
$Port = 5088
$StandinBase = 18080
$ApiPort = 18090
$IssuerPort = 18091
if (Test-Listening 18080) {
  Write-Host 'Port 18080 is busy (another stand-in). Using Windows alternate ports 5188 / 19080-19082 / 19090 / 19091.'
  $Port = 5188
  $StandinBase = 19080
  $ApiPort = 19090
  $IssuerPort = 19091
}

$env:ADMIN_DEV_SHELL = '1'
$env:ADMIN_DEV_SHELL_REDIRECT = "http://127.0.0.1:${Port}/signin-oidc"
$env:ADMIN_DEV_SHELL_SECRET_PATH = Join-Path $FfDb 'admin-dev-shell.secret'
$env:ADMIN_SHELL_PORT = "$Port"
$env:STANDIN_ALLOW = '1'
$env:STANDIN_PORT_BASE = "$StandinBase"
$env:STANDIN_STATE_DIR = Join-Path $FfDb 'admin-shell-standin'
$env:StandIn__Issuer = "http://127.0.0.1:${Port}/standin"
$env:StandIn__AllowlistJson = Join-Path $FfDb 'admin-shell-allowlist.json'
$env:ADMIN_API_ORIGIN = "http://127.0.0.1:${ApiPort}"
$env:STANDIN_FRONT_ORIGIN = "http://127.0.0.1:${StandinBase}"
$env:STANDIN_TOKEN_URL = "http://127.0.0.1:$($StandinBase + 1)/standin/connect/token"
$env:STANDIN_ISSUER = "http://127.0.0.1:${Port}/standin"
New-Item -ItemType Directory -Force -Path $env:STANDIN_STATE_DIR | Out-Null

function Test-PortFree([int]$p, [string]$label) {
  if (Test-Listening $p) {
    Write-Error "admin shell refused to start: port $p ($label) is already listening — stop that process first"
    exit 2
  }
}

function Wait-Port([int]$p, [string]$label, [int]$seconds = 90) {
  $deadline = [datetime]::UtcNow.AddSeconds($seconds)
  while ([datetime]::UtcNow -lt $deadline) {
    try {
      $c = [System.Net.Sockets.TcpClient]::new()
      $c.Connect('127.0.0.1', $p)
      $c.Dispose()
      return
    } catch {
      Start-Sleep -Milliseconds 400
    }
  }
  Write-Error "admin shell refused to start: $label did not listen on $p within ${seconds}s"
  exit 1
}

function New-HexPassword {
  -join ((1..18) | ForEach-Object { '{0:x2}' -f (Get-Random -Maximum 256) })
}

$DB_NAME = 'ff_kit_adminshell'
$DB_HOST = '127.0.0.1'
$DB_PORT = 5432
$OWNER_PW = New-HexPassword
$ADMIN_PW = New-HexPassword
$LOGIN_PW = New-HexPassword
$ISSUER_PW = New-HexPassword

$env:FF_DB_PASSWORD = $OWNER_PW
$env:FF_ADMIN_API_PASSWORD = $ADMIN_PW
$env:FF_LOGIN_PASSWORD = $LOGIN_PW
$env:FF_ISSUER_PASSWORD = $ISSUER_PW

if (-not $env:FF_PG_SUPERUSER_PASSWORD -and (Test-Path 'C:\Personal_Work\ProofbyteIO\ff-db\floorfunds_dev.env')) {
  Get-Content 'C:\Personal_Work\ProofbyteIO\ff-db\floorfunds_dev.env' | ForEach-Object {
    if ($_ -match '^\s*FF_PG_SUPERUSER_PASSWORD=(.*)$') { $env:FF_PG_SUPERUSER_PASSWORD = $Matches[1] }
  }
}
if (-not $env:FF_PG_SUPERUSER_PASSWORD) {
  Write-Error 'Set FF_PG_SUPERUSER_PASSWORD (postgres superuser password) before starting'
  exit 2
}

$psql = Get-Command psql -ErrorAction SilentlyContinue
if (-not $psql) {
  $candidate = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
  if (Test-Path $candidate) { $env:Path = "C:\Program Files\PostgreSQL\18\bin;$env:Path" }
  else { Write-Error 'psql not found on PATH'; exit 3 }
}

$env:PGPASSWORD = $env:FF_PG_SUPERUSER_PASSWORD
$kitState = & psql -X -q -tA -h $DB_HOST -p $DB_PORT -U postgres -d postgres -c @"
SELECT CASE
  WHEN EXISTS (
    SELECT 1 FROM pg_database d
     WHERE d.datname = 'ff_kit_adminshell' AND NOT d.datistemplate
       AND shobj_description(d.oid, 'pg_database') = 'floorfunds-kit'
  ) THEN 'marked'
  WHEN EXISTS (SELECT 1 FROM pg_database d WHERE d.datname = 'ff_kit_adminshell') THEN 'foreign'
  ELSE 'absent'
END
"@
$kitState = ($kitState | Out-String).Trim()
Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue

switch ($kitState) {
  'foreign' {
    Write-Error 'admin shell refused to start: ff_kit_adminshell exists and is not a synthetic kit database'
    exit 2
  }
  'marked' {
    Write-Host 'Dropping synthetic database ff_kit_adminshell so a database migrated at an older tip does not survive'
    $env:PGPASSWORD = $env:FF_PG_SUPERUSER_PASSWORD
    & pwsh -NoProfile -File .\db\setup.ps1 -Db $DB_NAME -Role $DB_NAME -DbHost $DB_HOST -Port $DB_PORT -AdminHost $DB_HOST -Force -Drop -NoMigrate
    $setupExit = $LASTEXITCODE
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    if ($setupExit -ne 0) { exit $setupExit }
  }
  'absent' {
    Write-Host 'Synthetic database ff_kit_adminshell is absent; it will be created from the current migrations'
  }
  default {
    Write-Error "admin shell refused to start: could not read whether ff_kit_adminshell is a synthetic kit database (got '$kitState')"
    exit 2
  }
}

Write-Host 'Creating synthetic database ff_kit_adminshell from the current migrations and loading the seed'
$env:PGPASSWORD = $env:FF_PG_SUPERUSER_PASSWORD
& pwsh -NoProfile -File .\db\setup.ps1 -Db $DB_NAME -Role $DB_NAME -DbHost $DB_HOST -Port $DB_PORT -AdminHost $DB_HOST -Force -Seed
$setupExit = $LASTEXITCODE
Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
if ($setupExit -ne 0) { exit $setupExit }

$env:CONNECTION_STRING = "Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_NAME};Password=${OWNER_PW}"
$env:ADMIN_API_CONNECTION_STRING = "Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_NAME}_admin_api;Password=${ADMIN_PW}"
$env:LOGIN_CONNECTION_STRING = "Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_NAME}_login;Password=${LOGIN_PW}"
$env:AUTH_CHECKS_DB = $DB_NAME

$envFile = Join-Path $FfDb 'adminshell.env'
@(
  "CONNECTION_STRING='$($env:CONNECTION_STRING)'"
  "ADMIN_API_CONNECTION_STRING='$($env:ADMIN_API_CONNECTION_STRING)'"
  "LOGIN_CONNECTION_STRING='$($env:LOGIN_CONNECTION_STRING)'"
  "AUTH_CHECKS_DB='$($env:AUTH_CHECKS_DB)'"
) | Set-Content -Path $envFile -Encoding utf8
Write-Host "disposable database ${DB_NAME} on ${DB_HOST}:${DB_PORT}"

Set-Content -Path $env:StandIn__AllowlistJson -Value '{"entries":[{"value":"127.0.0.1/32"},{"value":"::1/128"}]}' -Encoding utf8

# Broken git symlink on Windows: serve fonts from login-hub via junction when needed.
$fontsLink = Join-Path $PSScriptRoot 'fonts'
if ((Test-Path $fontsLink) -and -not (Get-Item $fontsLink).PSIsContainer) {
  Remove-Item $fontsLink -Force
}
if (-not (Test-Path $fontsLink)) {
  New-Item -ItemType Junction -Path $fontsLink -Target (Join-Path $Root 'ui\login-hub\fonts') | Out-Null
}

Test-PortFree $StandinBase 'stand-in'
Test-PortFree ($StandinBase + 1) 'stand-in token'
Test-PortFree $ApiPort 'admin API'
Test-PortFree $IssuerPort 'issuer'
Test-PortFree $Port 'admin shell'

# Build into %TEMP% so a leftover Login.StandIn.exe locking src/.../bin does not block the build.
$buildRoot = Join-Path $FfDb 'build'
$standinOut = Join-Path $buildRoot 'standin'
$issuerOut = Join-Path $buildRoot 'issuer'
$apiOut = Join-Path $buildRoot 'api'
Write-Host "Building Login.StandIn, Auth.Issuer, Admin.Api into $buildRoot ..."
dotnet build src/Login.StandIn/Login.StandIn.csproj -p:IncludeStandIn=true -o $standinOut -v q --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build src/Auth.Issuer/Auth.Issuer.csproj -o $issuerOut -v q --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build src/Admin.Api/Admin.Api.csproj -o $apiOut -v q --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$standinLog = Join-Path $FfDb 'admin-shell-standin.log'
$standinErr = Join-Path $FfDb 'admin-shell-standin.err.log'
$issuerLog = Join-Path $FfDb 'admin-shell-issuer.log'
$apiLog = Join-Path $FfDb 'admin-shell-api.log'
$apiErr = Join-Path $FfDb 'admin-shell-api.err.log'

$standinDll = Join-Path $standinOut 'Login.StandIn.dll'
$standin = Start-Process -FilePath 'dotnet' -ArgumentList @($standinDll) -WorkingDirectory $Root -PassThru -WindowStyle Hidden -RedirectStandardOutput $standinLog -RedirectStandardError $standinErr
$standin.Id | Set-Content (Join-Path $FfDb 'admin-shell-standin.pid')
Wait-Port $StandinBase 'stand-in'
Wait-Port ($StandinBase + 1) 'stand-in token'

$jwksPath = Join-Path $FfDb 'admin-shell-jwks.json'
try {
  Invoke-WebRequest -Uri "http://127.0.0.1:${StandinBase}/standin/.well-known/jwks" -OutFile $jwksPath -UseBasicParsing
} catch {
  Write-Error "admin shell refused to start: stand-in pid $($standin.Id) owns port $StandinBase but did not serve its JWKS"
  exit 1
}
if (-not (Test-Path $jwksPath) -or (Get-Item $jwksPath).Length -eq 0) {
  Write-Error 'admin shell refused to start: empty JWKS'
  exit 1
}

$env:Entra__Authority = 'https://login.microsoftonline.com'
$env:Entra__ClientId = '00000000-0000-0000-0000-000000000001'
$env:Entra__SecretRef = 'arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/admin-shell-AbCdEf'
$env:SecretRef__AllowedAccountIds = '000000000000'
$env:SecretRef__AllowedRegions = 'us-west-2'
$env:BackofficeAuth__Issuer = "http://127.0.0.1:${Port}/standin"
$env:BackofficeAuth__IssuerUrl = "http://127.0.0.1:${IssuerPort}"
$env:ISSUER_URL = "http://127.0.0.1:${IssuerPort}"
Remove-Item Env:BackofficeAuth__JwksPath -ErrorAction SilentlyContinue
Remove-Item Env:ISSUER_CONNECTION_STRING -ErrorAction SilentlyContinue
Remove-Item Env:ISSUER_CALLER_SECRET -ErrorAction SilentlyContinue
Remove-Item Env:ISSUER_JWKS -ErrorAction SilentlyContinue
Remove-Item Env:ISSUER_ISS -ErrorAction SilentlyContinue
$env:Urls = "http://127.0.0.1:${ApiPort}"

# Issuer holds the DB password + JWKS; start in a child env so Admin.Api never sees them.
$issuerDll = Join-Path $issuerOut 'Auth.Issuer.dll'
$issuerPsi = [System.Diagnostics.ProcessStartInfo]::new()
$issuerPsi.FileName = 'dotnet'
$issuerPsi.Arguments = "`"$issuerDll`" http://127.0.0.1:${IssuerPort}"
$issuerPsi.WorkingDirectory = $Root
$issuerPsi.UseShellExecute = $false
$issuerPsi.RedirectStandardOutput = $true
$issuerPsi.RedirectStandardError = $true
$issuerPsi.CreateNoWindow = $true
foreach ($kv in [System.Environment]::GetEnvironmentVariables().GetEnumerator()) {
  $issuerPsi.Environment[$kv.Key] = [string]$kv.Value
}
$issuerPsi.Environment['ISSUER_CONNECTION_STRING'] = "Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_NAME}_issuer;Password=${ISSUER_PW}"
$issuerPsi.Environment['ISSUER_ISS'] = "http://127.0.0.1:${Port}/standin"
$issuerPsi.Environment['ISSUER_JWKS'] = $jwksPath
$issuerProc = [System.Diagnostics.Process]::Start($issuerPsi)
$issuerProc.Id | Set-Content (Join-Path $FfDb 'admin-shell-issuer.pid')
Start-Job -ScriptBlock {
  param($p, $log)
  $p.StandardOutput.ReadToEnd() | Out-File $log -Append
  $p.StandardError.ReadToEnd() | Out-File $log -Append
} -ArgumentList $issuerProc, $issuerLog | Out-Null
Wait-Port $IssuerPort 'issuer'

try {
  Invoke-WebRequest -Uri "http://127.0.0.1:${IssuerPort}/v1/session" -Method POST -Body 'x' -UseBasicParsing | Out-Null
  Write-Error 'admin shell refused to start: issuer did not return 401'
  exit 1
} catch {
  $code = $null
  if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
  if ($code -ne 401) {
    Write-Error "admin shell refused to start: issuer probe failed ($code). See $issuerLog"
    exit 1
  }
}

$apiDll = Join-Path $apiOut 'Admin.Api.dll'
$api = Start-Process -FilePath 'dotnet' -ArgumentList @($apiDll) -WorkingDirectory $Root -PassThru -WindowStyle Hidden -RedirectStandardOutput $apiLog -RedirectStandardError $apiErr
$api.Id | Set-Content (Join-Path $FfDb 'admin-shell-api.pid')
Wait-Port $ApiPort 'admin API'

try {
  $nav = Invoke-WebRequest -Uri "http://127.0.0.1:${ApiPort}/api/nav" -UseBasicParsing
  Write-Error "admin shell refused to start: /api/nav returned $($nav.StatusCode) (want 401). See $apiErr"
  exit 1
} catch {
  $code = $null
  if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
  if ($code -ne 401) {
    Write-Error "admin shell refused to start: /api/nav returned $code (want 401). See $apiErr"
    exit 1
  }
}

$controlPort = $StandinBase + 2
$secretsPath = Join-Path $env:STANDIN_STATE_DIR "standin-${StandinBase}\fixture-secrets.json"
Write-Host 'admin shell listeners are the stand-in, issuer, and API this script started'
Write-Host "Open http://127.0.0.1:${Port}/ then Sign in as super_admin"
Write-Host "After first start, seed assignments: `$env:REQ064_DEV_SEED=1; `$env:STANDIN_ISSUER='http://127.0.0.1:${Port}/standin'; pwsh -File ui/admin-shell/seed-dev-assignment.ps1"
Write-Host "TOTP: Invoke-RestMethod http://127.0.0.1:${controlPort}/_standin/users/super_admin/totp"
Write-Host "Password: (Get-Content '$secretsPath' | ConvertFrom-Json).passwords.super_admin"

node ui/admin-shell/server.mjs
