#Requires -Version 7.0
<#
.SYNOPSIS
  Start stand-in, Auth.Issuer, Admin.Api, and the admin shell on 127.0.0.1 (Windows).
#>
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $Root
$LocalDir = if ($env:FF_LOCAL_DIR) { $env:FF_LOCAL_DIR } else { Join-Path $Root '.local-run' }
$Secrets = Join-Path $LocalDir 'secrets.env'
if (-not (Test-Path -LiteralPath $Secrets)) { Write-Error "missing $Secrets — run scripts/local/init-db.ps1 first"; exit 2 }

Get-Content -LiteralPath $Secrets | ForEach-Object {
  if ($_ -match "^\s*#" -or $_ -notmatch '=') { return }
  $k, $v = $_.Split('=', 2)
  $v = $v.Trim("'")
  Set-Item -Path "Env:$k" -Value $v
}

if (($env:ASPNETCORE_ENVIRONMENT ?? 'Development') -ne 'Development') {
  Write-Error 'refused: ASPNETCORE_ENVIRONMENT must be Development'; exit 2
}
$env:ASPNETCORE_ENVIRONMENT = 'Development'

$ShellPort = if ($env:FF_SHELL_PORT) { [int]$env:FF_SHELL_PORT } else { 5099 }
$StandinPort = if ($env:FF_STANDIN_PORT) { [int]$env:FF_STANDIN_PORT } else { 18280 }
$TokenPort = if ($env:FF_STANDIN_TOKEN_PORT) { [int]$env:FF_STANDIN_TOKEN_PORT } else { $StandinPort + 1 }
$ApiPort = if ($env:FF_API_PORT) { [int]$env:FF_API_PORT } else { 18290 }
$IssuerPort = if ($env:FF_ISSUER_PORT) { [int]$env:FF_ISSUER_PORT } else { 18291 }
$ControlPort = $StandinPort + 2

$env:ADMIN_DEV_SHELL = '1'
$env:ADMIN_DEV_SHELL_REDIRECT = "http://127.0.0.1:${ShellPort}/signin-oidc"
$env:ADMIN_SHELL_PORT = "$ShellPort"
$env:STANDIN_ALLOW = '1'
$env:STANDIN_PORT_BASE = "$StandinPort"
$env:STANDIN_STATE_DIR = (Join-Path $LocalDir 'standin-state')
$env:StandIn__Issuer = "http://127.0.0.1:${ShellPort}/standin"
$env:StandIn__AllowlistJson = (Join-Path $LocalDir 'allowlist.json')
$env:StandIn__Fixtures = (Join-Path $Root 'scripts\local\review-fixtures.json')
$env:ADMIN_API_ORIGIN = "http://127.0.0.1:${ApiPort}"
# Same as start.sh — server.mjs reads these (defaults 18080/18081 otherwise).
$env:STANDIN_FRONT_ORIGIN = "http://127.0.0.1:${StandinPort}"
$env:STANDIN_TOKEN_URL = "http://127.0.0.1:${TokenPort}/standin/connect/token"
New-Item -ItemType Directory -Force -Path $env:STANDIN_STATE_DIR | Out-Null
'{"entries":[{"value":"127.0.0.1/32"},{"value":"::1/128"}]}' | Set-Content -LiteralPath $env:StandIn__AllowlistJson -Encoding utf8

$super = if ($env:FF_PG_SUPERUSER) { $env:FF_PG_SUPERUSER } else { 'postgres' }
& psql -h $env:FF_DB_HOST -p $env:FF_DB_PORT -U $super -d $env:FF_DB_NAME -v ON_ERROR_STOP=1 -v "iss=$($env:StandIn__Issuer)" -f (Join-Path $Root 'scripts\local\review-seed.sql') | Out-Null

function Test-PortFree([int]$Port, [string]$Name) {
  $inUse = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue | Where-Object { $_.State -eq 'Listen' }
  if ($inUse) { Write-Error "port $Port in use ($Name)"; exit 2 }
}
Test-PortFree $StandinPort 'stand-in'
Test-PortFree $TokenPort 'stand-in token'
Test-PortFree $ControlPort 'stand-in control'
Test-PortFree $ApiPort 'admin API'
Test-PortFree $IssuerPort 'issuer'
Test-PortFree $ShellPort 'admin shell'
# Re-export after port checks (mirrors start.sh).
$env:STANDIN_FRONT_ORIGIN = "http://127.0.0.1:${StandinPort}"
$env:STANDIN_TOKEN_URL = "http://127.0.0.1:${TokenPort}/standin/connect/token"
$env:ADMIN_API_ORIGIN = "http://127.0.0.1:${ApiPort}"

