#!/usr/bin/env bash
# FloorFunds dev DB setup (see db/README.md). Never prints a password. Exit: 0 ok, 2 bad args, 3 psql missing,
# 4 migrations failed, 5 refused (nothing changed) or a kit SQL guard aborted.
#   0. read-only preflight (db/kit/preflight.sql) BEFORE any change: refuses postgres/template DBs, superusers,
#      and any existing role or DB the kit did not create (no 'floorfunds-kit' marker); --force never overrides
#      that; nor kit roles with BYPASSRLS/CREATEROLE/CREATEDB/REPLICATION or any role membership outside the kit
#      (KF1); output without the preflight's end marker is a refusal (KF3). Re-running on the kit's own (marked) objects needs --force (K1, TL 8:32).
#   1. owner login role + a database it owns (superuser step; created objects are marked 'floorfunds-kit')
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
FORCE=0
SUPERUSER_CMD_DEFAULT="sudo -u postgres psql"
SUPERUSER_CMD="$SUPERUSER_CMD_DEFAULT"

usage() {
  cat <<USAGE
Usage: db/setup.sh [--db NAME] [--role NAME] [--password PW] [--host H] [--port P] [--admin-host H]
                   [--superuser-cmd "CMD"] [--force] [--drop] [--no-migrate] [--migrations FILE] [--seed]
  Owner password: --password, else env FF_DB_PASSWORD, else prompted (hidden).
  App-role passwords (<db>_admin_api, <db>_login): env FF_ADMIN_API_PASSWORD and
                   FF_LOGIN_PASSWORD, else prompted (hidden).
  --superuser-cmd  command that runs psql as a Postgres superuser
                   (default: "$SUPERUSER_CMD_DEFAULT"; macOS Homebrew: "psql").
  --host           host written into the connection string (default 127.0.0.1)
  --admin-host     host the superuser psql connects to (default: local socket)
  --force          proceed when the kit's own (marked) DB/roles already exist: re-set their
                   passwords and grants. Never acts on objects the kit did not create.
  --drop           drop and recreate ONLY the named database; needs --force, and only a
                   kit-marked DB owned by --role is ever dropped.
  Always refused (even with --force): --db postgres/template0/template1 or any template DB,
  any superuser role, any existing role or DB without the kit marker, a DB owned by
  another role, a kit role that owns another DB, a kit role with BYPASSRLS/CREATEROLE/CREATEDB/
  REPLICATION or a member of any role outside the kit, or a preflight without its end marker.
  Exit 5, before anything is changed.
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
    --force) FORCE=1; shift ;;
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
[[ "$DROP" -eq 0 || "$FORCE" -eq 1 ]] || { echo "Refused: --drop needs --force (and drops only a kit-marked DB owned by --role). Nothing was changed." >&2; exit 2; }
KIT="$HERE/kit"
for f in preflight.sql role.sql database.sql; do [[ -r "$KIT/$f" ]] || { echo "Kit SQL not found: $KIT/$f" >&2; exit 2; }; done
# K1 hard refusals by name (no SQL at all): --force never overrides these.
refuse() { echo "Refused: $1. Nothing was changed." >&2; exit 5; }
case "$DB" in postgres|template0|template1) refuse "--db $DB is a system database" ;; esac
for r in "$ROLE" "$ADMIN_API_ROLE" "$LOGIN_ROLE"; do
  [[ "$r" != postgres ]] || refuse "role postgres is the bootstrap superuser"
  [[ "$r" != pg_* ]] || refuse "role $r: the pg_ prefix is reserved"
done

ask() { # ask VAR "prompt": hidden prompt if VAR is empty
  local __v="$1"
  if [[ -z "${!__v}" ]]; then
    local __in; read -r -s -p "$2: " __in; echo >&2
    [[ -n "$__in" ]] || { echo "Empty password refused" >&2; exit 2; }
    printf -v "$__v" '%s' "$__in"
  fi
  # K5: a newline/CR would break the psql \set the password travels in.
  [[ "${!__v}" != *$'\n'* && "${!__v}" != *$'\r'* ]] || { echo "Refused: the password for $3 contains a newline or carriage return. Nothing was changed." >&2; exit 2; }
  # KF-A: the kit sends Postgres a SCRAM-SHA-256 verifier, never the password. SASLprep (RFC 4013) is the identity
  # only for printable ASCII, so other characters are refused rather than hashed differently from libpq at login.
  local LC_ALL=C; [[ "${!__v}" =~ ^[\ -~]+$ ]] || { echo "Refused: the password for $3 must be printable ASCII (space to ~). Nothing was changed." >&2; exit 2; }
}
ask PASSWORD "Password for role $ROLE" "role $ROLE"
ask ADMIN_API_PASSWORD "Password for app role $ADMIN_API_ROLE" "app role $ADMIN_API_ROLE"
ask LOGIN_PASSWORD "Password for app role $LOGIN_ROLE" "app role $LOGIN_ROLE"

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

