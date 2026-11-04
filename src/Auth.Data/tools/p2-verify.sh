#!/usr/bin/env bash
# S2a P2 verification on ff_sse ONLY (never ff_sse2). Rebuilds ff_sse twice. Prints no secrets.
# usage: src/Auth.Data/tools/p2-verify.sh   (from the repo root; needs ff-db.sh, sudo -u postgres, .NET 10 SDK)
set -euo pipefail
DB=ff_sse
FFDB=/workspace/cachd-ones/ff-db/ff-db.sh
cd "$(dirname "$0")/../.."   # src/
export PATH=/home/box/.dotnet:$PATH DOTNET_ROOT=/home/box/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
M() { dotnet run --no-build --project Auth.Data.Migrator -- "$@" 2>&1 | grep -v '^info:\|^      '; }
C() { dotnet run --no-build --project Auth.Data.Checks -- Login.StandIn/fixtures.json; }
q() { psql -X -tA -v ON_ERROR_STOP=1 -c "$1"; }
step() { echo; echo "=== $* ==="; }
envs() { . Auth.Data/tools/env.sh "$DB"; }

step "build (locked restore)"
dotnet build Auth.Data.Checks -p:RestoreLockedMode=true 2>&1 | grep -E 'Warn|Error\(s\)|error' | sort -u
dotnet build Auth.Data.Migrator -p:RestoreLockedMode=true 2>&1 | grep -E 'Error\(s\)' | sort -u

step "A1 rebuild $DB + roles"
$FFDB rebuild $DB | grep -v -i password
Auth.Data/tools/create-app-roles.sh $DB
envs
step "A2 runner: update (MigrateAsync) -> list"
M update; M list
echo "auth tables: $(q "select count(*) from pg_tables where schemaname='auth'")"
step "A3 runner: update 0 (revert all) -> list, nothing left"
M update 0; M list
echo "auth schema present: $(q "select count(*) from pg_namespace where nspname='auth'")"
echo "auth policies: $(q "select count(*) from pg_policies where schemaname='auth'")"
echo "owner default ACL entries: $(q "select count(*) from pg_default_acl where defaclrole=(select oid from pg_roles where rolname=current_user)")"
echo "app roles with CONNECT: $(q "select count(*) from pg_roles where rolname in ('${DB}_admin_api','${DB}_login') and has_database_privilege(rolname, current_database(), 'CONNECT')")"
echo "history rows: $(q 'select count(*) from "__EFMigrationsHistory"')"
step "A4 runner: update (re-apply) -> list"
M update; M list
step "A5 seed (twice: idempotent) + checks on runner-applied DB"
psql -X -q -v ON_ERROR_STOP=1 -f ../db/seed/s2a-test-app-user.sql
psql -X -q -v ON_ERROR_STOP=1 -f ../db/seed/s2a-test-app-user.sql
echo "app_user rows: $(q 'select count(*) from auth.app_user')"
C | tail -1

step "B script (idempotent) -> db/migrations.sql; deterministic"
M script ../db/migrations.sql
M script /tmp/sse-llc1/migrations.sql
md5sum ../db/migrations.sql /tmp/sse-llc1/migrations.sql

step "C1 rebuild $DB; missing role -> script aborts, nothing applied"
$FFDB rebuild $DB | grep -v -i password
envs
sudo -u postgres psql -X -q -c "ALTER ROLE ${DB}_login RENAME TO ${DB}_login_renamed_probe"
if psql -X -q -v ON_ERROR_STOP=1 -f ../db/migrations.sql >/tmp/sse-llc1/run/missing-role.out 2>&1; then echo "UNEXPECTED: applied without role"; exit 1; fi
grep -o 'S2a: app roles [^;]*' /tmp/sse-llc1/run/missing-role.out | head -1
echo "auth schema present after failed apply: $(q "select count(*) from pg_namespace where nspname='auth'")"
sudo -u postgres psql -X -q -c "ALTER ROLE ${DB}_login_renamed_probe RENAME TO ${DB}_login"
Auth.Data/tools/create-app-roles.sh $DB
step "C2 apply db/migrations.sql twice on the fresh DB"
psql -X -q -v ON_ERROR_STOP=1 -f ../db/migrations.sql >/tmp/sse-llc1/run/apply1.out 2>&1 && echo "apply #1: exit 0"
psql -X -q -v ON_ERROR_STOP=1 -f ../db/migrations.sql >/tmp/sse-llc1/run/apply2.out 2>&1 && echo "apply #2: exit 0 (no-op)"
echo "history rows: $(q 'select count(*) from "__EFMigrationsHistory"')"
echo "auth tables: $(q "select count(*) from pg_tables where schemaname='auth'")"
M list
step "C3 seed + checks on script-applied DB"
psql -X -q -v ON_ERROR_STOP=1 -f ../db/seed/s2a-test-app-user.sql
C