dotnet build src/Login.StandIn/Login.StandIn.csproj -p:IncludeStandIn=true -v q --nologo
dotnet build src/Auth.Issuer/Auth.Issuer.csproj -v q --nologo
dotnet build src/Admin.Api/Admin.Api.csproj -v q --nologo

$standin = Start-Process -PassThru -WindowStyle Hidden -FilePath dotnet -ArgumentList @('run','--project','src/Login.StandIn/Login.StandIn.csproj','--no-build','-p:IncludeStandIn=true') -RedirectStandardOutput (Join-Path $LocalDir 'standin.log') -RedirectStandardError (Join-Path $LocalDir 'standin.err.log')
$standin.Id | Set-Content (Join-Path $LocalDir 'standin.pid')
Start-Sleep -Seconds 2
Invoke-WebRequest -Uri "http://127.0.0.1:${StandinPort}/standin/.well-known/jwks" -OutFile (Join-Path $LocalDir 'jwks.json') | Out-Null

$secretsSrc = Join-Path $env:STANDIN_STATE_DIR "standin-${StandinPort}\fixture-secrets.json"
if (Test-Path $secretsSrc) {
  Copy-Item $secretsSrc (Join-Path $LocalDir 'standin-passwords.json') -Force
  icacls (Join-Path $LocalDir 'standin-passwords.json') /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null
}

$env:Entra__Authority = 'https://login.microsoftonline.com'
$env:Entra__ClientId = '00000000-0000-0000-0000-000000000001'
$env:Entra__SecretRef = 'arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/local-run-AbCdEf'
$env:SecretRef__AllowedAccountIds = '000000000000'
$env:SecretRef__AllowedRegions = 'us-west-2'
$env:BackofficeAuth__Issuer = $env:StandIn__Issuer
$env:BackofficeAuth__IssuerUrl = "http://127.0.0.1:${IssuerPort}"
$env:ISSUER_URL = "http://127.0.0.1:${IssuerPort}"
$env:Urls = "http://127.0.0.1:${ApiPort}"
$env:ISSUER_ISS = $env:StandIn__Issuer
$env:ISSUER_JWKS = (Join-Path $LocalDir 'jwks.json')

$issuer = Start-Process -PassThru -WindowStyle Hidden -FilePath dotnet -ArgumentList @('run','--project','src/Auth.Issuer/Auth.Issuer.csproj','--no-build','--',"http://127.0.0.1:${IssuerPort}") -RedirectStandardOutput (Join-Path $LocalDir 'issuer.log') -RedirectStandardError (Join-Path $LocalDir 'issuer.err.log')
$issuer.Id | Set-Content (Join-Path $LocalDir 'issuer.pid')
Start-Sleep -Seconds 2

$api = Start-Process -PassThru -WindowStyle Hidden -FilePath dotnet -ArgumentList @('run','--project','src/Admin.Api/Admin.Api.csproj','--no-build') -RedirectStandardOutput (Join-Path $LocalDir 'admin-api.log') -RedirectStandardError (Join-Path $LocalDir 'admin-api.err.log')
$api.Id | Set-Content (Join-Path $LocalDir 'admin-api.pid')
Start-Sleep -Seconds 2

@(
  "SHELL_PORT=$ShellPort"
  "STANDIN_PORT=$StandinPort"
  "STANDIN_TOKEN_PORT=$TokenPort"
  "API_PORT=$ApiPort"
  "ISSUER_PORT=$IssuerPort"
  "ADMIN_SHELL_URL=http://127.0.0.1:${ShellPort}/"
  "STANDIN_ADMIN_URL=http://127.0.0.1:${ShellPort}/standin/admin"
) | Set-Content (Join-Path $LocalDir 'ports.env') -Encoding utf8

Write-Host "Services up — shell http://127.0.0.1:${ShellPort}/  admin http://127.0.0.1:${ShellPort}/standin/admin"
Write-Host "Usernames: local_sa local_pa local_ca local_la local_la_one. Passwords in $LocalDir (ACL restricted). TOTP: scripts/local/totp.ps1 <user>"
Write-Host "Starting admin shell (foreground). Ctrl+C stops the shell — run scripts/local/stop.ps1 for the rest."
node ui/admin-shell/server.mjs
