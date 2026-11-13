#Requires -Version 7.0
<#
.SYNOPSIS
  Windows port of seed-dev-assignment.sh — inserts fixture Super Admin + Partner Admin assignments.
#>
$ErrorActionPreference = 'Stop'
if ($env:REQ064_DEV_SEED -ne '1') {
  Write-Error 'refused: set REQ064_DEV_SEED=1 to run this dev-only seed'
  exit 2
}
if (($env:ASPNETCORE_ENVIRONMENT ?? 'Development') -eq 'Production') {
  Write-Error 'refused: ASPNETCORE_ENVIRONMENT is Production'
  exit 2
}

$cs = $env:CONNECTION_STRING
if (-not $cs) {
  $envFile = Join-Path $env:TEMP 'ff-db\adminshell.env'
  if (-not (Test-Path $envFile)) {
    Write-Error "refused: CONNECTION_STRING is unset and $envFile is missing"
    exit 2
  }
  $line = Get-Content $envFile | Where-Object { $_ -match "^CONNECTION_STRING='(.*)'$" } | Select-Object -First 1
  if ($line -match "^CONNECTION_STRING='(.*)'$") { $cs = $Matches[1] }
}
if (-not $cs) { Write-Error 'refused: connection string is empty'; exit 2 }

function Field([string]$name) {
  $rx = '^(?i)' + [regex]::Escape($name) + '=(.*)$'
  foreach ($part in $cs.Split(';')) {
    if ($part -match $rx) { return $Matches[1] }
  }
  return $null
}

$hostName = Field 'Host'
if (-not $hostName) { Write-Error 'refused: connection string is missing Host'; exit 2 }
$port = Field 'Port'; if (-not $port) { $port = '5432' }
$db = Field 'Database'
$user = Field 'Username'
$pass = Field 'Password'
if (-not $db -or -not $user -or -not $pass) { Write-Error 'refused: connection string incomplete'; exit 2 }

if ($hostName -notin @('127.0.0.1', 'localhost', '::1')) {
  Write-Error "refused: host $hostName is not a local disposable database"; exit 2
}
if ($port -notin @('5432', '5433')) {
  Write-Error "refused: port $port is not a local disposable database"; exit 2
}
if ($db -in @('postgres', 'template0', 'template1')) {
  Write-Error "refused: $db is a system database"; exit 2
}
if ($db -notmatch '^ff_kit_[a-z0-9_]+$') {
  Write-Error "refused: database $db is not a disposable ff_kit_ database"; exit 2
}

if (-not (Get-Command psql -ErrorAction SilentlyContinue)) {
  $env:Path = "C:\Program Files\PostgreSQL\18\bin;$env:Path"
}

# Owner can SELECT assignments but after definer-owner.sql only *_auth_owner / *_test_seed may INSERT.
# On Windows use the local postgres superuser (same disposable kit gates as above) when FF_PG_SUPERUSER_PASSWORD is set.
$psqlUser = $user
$psqlPass = $pass
if ($env:FF_PG_SUPERUSER_PASSWORD) {
  $psqlUser = if ($env:FF_PG_ADMIN_USER) { $env:FF_PG_ADMIN_USER } else { 'postgres' }
  $psqlPass = $env:FF_PG_SUPERUSER_PASSWORD
}

$env:PGPASSWORD = $psqlPass
$marker = (& psql -X -q -tA -v ON_ERROR_STOP=1 -h $hostName -p $port -U $psqlUser -d $db `
  -c "SELECT coalesce(shobj_description(oid, 'pg_database'), '') FROM pg_database WHERE datname = current_database()").Trim()
if ($marker -ne 'floorfunds-kit') {
  Write-Error "refused: $db is not marked floorfunds-kit"; exit 2
}

$iss = if ($env:STANDIN_ISSUER) { $env:STANDIN_ISSUER } else { 'http://127.0.0.1:5088/standin' }
if ($iss -notmatch '^http://(127\.0\.0\.1|localhost):\d+/standin$') {
  Write-Error 'refused: stand-in issuer is not a loopback /standin URL'; exit 2
}

$sql = @"
INSERT INTO auth.admin_assignment (assignment_id, user_id, role_id, scope_kind, status, version, granted_by, granted_at)
SELECT '5f0c3e1a-0064-4000-8000-0000000000d1', u.user_id, 'super_admin', 'global', 'active', 1, u.user_id, now()
  FROM auth.app_user u
 WHERE u.user_id = '5f0c3e1a-0003-4000-8000-000000000c0a' AND u.status = 'active'
   AND NOT EXISTS (
     SELECT 1 FROM auth.admin_assignment a
      WHERE a.user_id = u.user_id AND a.role_id = 'super_admin' AND a.scope_kind = 'global'
        AND a.status = 'active' AND a.revoked_at IS NULL);
INSERT INTO auth.admin_assignment (
  assignment_id, user_id, role_id, scope_kind, partner_id, location_id, status, version, granted_by, granted_at, revoked_at)
SELECT '5f0c3e1a-0003-4000-8000-000000000c14', u.user_id, 'partner_admin', 'partner',
       '5f0c3e1a-0001-4000-8000-000000000001', NULL, 'active', 1, u.user_id, now(), NULL
  FROM auth.app_user u
 WHERE u.user_id = '5f0c3e1a-0003-4000-8000-000000000c04' AND u.status = 'active'
   AND NOT EXISTS (
     SELECT 1 FROM auth.admin_assignment a
      WHERE a.user_id = u.user_id AND a.role_id = 'partner_admin' AND a.status = 'active' AND a.revoked_at IS NULL);
INSERT INTO auth.user_issuer (user_id, issuer)
SELECT u.user_id, :'iss'
  FROM auth.app_user u
 WHERE u.user_id IN ('5f0c3e1a-0003-4000-8000-000000000c0a', '5f0c3e1a-0003-4000-8000-000000000c04')
ON CONFLICT (user_id) DO UPDATE SET issuer = EXCLUDED.issuer;
"@

$sql | & psql -X -q -v ON_ERROR_STOP=1 -h $hostName -p $port -U $psqlUser -d $db -v "iss=$iss"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "dev seed applied on $db (as $psqlUser)"
