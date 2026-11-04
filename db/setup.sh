#!/usr/bin/env bash
# FloorFunds dev DB setup (see db/README.md). Idempotent. Never prints a password.
#   1. owner login role + a database it owns (superuser step)
#   2. the two S2a app roles <db>_admin_api and <db>_login (superuser step; spec §8.2)
#   3. db/migrations.sql applied AS THE OWNER (idempotent script from the EF runner;
#      the only way schema/tables are created) — skip with --no-migrate
#   4. optional TEST-ONLY seed db/seed/*.sql (--seed; SSE2-FAKE-TEST-ONLY data)
set -euo pipefail

DB=floorfunds_dev
ROLE=floorfunds_dev
PASSWORD="${FF_DB_PASSWORD:-}"
ADMIN_API_PASSWORD="${FF_ADMIN_API_PASSWORD:-}"
LOGIN_PASSWORD="${FF_LOGIN_PASSWORD:-}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MIGRATIONS="$HERE/migrations.sql"
MIGRATE=1
SEED=0
HOST=127.0.0.1
PORT=5432
ADMIN_HOST=""   # empty = local Unix socket (peer auth for sudo -u postgres)
DROP=0
SUPERUSER_CMD_DEFAULT="sudo -u postgres psql"
SUPERUSER_CMD="$SUPERUSER_CMD_DEFAULT"

