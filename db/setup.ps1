#Requires -Version 7.0
<#
.SYNOPSIS
  FloorFunds dev DB setup (PowerShell port of db/setup.sh).
  Idempotent. Never prints a password; passwords reach psql only via stdin
  (role passwords) or the child's PGPASSWORD env var (admin / owner login).
    1. owner login role + a database it owns (superuser step)
    2. the S2a app roles <db>_admin_api and <db>_login (superuser step; spec 8.2)
    3. db/migrations.sql applied AS THE OWNER (idempotent; the only way schema
       and tables are created). Skip with -NoMigrate.
    4. optional TEST-ONLY seed db/seed/*.sql (-Seed; SSE2-FAKE-TEST-ONLY data)
  Tested only with pwsh 7.6.9 on Linux; Windows (incl. the TCP admin login) untested.
.EXAMPLE
  ./db/setup.ps1                      # floorfunds_dev, prompts for passwords
  ./db/setup.ps1 -Db ff_x -Role ff_x -Drop -Seed
#>
# No [CmdletBinding()]: its -Debug common parameter has alias "db", which clashes with -Db.
param(
  [string]$Db = 'floorfunds_dev',
  [string]$Role = 'floorfunds_dev',
  [SecureString]$Password,
  [SecureString]$AdminApiPassword,   # else env FF_ADMIN_API_PASSWORD, else prompt
  [SecureString]$LoginPassword,      # else env FF_LOGIN_PASSWORD, else prompt
  [Alias('Host')][string]$DbHost = '127.0.0.1',
  [int]$Port = 5432,
  # Admin (superuser) connection. Windows default: psql -U postgres -h localhost.
  # Pass -AdminHost '' to use the local socket (Linux/macOS peer auth).
  [AllowEmptyString()][string]$AdminHost = 'localhost',
  [string]$AdminUser = 'postgres',
  [switch]$Drop,
  [switch]$NoMigrate,
  [string]$Migrations = (Join-Path $PSScriptRoot 'migrations.sql'),
  [switch]$Seed
)
# -Host is an alias of -DbHost: $Host is a read-only automatic variable in PowerShell.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$NameRe = '^[a-z_][a-z0-9_]{0,62}$'
if ($Db -cnotmatch $NameRe)   { Write-Error "Invalid -Db name: must match $NameRe" -ErrorAction Continue; exit 2 }
if ($Role -cnotmatch $NameRe) { Write-Error "Invalid -Role name: must match $NameRe" -ErrorAction Continue; exit 2 }
if ($Port -lt 1 -or $Port -gt 65535) { Write-Error 'Invalid -Port' -ErrorAction Continue; exit 2 }
# <db>_admin_api must still fit in 63 bytes.
if ($Db.Length -gt 53) { Write-Error 'Invalid -Db name: at most 53 characters (app role <db>_admin_api must fit)' -ErrorAction Continue; exit 2 }
$AdminApiRole = "${Db}_admin_api"; $LoginRole = "${Db}_login"
if ($Role -ceq $AdminApiRole -or $Role -ceq $LoginRole) { Write-Error 'Invalid -Role: must not be an app role name' -ErrorAction Continue; exit 2 }
if (-not $NoMigrate -and -not (Test-Path -LiteralPath $Migrations -PathType Leaf)) { Write-Error "Migrations script not found: $Migrations" -ErrorAction Continue; exit 2 }
if ($Seed -and $NoMigrate) { Write-Error '-Seed needs the migrations (drop -NoMigrate)' -ErrorAction Continue; exit 2 }

function ConvertFrom-Secure([SecureString]$s) {
  [System.Net.NetworkCredential]::new('', $s).Password
}

function Get-Pw([SecureString]$given, [string]$envValue, [string]$prompt) {
  $v = if ($given) { ConvertFrom-Secure $given } elseif ($envValue) { $envValue } else { ConvertFrom-Secure (Read-Host -AsSecureString $prompt) }
  if ([string]::IsNullOrEmpty($v)) { Write-Error 'Empty password refused' -ErrorAction Continue; exit 2 }
  $v
}
$pw = Get-Pw $Password $env:FF_DB_PASSWORD "Password for role $Role"
$apiPw = Get-Pw $AdminApiPassword $env:FF_ADMIN_API_PASSWORD "Password for app role $AdminApiRole"
$loginPw = Get-Pw $LoginPassword $env:FF_LOGIN_PASSWORD "Password for app role $LoginRole"

# Admin password: PGPASSWORD if already set; prompt only for a TCP admin connection.
$adminPw = $env:PGPASSWORD
if ($AdminHost -and -not $adminPw) {
  $adminPw = ConvertFrom-Secure (Read-Host -AsSecureString "Password for Postgres superuser $AdminUser")
}

$psqlCmd = Get-Command psql -ErrorAction SilentlyContinue
if (-not $psqlCmd) { Write-Error 'psql not found on PATH (Windows: add e.g. C:\Program Files\PostgreSQL\17\bin)' -ErrorAction Continue; exit 3 }

function Invoke-Psql([string]$sql) {
  # Values go in as psql variables (argv holds only names, never the password);
  # SQL uses format(%I/%L) + \gexec. The script itself travels on stdin.
  $psi = [System.Diagnostics.ProcessStartInfo]::new($psqlCmd.Source)
  foreach ($a in @('-X','-q','-v','ON_ERROR_STOP=1','-p',"$Port",'-U',$AdminUser,'-d','postgres',
                   '-v',"db=$Db",'-v',"role=$Role",'-f','-')) { $psi.ArgumentList.Add($a) }
  if ($AdminHost) { $psi.ArgumentList.Add('-h'); $psi.ArgumentList.Add($AdminHost) }
  if ($adminPw) { $psi.Environment['PGPASSWORD'] = $adminPw } else { [void]$psi.Environment.Remove('PGPASSWORD') }
  $psi.RedirectStandardInput = $true
  $psi.UseShellExecute = $false
  $p = [System.Diagnostics.Process]::Start($psi)
  $p.StandardInput.Write($sql)
  $p.StandardInput.Close()
  $p.WaitForExit()
  if ($p.ExitCode -ne 0) { Write-Error "psql failed (exit $($p.ExitCode))" -ErrorAction Continue; exit $p.ExitCode }
}

# Plain login role: no superuser, no BYPASSRLS, no CREATEDB/CREATEROLE/REPLICATION.
function Set-AppRole([string]$r, [string]$p) {
  Write-Host "Role ${r}: create or update..."
  # psql quoted-variable escaping: ' -> '' and \ -> \\
  $pq = $p.Replace('\', '\\').Replace("'", "''")
  Invoke-Psql (@"
\set r '$r'
\set pw '$pq'
SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', :'r', :'pw')
  WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'r') \gexec
SELECT format('ALTER ROLE %I LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', :'r', :'pw') \gexec
"@ + "`n")
}
Set-AppRole $Role $pw

if ($Drop) {
  Write-Host "Dropping database $Db (only this database)..."
  Invoke-Psql "SELECT format('DROP DATABASE IF EXISTS %I WITH (FORCE)', :'db') \gexec`n"
}

Write-Host "Database ${Db}: create if missing..."
Invoke-Psql (@"
SELECT format('CREATE DATABASE %I OWNER %I', :'db', :'role')
  WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db') \gexec
SELECT format('ALTER DATABASE %I OWNER TO %I', :'db', :'role') \gexec
SELECT format('REVOKE CONNECT ON DATABASE %I FROM PUBLIC', :'db') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'db', :'role') \gexec
"@ + "`n")

# S2a app roles (spec 8.2). The migration grants to them and RAISEs if either is missing or privileged.
Set-AppRole $AdminApiRole $apiPw
Set-AppRole $LoginRole $loginPw
$apiPw = $null; $loginPw = $null

# As the owner over TCP; the owner password goes in the child's env, never argv.
function Invoke-OwnerPsql([string[]]$extra) {
  $psi = [System.Diagnostics.ProcessStartInfo]::new($psqlCmd.Source)
  foreach ($a in @('-X','-q','-v','ON_ERROR_STOP=1','-h',$DbHost,'-p',"$Port",'-U',$Role,'-d',$Db) + $extra) { $psi.ArgumentList.Add($a) }
  $psi.Environment['PGPASSWORD'] = $pw
  # Hides the expected "__EFMigrationsHistory already exists" NOTICE on re-runs.
  $psi.Environment['PGOPTIONS'] = '-c client_min_messages=warning'
  $psi.UseShellExecute = $false
  $psi.RedirectStandardOutput = $true
  $p = [System.Diagnostics.Process]::Start($psi)
  $out = $p.StandardOutput.ReadToEnd()
  $p.WaitForExit()
  if ($p.ExitCode -ne 0) { Write-Error "psql (owner) failed (exit $($p.ExitCode)); see the error above" -ErrorAction Continue; exit 4 }
  $out
}
if (-not $NoMigrate) {
  Write-Host "Migrations: applying $(Split-Path -Leaf $Migrations) as $Role (idempotent)..."
  [void](Invoke-OwnerPsql @('-f', $Migrations))
  $applied = Invoke-OwnerPsql @('-tA','-c','SELECT string_agg("MigrationId", '', '' ORDER BY "MigrationId") FROM public."__EFMigrationsHistory"')
  Write-Host "Applied migrations: $($applied.Trim())"
}
if ($Seed) {
  foreach ($f in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'seed') -Filter '*.sql' | Sort-Object Name) {
    Write-Host "Seed (TEST-ONLY): $($f.Name)..."
    [void](Invoke-OwnerPsql @('-f', $f.FullName))
  }
}
$pw = $null

$cs = "Host=$DbHost;Port=$Port;Database=$Db;Username=$Role;Password=<password>"
$migr = if ($NoMigrate) { 'NOT applied (-NoMigrate)' } else { 'applied' }
$seeded = if ($Seed) { 'loaded (TEST-ONLY)' } else { 'not loaded' }
Write-Host @"

Done. Migrations: $migr. Seed: $seeded.

Connection string templates (Npgsql); passwords are the ones you entered:
  owner:     $cs
  admin API: Host=$DbHost;Port=$Port;Database=$Db;Username=$AdminApiRole;Password=<password>
  login:     Host=$DbHost;Port=$Port;Database=$Db;Username=$LoginRole;Password=<password>

Set them outside the repo, as env vars (see db/README.md, "Run and test"):
  CONNECTION_STRING (owner), ADMIN_API_CONNECTION_STRING, LOGIN_CONNECTION_STRING
"@
