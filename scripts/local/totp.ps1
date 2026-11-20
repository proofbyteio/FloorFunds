#Requires -Version 7.0
param(
  [Parameter(Mandatory = $true, Position = 0)][string]$Username
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$LocalDir = if ($env:FF_LOCAL_DIR) { $env:FF_LOCAL_DIR } else { Join-Path $Root '.local-run' }
$StandinPort = 18280
$ports = Join-Path $LocalDir 'ports.env'
if (Test-Path $ports) {
  Get-Content $ports | ForEach-Object {
    if ($_ -match '^STANDIN_PORT=(\d+)$') { $StandinPort = [int]$Matches[1] }
  }
}
$ControlPort = $StandinPort + 2
$resp = Invoke-RestMethod -Uri "http://127.0.0.1:${ControlPort}/_standin/users/${Username}/totp"
if ($resp.code) { Write-Output $resp.code } else { Write-Output $resp.error; exit 1 }