command -v perl >/dev/null || { echo "perl not found on PATH (needed to hash the passwords client-side)" >&2; exit 3; }
scram() { # KF-A: password on stdin -> SCRAM-SHA-256 verifier (RFC 5802/7677, 4096 iterations, 16-byte random salt),
  # the same format libpq's PQencryptPasswordConn produces. Only the verifier is ever sent to the server.
  perl -MDigest::SHA=hmac_sha256,sha256 -MMIME::Base64=encode_base64 -e '
    local $/; my $pw = <STDIN>; open(my $r, "<:raw", "/dev/urandom") or die; read($r, my $salt, 16) == 16 or die;
    my $u = hmac_sha256($salt . pack("N", 1), $pw); my $h = $u;
    for (2 .. 4096) { $u = hmac_sha256($u, $pw); $h ^= $u; }
    my $b = sub { encode_base64($_[0], "") };
    print "SCRAM-SHA-256\$4096:", $b->($salt), "\$", $b->(sha256(hmac_sha256("Client Key", $h))), ":", $b->(hmac_sha256("Server Key", $h));'
}
pw_set() { # \set NAME 'verifier' (base64 + $ : only, so no psql escaping is needed)
  local v; v="$(printf '%s' "$2" | scram)" || { echo "Password hashing failed. Nothing more was changed." >&2; exit 5; }
  printf '\\set %s %s\n' "$1" "'$v'"
}

# 0. K1 preflight: read-only, before anything is changed.
echo "Preflight (read-only): $DB, $ROLE, $ADMIN_API_ROLE, $LOGIN_ROLE..."
PRE="$(run_psql -tA -v api_role="$ADMIN_API_ROLE" -v login_role="$LOGIN_ROLE" -f - <"$KIT/preflight.sql")" \
  || { echo "Preflight failed (see the psql error above). Nothing was changed." >&2; exit 5; }
# KF3: fail closed. The preflight always ends with this line; empty or truncated output is a refusal.
if [[ "$(tail -n 1 <<<"$PRE")" != "END floorfunds-kit-preflight" ]]; then
  echo "Refused: the preflight returned no end marker (empty or incomplete output), so the catalog could not be checked. Nothing was changed." >&2; exit 5
fi
if grep -q '^REFUSE ' <<<"$PRE"; then
  sed -n 's/^REFUSE /Refused: /p' <<<"$PRE" >&2
  echo "Refused (--force never overrides this). Nothing was changed." >&2; exit 5
fi
if grep -q '^EXISTS ' <<<"$PRE" && [[ "$FORCE" -eq 0 ]]; then
  sed -n 's/^EXISTS /Already exists (created by the kit): /p' <<<"$PRE" >&2
  echo "Refused: re-run with --force to update the kit's own objects (add --drop to recreate the DB). Nothing was changed." >&2; exit 5
fi

# upsert_role ROLE PASSWORD: plain login role (no superuser, no BYPASSRLS, no CREATEDB/CREATEROLE/REPLICATION);
# creates + marks it, or updates it only if it carries the kit marker (db/kit/role.sql).
upsert_role() {
  echo "Role $1: create or update (kit-marked only)..."
  { printf '\\set r %s\n' "'$1'"; pw_set p "$2"; cat "$KIT/role.sql"; } | run_psql -f - \
    || { echo "Role $1: kit SQL step aborted (see the psql error above)." >&2; exit 5; }
}
upsert_role "$ROLE" "$PASSWORD"

if [[ "$DROP" -eq 1 ]]; then echo "Dropping database $DB (only this database, only if kit-marked)..."; fi
echo "Database $DB: create if missing (kit-marked only)..."
run_psql -v drop="$DROP" -f - <"$KIT/database.sql" \
  || { echo "Database $DB: kit SQL step aborted (see the psql error above)." >&2; exit 5; }

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
