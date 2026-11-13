#Requires -Version 7.0

<#
.SYNOPSIS
  FloorFunds dev DB setup (PowerShell port of db/setup.sh).
  Never prints a password; passwords reach psql only via stdin
  (role passwords) or the child's PGPASSWORD env var (admin / owner login).
    0. read-only preflight (db/kit/preflight.sql) BEFORE any change: refuses
       postgres/template DBs, superusers, and any existing role or DB the kit did
       not create (no 'floorfunds-kit' marker), kit roles with BYPASSRLS/CREATEROLE/
       CREATEDB/REPLICATION or a membership outside the kit (KF1), and a preflight
       output without its end marker (KF3); -Force never overrides that.
       Re-running on the kit's own (marked) objects needs -Force (K1, TL 8:32).
    1. owner login role + a database it owns (superuser step; marked 'floorfunds-kit')
    2. the S2a app roles <db>_admin_api and <db>_login, plus <db>_issuer (superuser step; spec 8.2).
       <db>_issuer is LOGIN with no membership. Its password is only for the issuer process.
    3. db/migrations.sql applied AS THE OWNER (idempotent; the only way schema
       and tables are created). Skip with -NoMigrate.
    4. optional TEST-ONLY seed db/seed/*.sql (-Seed; SSE2-FAKE-TEST-ONLY data)
  WARNING (I-3): -Password/-AdminApiPassword/-LoginPassword/-IssuerPassword given on the command line (e.g. via
  ConvertTo-SecureString -AsPlainText) put the password in argv, visible in the process list and in
  shell history. Prefer the hidden prompts, or env FF_DB_PASSWORD / FF_ADMIN_API_PASSWORD / FF_LOGIN_PASSWORD /
  FF_ISSUER_PASSWORD. FF_ISSUER_PASSWORD is for the issuer process only, not the admin host.
  Exit: 0 ok, 2 bad args, 3 psql missing, 4 migrations failed, 5 refused
  (nothing changed) or a kit SQL guard aborted (earlier steps may have completed; see db/README.md, I-1).
  Tested only with pwsh 7.6.9 on Linux; Windows (incl. the TCP admin login) untested.
.EXAMPLE
  ./db/setup.ps1                      # floorfunds_dev, prompts for passwords
  ./db/setup.ps1 -Db ff_x -Role ff_x -Seed
  ./db/setup.ps1 -Db ff_x -Role ff_x -Force -Drop -Seed   # recreate the kit's own ff_x
#>
# No [CmdletBinding()]: its -Debug common parameter has alias "db", which clashes with -Db.
param(
  [string]$Db = 'floorfunds_dev',
  [string]$Role = 'floorfunds_dev',
  [SecureString]$Password,
  [SecureString]$AdminApiPassword,   # else env FF_ADMIN_API_PASSWORD, else prompt
  [SecureString]$LoginPassword,      # else env FF_LOGIN_PASSWORD, else prompt
  [SecureString]$IssuerPassword,     # else env FF_ISSUER_PASSWORD, else prompt. Issuer process only.
  [Alias('Host')][string]$DbHost = '127.0.0.1',
  [int]$Port = 5432,
  # Admin (superuser) connection. Windows default: psql -U postgres -h localhost.
  # Pass -AdminHost '' to use the local socket (Linux/macOS peer auth).
  [AllowEmptyString()][string]$AdminHost = 'localhost',
  [string]$AdminUser = 'postgres',
  [switch]$Force,   # proceed on the kit's own (marked) existing objects; never on unmarked ones
  [switch]$Drop,    # needs -Force; drops only a kit-marked DB owned by -Role
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
$AdminApiRole = "${Db}_admin_api"; $LoginRole = "${Db}_login"; $IssuerRole = "${Db}_issuer"
if ($Role -ceq $AdminApiRole -or $Role -ceq $LoginRole -or $Role -ceq $IssuerRole) { Write-Error 'Invalid -Role: must not be an app role name' -ErrorAction Continue; exit 2 }
if (-not $NoMigrate -and -not (Test-Path -LiteralPath $Migrations -PathType Leaf)) { Write-Error "Migrations script not found: $Migrations" -ErrorAction Continue; exit 2 }
if ($Seed -and $NoMigrate) { Write-Error '-Seed needs the migrations (drop -NoMigrate)' -ErrorAction Continue; exit 2 }
if ($Drop -and -not $Force) { [Console]::Error.WriteLine('Refused: -Drop needs -Force (and drops only a kit-marked DB owned by -Role). Nothing was changed.'); exit 2 }
$Kit = Join-Path $PSScriptRoot 'kit'
foreach ($f in 'preflight.sql', 'role.sql', 'database.sql') {
  if (-not (Test-Path -LiteralPath (Join-Path $Kit $f) -PathType Leaf)) { Write-Error "Kit SQL not found: $(Join-Path $Kit $f)" -ErrorAction Continue; exit 2 }
}
# K1 hard refusals by name (no SQL at all): -Force never overrides these.
function Stop-Refused([string]$why) { [Console]::Error.WriteLine("Refused: $why. Nothing was changed."); exit 5 }
if ($Db -cin @('postgres', 'template0', 'template1')) { Stop-Refused "-Db $Db is a system database" }
foreach ($r in @($Role, $AdminApiRole, $LoginRole, $IssuerRole)) {
  if ($r -ceq 'postgres') { Stop-Refused 'role postgres is the bootstrap superuser' }
  if ($r.StartsWith('pg_')) { Stop-Refused "role ${r}: the pg_ prefix is reserved" }
}

function ConvertFrom-Secure([SecureString]$s) {
  [System.Net.NetworkCredential]::new('', $s).Password
}

function Get-Pw([SecureString]$given, [string]$envValue, [string]$prompt) {
  $v = if ($given) { ConvertFrom-Secure $given } elseif ($envValue) { $envValue } else { ConvertFrom-Secure (Read-Host -AsSecureString $prompt) }
  if ([string]::IsNullOrEmpty($v)) { Write-Error 'Empty password refused' -ErrorAction Continue; exit 2 }
  # K5: a newline/CR would break the psql \set the password travels in.
  if ($v.Contains("`n") -or $v.Contains("`r")) { [Console]::Error.WriteLine("Refused: the password ($prompt) contains a newline or carriage return. Nothing was changed."); exit 2 }
  # KF-A: only a SCRAM verifier is sent; SASLprep is the identity only for printable ASCII.
  if ($v -cnotmatch '^[\x20-\x7E]+$') { [Console]::Error.WriteLine("Refused: the password ($prompt) must be printable ASCII (space to ~). Nothing was changed."); exit 2 }
  $v
}
$pw = Get-Pw $Password $env:FF_DB_PASSWORD "Password for role $Role"
$apiPw = Get-Pw $AdminApiPassword $env:FF_ADMIN_API_PASSWORD "Password for app role $AdminApiRole"
$loginPw = Get-Pw $LoginPassword $env:FF_LOGIN_PASSWORD "Password for app role $LoginRole"
$issuerPw = Get-Pw $IssuerPassword $env:FF_ISSUER_PASSWORD "Password for app role $IssuerRole"

# Admin password: PGPASSWORD if already set; prompt only for a TCP admin connection.
$adminPw = $env:PGPASSWORD
if ($AdminHost -and -not $adminPw) {
  $adminPw = ConvertFrom-Secure (Read-Host -AsSecureString "Password for Postgres superuser $AdminUser")
}

$psqlCmd = Get-Command psql -ErrorAction SilentlyContinue
if (-not $psqlCmd) { Write-Error 'psql not found on PATH (Windows: add e.g. C:\Program Files\PostgreSQL\17\bin)' -ErrorAction Continue; exit 3 }
# The kit test stub stands in for the superuser client only. Owner TCP uses a real psql,
# the same split as setup.sh --superuser-cmd. A stub that reads stdin would wait forever.
function Get-OwnerPsql {
  foreach ($c in @(Get-Command psql -All -ErrorAction SilentlyContinue)) {
    if (-not $c.Source) { continue }
    $head = ""
    try { $head = (Get-Content -LiteralPath $c.Source -TotalCount 3 -ErrorAction Stop) -join "`n" } catch { $head = "" }
    if ($head -notmatch 'TEST-ONLY stand-in') { return $c.Source }
  }
  return $psqlCmd.Source
}
$ownerPsql = Get-OwnerPsql

function Escape-Arg([string]$a) {
  # Windows ProcessStartInfo.Arguments quoting (needed: ArgumentList is null under some Windows hosts).
  if ($a -notmatch '[\s"]') { return $a }
  '"' + ($a -replace '"', '\"') + '"'
}
function Invoke-Psql([string]$sql, [string[]]$extra = @(), [switch]$Capture, [int]$FailCode = 0) {
  # Values go in as psql variables (argv holds only names, never the password);
  # SQL uses format(%I/%L) + \gexec. The script itself travels on stdin.
  $psi = [System.Diagnostics.ProcessStartInfo]::new($psqlCmd.Source)
  $args = [System.Collections.Generic.List[string]]::new()
  foreach ($a in @('-X','-q','-v','ON_ERROR_STOP=1','-p',"$Port",'-U',$AdminUser,'-d','postgres',
                   '-v',"db=$Db",'-v',"role=$Role") + $extra + @('-f','-')) { $args.Add($a) }
  if ($AdminHost) { $args.Add('-h'); $args.Add($AdminHost) }
  $psi.UseShellExecute = $false
  if ($null -ne $psi.ArgumentList) {
    foreach ($a in $args) { [void]$psi.ArgumentList.Add($a) }
  } else {
    $psi.Arguments = (($args | ForEach-Object { Escape-Arg $_ }) -join ' ')
  }
  if ($adminPw) { $psi.Environment['PGPASSWORD'] = $adminPw } else { [void]$psi.Environment.Remove('PGPASSWORD') }
  $psi.RedirectStandardInput = $true
  $psi.RedirectStandardOutput = [bool]$Capture
  $psi.RedirectStandardError = $true
  $p = [System.Diagnostics.Process]::Start($psi)
  $p.StandardInput.Write($sql)
  $p.StandardInput.Close()
  $out = if ($Capture) { $p.StandardOutput.ReadToEnd() } else { '' }
  $err = $p.StandardError.ReadToEnd()
  $p.WaitForExit()
  if ($p.ExitCode -ne 0) {
    if ($err) { [Console]::Error.WriteLine($err) }
    $code = if ($FailCode) { $FailCode } else { $p.ExitCode }
    Write-Error "psql failed (exit $($p.ExitCode)); see the psql error above" -ErrorAction Continue; exit $code
  }
  $out
}
function Get-KitSql([string]$name) { [System.IO.File]::ReadAllText((Join-Path $Kit $name)) }

# 0. K1 preflight: read-only, before anything is changed.
Write-Host "Preflight (read-only): $Db, $Role, $AdminApiRole, $LoginRole, $IssuerRole..."
# KF-E: split on LF only. Strip trailing CR so Windows psql (CRLF) still matches markers (Unix stays LF-only).
$pre = @((Invoke-Psql (Get-KitSql 'preflight.sql') @('-tA', '-v', "api_role=$AdminApiRole", '-v', "login_role=$LoginRole", '-v', "issuer_role=$IssuerRole") -Capture -FailCode 5) -split "`n" | ForEach-Object { $_.TrimEnd("`r") } | Where-Object { $_ })
# KF3: fail closed. The preflight always ends with this line; empty or truncated output is a refusal.
if (-not $pre.Count -or $pre[-1] -cne 'END floorfunds-kit-preflight') {
  [Console]::Error.WriteLine('Refused: the preflight returned no end marker (empty or incomplete output), so the catalog could not be checked. Nothing was changed.'); exit 5
}
$refuse = @($pre | Where-Object { $_.StartsWith('REFUSE ') })
if ($refuse.Count) {
  foreach ($l in $refuse) { [Console]::Error.WriteLine('Refused: ' + $l.Substring(7)) }
  [Console]::Error.WriteLine('Refused (-Force never overrides this). Nothing was changed.'); exit 5
}
$exists = @($pre | Where-Object { $_.StartsWith('EXISTS ') })
if ($exists.Count -and -not $Force) {
  foreach ($l in $exists) { [Console]::Error.WriteLine('Already exists (created by the kit): ' + $l.Substring(7)) }
  [Console]::Error.WriteLine("Refused: re-run with -Force to update the kit's own objects (add -Drop to recreate the DB). Nothing was changed."); exit 5
}

function Get-ScramVerifier([string]$pw) {
  # RFC 5802/7677 SCRAM-SHA-256, 4096 iterations, 16-byte random salt: the format libpq's PQencryptPasswordConn makes.
  $C = [System.Security.Cryptography.HMACSHA256]
  $pb = [System.Text.Encoding]::ASCII.GetBytes($pw)
  $salt = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
  $salted = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($pb, $salt, 4096, [System.Security.Cryptography.HashAlgorithmName]::SHA256, 32)
  $ck = $C::HashData($salted, [System.Text.Encoding]::ASCII.GetBytes('Client Key'))
  $sk = $C::HashData($salted, [System.Text.Encoding]::ASCII.GetBytes('Server Key'))
  $stored = [System.Security.Cryptography.SHA256]::HashData($ck)
  'SCRAM-SHA-256$4096:' + [Convert]::ToBase64String($salt) + '$' + [Convert]::ToBase64String($stored) + ':' + [Convert]::ToBase64String($sk)
}
# Plain login role: no superuser, no BYPASSRLS, no CREATEDB/CREATEROLE/REPLICATION; created + marked, or
# updated only if it carries the kit marker (db/kit/role.sql).
function Set-AppRole([string]$r, [string]$p) {
  Write-Host "Role ${r}: create or update (kit-marked only)..."
  # KF-A: only the SCRAM-SHA-256 verifier reaches psql/the server (base64 + $ :, no escaping needed).
  $v = Get-ScramVerifier $p
  [void](Invoke-Psql ("\set r '$r'`n\set p '$v'`n" + (Get-KitSql 'role.sql')) -FailCode 5)
}
Set-AppRole $Role $pw

if ($Drop) { Write-Host "Dropping database $Db (only this database, only if kit-marked)..." }
Write-Host "Database ${Db}: create if missing (kit-marked only)..."
[void](Invoke-Psql (Get-KitSql 'database.sql') @('-v', "drop=$(if ($Drop) { 1 } else { 0 })") -FailCode 5)

# S2a app roles (spec 8.2). The migration grants to them and RAISEs if either is missing or privileged.
Set-AppRole $AdminApiRole $apiPw
Set-AppRole $LoginRole $loginPw
Set-AppRole $IssuerRole $issuerPw
$apiPw = $null; $loginPw = $null; $issuerPw = $null

# As the owner over TCP; the owner password goes in the child's env, never argv.
function Invoke-OwnerPsql([string[]]$extra, [switch]$AllowFail) {
  $psi = [System.Diagnostics.ProcessStartInfo]::new($ownerPsql)
  $args = [System.Collections.Generic.List[string]]::new()
  foreach ($a in @('-X','-q','-v','ON_ERROR_STOP=1','-h',$DbHost,'-p',"$Port",'-U',$Role,'-d',$Db) + $extra) { $args.Add($a) }
  $psi.UseShellExecute = $false
  if ($null -ne $psi.ArgumentList) {
    foreach ($a in $args) { [void]$psi.ArgumentList.Add($a) }
  } else {
    $psi.Arguments = (($args | ForEach-Object { Escape-Arg $_ }) -join ' ')
  }
  $psi.Environment['PGPASSWORD'] = $pw
  # Hides the expected "__EFMigrationsHistory already exists" NOTICE on re-runs.
  $psi.Environment['PGOPTIONS'] = '-c client_min_messages=warning'
  $psi.RedirectStandardInput = $true
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $p = [System.Diagnostics.Process]::Start($psi)
  $p.StandardInput.Close()
  $out = $p.StandardOutput.ReadToEnd()
  $err = $p.StandardError.ReadToEnd()
  $p.WaitForExit()
  if ($p.ExitCode -ne 0) {
    if ($AllowFail) { return "" }
    if ($err) { [Console]::Error.WriteLine($err) }
    Write-Error "psql (owner) failed (exit $($p.ExitCode)); see the error above" -ErrorAction Continue; exit 4
  }
  $out
}
if (-not $NoMigrate) {
  Write-Host "Migrations: applying $(Split-Path -Leaf $Migrations) as $Role (idempotent)..."
  [void](Invoke-OwnerPsql @('-f', $Migrations))
  $applied = Invoke-OwnerPsql @('-tA','-c','SELECT string_agg("MigrationId", '', '' ORDER BY "MigrationId") FROM public."__EFMigrationsHistory"')
  Write-Host "Applied migrations: $($applied.Trim())"
}
function Test-OwnerTrue([string]$sql) {
  # A missing database, or history not installed yet, is "not ready", as in setup.sh.
  return (Invoke-OwnerPsql @('-tA','-c',$sql) -AllowFail).Trim() -ceq 't'
}
$authReady = $false
$catalogReady = $false
if (Test-OwnerTrue 'SELECT to_regclass(''public."__EFMigrationsHistory"'') IS NOT NULL') {
  if (Test-OwnerTrue "SELECT to_regnamespace('auth') IS NOT NULL AND to_regprocedure('auth._request_user()') IS NOT NULL") { $authReady = $true }
  if (Test-OwnerTrue "SELECT to_regnamespace('catalog') IS NOT NULL") { $catalogReady = $true }
}
if ($authReady) {
  Write-Host "Definer owner: NOLOGIN role (superuser step)..."
  [void](Invoke-Psql ("\c $Db`n" + (Get-KitSql 'definer-owner.sql')) -FailCode 4)
} else {
  Write-Host "Definer owner: skipped, auth._request_user is not installed yet."
}
if ($catalogReady) {
  Write-Host "Catalog definer owner: NOLOGIN role (superuser step)..."
  [void](Invoke-Psql ("\c $Db`n" + (Get-KitSql 'catalog-definer-owner.sql')) -FailCode 4)
} else {
  Write-Host "Catalog definer owner: skipped, schema catalog is not installed yet."
}
function Set-TestSeedFileMode([string]$path) {
  # Unix: mode 0600 (+ optional SUDO_USER ACL). Windows: NTFS ACLs already limit the file to the creating user.
  if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) { return }
  & chmod 0600 $path
  if ($LASTEXITCODE -ne 0) { Write-Error "chmod 0600 failed on the test seed file" -ErrorAction Continue; exit 4 }
  $sudoUser = $env:SUDO_USER
  if ([string]::IsNullOrEmpty($sudoUser) -or $sudoUser -ceq [System.Environment]::UserName) { return }
  $uidText = $env:SUDO_UID
  if ([string]::IsNullOrEmpty($uidText) -or $uidText -notmatch '^[0-9]+$') { return }
  & chown -- $sudoUser -- $path 2>&1 | Out-Null
  if ($LASTEXITCODE -eq 0) { return }
  if (-not ('FfSeedAcl' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FfSeedAcl {
  [DllImport("libc", SetLastError = true)]
  public static extern int setxattr(string path, string name, byte[] value, UIntPtr size, int flags);
}
'@
  }
  $uid = [uint32]$uidText
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter $ms
  $bw.Write([uint32]2)
  $undef = [uint32]4294967295
  foreach ($tag in @(1, 2, 4, 16, 32)) {
    $perm = [uint16]0
    $id = $undef
    if ($tag -eq 1) { $perm = [uint16]6 }
    elseif ($tag -eq 2) { $perm = [uint16]4; $id = $uid }
    elseif ($tag -eq 16) { $perm = [uint16]4 }
    $bw.Write([uint16]$tag)
    $bw.Write($perm)
    $bw.Write($id)
  }
  $bw.Flush()
  $acl = $ms.ToArray()
  $rc = [FfSeedAcl]::setxattr($path, 'system.posix_acl_access', $acl, [UIntPtr]::new([uint64]$acl.Length), 0)
  if ($rc -ne 0) {
    $err = [System.Runtime.InteropServices.Marshal]::GetLastPInvokeError()
    Write-Error "test seed file could not be made readable by ${sudoUser} (errno $err)" -ErrorAction Continue
    exit 4
  }
}
if ($Seed) {
  $isWin = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
  $ffDbDir = if ($isWin) { Join-Path $env:TEMP 'ff-db' } else { '/tmp/ff-db' }
  New-Item -ItemType Directory -Force -Path $ffDbDir | Out-Null
  if (-not $isWin) { & chmod 1733 $ffDbDir }
  $seedEnv = Join-Path $ffDbDir "${Db}_test_seed.env"
  if (Test-Path -LiteralPath $seedEnv) {
    $seedPw = (Get-Content -LiteralPath $seedEnv | Where-Object { $_.StartsWith('TEST_SEED_PASSWORD=') } | Select-Object -First 1).Substring(19)
  } else {
    $seedPw = ([Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24))).ToLowerInvariant()
    $tmp = "$seedEnv.tmp"
    [System.IO.File]::WriteAllText($tmp, "TEST_SEED_PASSWORD=$seedPw`n")
    if (-not $isWin) { & chmod 0600 $tmp }
    Move-Item -Force -LiteralPath $tmp -Destination $seedEnv
  }
  Set-TestSeedFileMode $seedEnv
  Write-Host "Test seed role: LOGIN, no memberships (superuser step, test mode only)..."
  [void](Invoke-Psql ("\set test_seed_password '$seedPw'`n\c $Db`n" + (Get-KitSql 'test-seed-role.sql')) -FailCode 4)
  foreach ($f in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'seed') -Filter '*.sql' | Sort-Object Name) {
    Write-Host "Seed (TEST-ONLY): $($f.Name) as ${Db}_test_seed..."
    $psi = [System.Diagnostics.ProcessStartInfo]::new($psqlCmd.Source)
    $seedArgs = [System.Collections.Generic.List[string]]::new()
    foreach ($a in @('-X','-q','-v','ON_ERROR_STOP=1','-h',$DbHost,'-p',"$Port",'-U',"${Db}_test_seed",'-d',$Db,'-f',$f.FullName)) { $seedArgs.Add($a) }
    $psi.UseShellExecute = $false
    if ($null -ne $psi.ArgumentList) {
      foreach ($a in $seedArgs) { [void]$psi.ArgumentList.Add($a) }
    } else {
      $psi.Arguments = (($seedArgs | ForEach-Object { Escape-Arg $_ }) -join ' ')
    }
    $psi.Environment['PGPASSWORD'] = $seedPw
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $seedErr = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) {
      if ($seedErr) { [Console]::Error.WriteLine($seedErr) }
      Write-Error "psql (test seed) failed (exit $($p.ExitCode)); see the error above" -ErrorAction Continue; exit 4
    }
  }
  $seedPw = $null
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

The issuer role $IssuerRole is LOGIN and a member of nothing. Its password is
FF_ISSUER_PASSWORD, read only by the issuer process. Do not put it in the admin
host's config, environment, pool, or connection strings.
"@
