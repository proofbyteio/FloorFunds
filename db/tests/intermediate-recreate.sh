#!/usr/bin/env bash
# --force drops a marked database only when its history contains one of the
# intermediate REQ-064 ids listed in db/kit/database.sql. A future main id is
# kept. A database the kit did not create is still refused. TEST-ONLY.
# Port: PGPORT, otherwise 5432 (the same default as db/setup.sh).
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PORT="${PGPORT:-5432}"
export PGPORT="$PORT"
KEEP=ff_kit_r12keep
INT=ff_kit_r12int
FOREIGN=ff_kit_r12nokit
: "${FF_DB_PASSWORD:?}" "${FF_ADMIN_API_PASSWORD:?}" "${FF_LOGIN_PASSWORD:?}" "${FF_ISSUER_PASSWORD:?}"

# Keep this list identical to the req064-intermediate-id-list block in database.sql.
IDS=(
  20261110160000_Req064AssignmentResolver
  20261110200000_Req064RoleScope
  20261110210000_Req064HistoryPartner
  20261110220000_Req064Issuer
  20261110230000_Req064Principal
  20261110240000_Req064ReasonRead
  20261110250000_Req064Definer
  20261110260000_Req064RlsPrincipal
  20261110270000_Req064LocationSave
  20261110280000_Req064RevokeTemp
  20261110290000_Req064Session
  20261110300000_Req064RevokeLogin
  20261110310000_Req064Actor
)

psql_owner() {
  PGPASSWORD="$FF_DB_PASSWORD" PGOPTIONS='-c client_min_messages=warning' psql -X -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -p "$PORT" -U "$1" -d "$1" "$@"
}
exists() {
  sudo -u postgres psql -X -tA -d postgres -c "SELECT 1 FROM pg_database WHERE datname='$1'" | grep -q 1
}
drop_if() {
  if exists "$1"; then
    bash "$HERE/setup.sh" --db "$1" --role "$1" --port "$PORT" --force --drop --no-migrate >/dev/null
  fi
}
fresh() {
  drop_if "$1"
  bash "$HERE/setup.sh" --db "$1" --role "$1" --port "$PORT" --force --no-migrate >/dev/null
}

cleanup() {
  drop_if "$KEEP" || true
  drop_if "$INT" || true
  sudo -u postgres env PGOPTIONS='-c client_min_messages=warning' psql -X -q -d postgres -c "DROP DATABASE IF EXISTS $FOREIGN WITH (FORCE)" >/dev/null || true
}
trap cleanup EXIT

fail=0
main_ok=0

fresh "$KEEP"
psql_owner "$KEEP" -c "
CREATE TABLE public.\"__EFMigrationsHistory\" (\"MigrationId\" varchar(150) PRIMARY KEY, \"ProductVersion\" varchar(32) NOT NULL);
INSERT INTO public.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES
  ('20261104160000_S2aInitial', '10.0.14'),
  ('20261104170000_S2aLlcColumnUpdate', '10.0.14'),
  ('20261105080000_S2aPartnerReasonBounds', '10.0.14'),
  ('20261108190000_CatalogMasterData', '10.0.14');
CREATE TABLE public.main_sentinel (id int);"
bash "$HERE/setup.sh" --db "$KEEP" --role "$KEEP" --port "$PORT" --force --no-migrate >/dev/null
kept=$(psql_owner "$KEEP" -tA -c "SELECT to_regclass('public.main_sentinel') IS NOT NULL")
if [[ "$kept" == t ]]; then
  main_ok=1
else
  echo "FAIL force-recreates-an-intermediate-req064-database :: main history was recreated"
  fail=1
fi

psql_owner "$KEEP" -c "
INSERT INTO public.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\")
VALUES ('20261201000000_FutureMain', '10.0.14');
CREATE TABLE public.future_sentinel (id int);"
bash "$HERE/setup.sh" --db "$KEEP" --role "$KEEP" --port "$PORT" --force --no-migrate >/dev/null
future=$(psql_owner "$KEEP" -tA -c "SELECT to_regclass('public.future_sentinel') IS NOT NULL")
if [[ "$future" == t ]]; then
  echo "PASS future-main-id-is-kept"
else
  echo "FAIL future-main-id-is-kept :: future main id was dropped"
  fail=1
fi

fresh "$INT"
missed=""
for id in "${IDS[@]}"; do
  psql_owner "$INT" -c "
DROP TABLE IF EXISTS public.\"__EFMigrationsHistory\";
CREATE TABLE public.\"__EFMigrationsHistory\" (\"MigrationId\" varchar(150) PRIMARY KEY, \"ProductVersion\" varchar(32) NOT NULL);
INSERT INTO public.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('$id', '10.0.14');
DROP TABLE IF EXISTS public.intermediate_sentinel;
CREATE TABLE public.intermediate_sentinel (id int);"
  bash "$HERE/setup.sh" --db "$INT" --role "$INT" --port "$PORT" --force --no-migrate >/dev/null
  gone=$(psql_owner "$INT" -tA -c "SELECT to_regclass('public.intermediate_sentinel') IS NULL")
  if [[ "$gone" != t ]]; then
    missed="$missed $id"
  fi
done
if [[ -z "$missed" && "$main_ok" -eq 1 ]]; then
  echo "PASS each-intermediate-req064-id-is-recreated"
  echo "PASS force-recreates-an-intermediate-req064-database"
elif [[ -z "$missed" ]]; then
  echo "PASS each-intermediate-req064-id-is-recreated"
else
  echo "FAIL each-intermediate-req064-id-is-recreated ::$missed"
  echo "FAIL force-recreates-an-intermediate-req064-database :: intermediate history was migrated in place"
  fail=1
fi

sudo -u postgres env PGOPTIONS='-c client_min_messages=warning' psql -X -q -v ON_ERROR_STOP=1 -d postgres -c "DROP DATABASE IF EXISTS $FOREIGN WITH (FORCE)" >/dev/null
sudo -u postgres env PGOPTIONS='-c client_min_messages=warning' psql -X -q -v ON_ERROR_STOP=1 -d postgres -c "CREATE DATABASE $FOREIGN OWNER postgres" >/dev/null
set +e
bash "$HERE/setup.sh" --db "$FOREIGN" --role "$FOREIGN" --port "$PORT" --force --no-migrate >/tmp/req064-r12-nokit.log 2>&1
nokit_rc=$?
set -e
if [[ "$nokit_rc" -ne 0 ]] && exists "$FOREIGN"; then
  echo "PASS non-kit-database-is-refused"
else
  echo "FAIL non-kit-database-is-refused :: rc $nokit_rc"
  fail=1
fi

exit "$fail"