usage() {
  cat <<USAGE
Usage: db/setup.sh [--db NAME] [--role NAME] [--password PW] [--host H] [--port P] [--admin-host H]
                   [--superuser-cmd "CMD"] [--drop] [--no-migrate] [--migrations FILE] [--seed]
  Owner password: --password, else env FF_DB_PASSWORD, else prompted (hidden).
  App-role passwords (<db>_admin_api, <db>_login): env FF_ADMIN_API_PASSWORD and
                   FF_LOGIN_PASSWORD, else prompted (hidden).
  --superuser-cmd  command that runs psql as a Postgres superuser
                   (default: "$SUPERUSER_CMD_DEFAULT"; macOS Homebrew: "psql").
  --host           host written into the connection string (default 127.0.0.1)
  --admin-host     host the superuser psql connects to (default: local socket)
  --drop           drop and recreate ONLY the named database.
  --no-migrate     stop after the roles; do not apply db/migrations.sql
  --migrations F   script to apply (default: db/migrations.sql next to this file)
  --seed           also load db/seed/*.sql (TEST-ONLY fake data; never on a real DB)
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --db) DB="$2"; shift 2 ;;
    --role) ROLE="$2"; shift 2 ;;
    --password) PASSWORD="$2"; shift 2 ;;
    --host) HOST="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --admin-host) ADMIN_HOST="$2"; shift 2 ;;
    --superuser-cmd) SUPERUSER_CMD="$2"; shift 2 ;;
    --drop) DROP=1; shift ;;
    --no-migrate) MIGRATE=0; shift ;;
    --migrations) MIGRATIONS="$2"; shift 2 ;;
    --seed) SEED=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

NAME_RE='^[a-z_][a-z0-9_]{0,62}$'
[[ "$DB" =~ $NAME_RE ]]   || { echo "Invalid --db name: must match $NAME_RE" >&2; exit 2; }
[[ "$ROLE" =~ $NAME_RE ]] || { echo "Invalid --role name: must match $NAME_RE" >&2; exit 2; }
[[ "$PORT" =~ ^[0-9]{1,5}$ ]] || { echo "Invalid --port" >&2; exit 2; }
# <db>_admin_api must still fit in 63 bytes.
(( ${#DB} <= 53 )) || { echo "Invalid --db name: at most 53 characters (app role <db>_admin_api must fit)" >&2; exit 2; }
ADMIN_API_ROLE="${DB}_admin_api"; LOGIN_ROLE="${DB}_login"
[[ "$ROLE" != "$ADMIN_API_ROLE" && "$ROLE" != "$LOGIN_ROLE" ]] || { echo "Invalid --role: must not be an app role name" >&2; exit 2; }
if [[ "$MIGRATE" -eq 1 ]]; then
  [[ -r "$MIGRATIONS" ]] || { echo "Migrations script not found: $MIGRATIONS" >&2; exit 2; }
  command -v psql >/dev/null || { echo "psql not found on PATH (needed to apply migrations as the owner)" >&2; exit 3; }
fi
[[ "$SEED" -eq 0 || "$MIGRATE" -eq 1 ]] || { echo "--seed needs the migrations (drop --no-migrate)" >&2; exit 2; }

ask() { # ask VAR "prompt": hidden prompt if VAR is empty
  local __v="$1"
  if [[ -z "${!__v}" ]]; then
    local __in; read -r -s -p "$2: " __in; echo >&2
    [[ -n "$__in" ]] || { echo "Empty password refused" >&2; exit 2; }
    printf -v "$__v" '%s' "$__in"
  fi
}
ask PASSWORD "Password for role $ROLE"
ask ADMIN_API_PASSWORD "Password for app role $ADMIN_API_ROLE"
ask LOGIN_PASSWORD "Password for app role $LOGIN_ROLE"

# shellcheck disable=SC2206
PSQL=($SUPERUSER_CMD)
run_psql() {
  # Values are passed as psql variables; SQL uses format(%I/%L) + \gexec.
  # The password travels on stdin (not argv) so it is not visible in ps.
  local hostarg=()
  [[ -n "$ADMIN_HOST" ]] && hostarg=(-h "$ADMIN_HOST")
  "${PSQL[@]}" -X -q -v ON_ERROR_STOP=1 "${hostarg[@]}" -p "$PORT" -d postgres \
    -v db="$DB" -v role="$ROLE" "$@"
}

# upsert_role ROLE PASSWORD: plain login role (no superuser, no BYPASSRLS, no CREATEDB/CREATEROLE/REPLICATION).
upsert_role() {
  echo "Role $1: create or update..."
  {
    printf '\\set r %s\n' "'$1'"
    printf '\\set pw %s\n' "'$(printf '%s' "$2" | sed "s/'/''/g; s/\\\\/\\\\\\\\/g")'"
    cat <<'SQL'
SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', :'r', :'pw')
  WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'r') \gexec
SELECT format('ALTER ROLE %I LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', :'r', :'pw') \gexec
SQL
  } | run_psql -f -
}
upsert_role "$ROLE" "$PASSWORD"

if [[ "$DROP" -eq 1 ]]; then
  echo "Dropping database $DB (only this database)..."
  run_psql <<'SQL'
SELECT format('DROP DATABASE IF EXISTS %I WITH (FORCE)', :'db') \gexec
SQL
fi

echo "Database $DB: create if missing..."
run_psql <<'SQL'
SELECT format('CREATE DATABASE %I OWNER %I', :'db', :'role')
  WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db') \gexec
SELECT format('ALTER DATABASE %I OWNER TO %I', :'db', :'role') \gexec
SELECT format('REVOKE CONNECT ON DATABASE %I FROM PUBLIC', :'db') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'db', :'role') \gexec
SQL

# S2a app roles (spec §8.2). The migration grants CONNECT/USAGE/table rights to them and
# RAISEs if either is missing or privileged.
upsert_role "$ADMIN_API_ROLE" "$ADMIN_API_PASSWORD"
upsert_role "$LOGIN_ROLE" "$LOGIN_PASSWORD"

owner_psql() { # as the owner over TCP; the password goes in the child's env, never argv
  PGPASSWORD="$PASSWORD" psql -X -q -v ON_ERROR_STOP=1 -h "$HOST" -p "$PORT" -U "$ROLE" -d "$DB" "$@"
}
if [[ "$MIGRATE" -eq 1 ]]; then
  echo "Migrations: applying $(basename "$MIGRATIONS") as $ROLE (idempotent)..."
  # client_min_messages=warning hides the expected "__EFMigrationsHistory already exists" NOTICE on re-runs.
  PGOPTIONS='-c client_min_messages=warning' owner_psql -f "$MIGRATIONS" || { echo "Migrations failed (see the psql error above)" >&2; exit 4; }
  echo "Applied migrations: $(owner_psql -tA -c 'SELECT string_agg("MigrationId", '"', '"' ORDER BY "MigrationId") FROM public."__EFMigrationsHistory"')"
fi
if [[ "$SEED" -eq 1 ]]; then
  for f in "$HERE"/seed/*.sql; do
    echo "Seed (TEST-ONLY): $(basename "$f")..."
    owner_psql -f "$f"
  done
fi

cat <<DONE

Done. Migrations: $([[ $MIGRATE -eq 1 ]] && echo applied || echo "NOT applied (--no-migrate)"). Seed: $([[ $SEED -eq 1 ]] && echo "loaded (TEST-ONLY)" || echo "not loaded").

Connection string templates (Npgsql); passwords are the ones you entered:
  owner:     Host=$HOST;Port=$PORT;Database=$DB;Username=$ROLE;Password=<password>
  admin API: Host=$HOST;Port=$PORT;Database=$DB;Username=$ADMIN_API_ROLE;Password=<password>
  login:     Host=$HOST;Port=$PORT;Database=$DB;Username=$LOGIN_ROLE;Password=<password>

Set them outside the repo, as env vars (see db/README.md, "Run and test"):
  CONNECTION_STRING (owner), ADMIN_API_CONNECTION_STRING, LOGIN_CONNECTION_STRING
DONE
