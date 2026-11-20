#Requires -Version 7.0
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$LocalDir = if ($env:FF_LOCAL_DIR) { $env:FF_LOCAL_DIR } else { Join-Path $Root '.local-run' }

function Stop-LocalPid([string]$Name, [string]$File) {
  $path = Join-Path $LocalDir $File
  if (-not (Test-Path $path)) { return }
  $procId = (Get-Content $path -Raw).Trim()
  if ($procId -and (Get-Process -Id $procId -ErrorAction SilentlyContinue)) {
    Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
    Write-Host "stopped $Name pid=$procId"
  }
  Remove-Item $path -Force -ErrorAction SilentlyContinue
}

Stop-LocalPid 'admin-api' 'admin-api.pid'
Stop-LocalPid 'issuer' 'issuer.pid'
Stop-LocalPid 'stand-in' 'standin.pid'
Stop-LocalPid 'shell' 'shell.pid'
Write-Host 'local-run services stopped (DB left running).'
