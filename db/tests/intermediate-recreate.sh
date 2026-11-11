#!/usr/bin/env bash
# --force drops a marked database whose migration history is not main 18372896's.
# A history that is only those four ids is left in place. TEST-ONLY.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
KEEP=ff_kit_r11keep
INT=ff_kit_r11int
: "${FF_DB_PASSWORD:?}" "${FF_ADMIN_API_PASSWORD:?}" "${FF_LOGIN_PASSWORD:?}" "${FF_ISSUER_PASSWORD:?}"

psql_owner() {
  PGPASSWORD="$FF_DB_PASSWORD" psql -X -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -p 5432 -U "$1" -d "$1" "$@"
}
exists() {
  sudo -u postgres psql -X -tA -d postgres -c "SELECT 1 FROM pg_database WHERE datname='$1'" | grep -q 1
}
drop_if() {
  if exists "$1"; then
    bash "$HERE/setup.sh" --db "$1" --role "$1" --force --drop --no-migrate >/dev/null
  fi
}
fresh() {
  drop_if "$1"
  bash "$HERE/setup.sh" --db "$1" --role "$1" --force --no-migrate >/dev/null
}

cleanup() { drop_if "$KEEP" || true; drop_if "$INT" || true; }
trap cleanup EXIT

fresh "$KEEP"
psql_owner "$KEEP" -c "
CREATE TABLE public.\"__EFMigrationsHistory\" (\"MigrationId\" varchar(150) PRIMARY KEY, \"ProductVersion\" varchar(32) NOT NULL);
INSERT INTO public.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES
  ('20261104160000_S2aInitial', '10.0.14'),
  ('20261104170000_S2aLlcColumnUpdate', '10.0.14'),
  ('20261105080000_S2aPartnerReasonBounds', '10.0.14'),
  ('20261108190000_CatalogMasterData', '10.0.14');
CREATE TABLE public.main_sentinel (id int);"
bash "$HERE/setup.sh" --db "$KEEP" --role "$KEEP" --force --no-migrate >/dev/null
kept=$(psql_owner "$KEEP" -tA -c "SELECT to_regclass('public.main_sentinel') IS NOT NULL")
[[ "$kept" == t ]] || { echo "FAIL force-recreates-an-intermediate-req064-database :: main history was recreated"; exit 1; }

fresh "$INT"
psql_owner "$INT" -c "
CREATE TABLE public.\"__EFMigrationsHistory\" (\"MigrationId\" varchar(150) PRIMARY KEY, \"ProductVersion\" varchar(32) NOT NULL);
INSERT INTO public.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES
  ('20261110290000_Req064Session', '10.0.14');
CREATE TABLE public.intermediate_sentinel (id int);"
bash "$HERE/setup.sh" --db "$INT" --role "$INT" --force --no-migrate >/dev/null
gone=$(psql_owner "$INT" -tA -c "SELECT to_regclass('public.intermediate_sentinel') IS NULL")
[[ "$gone" == t ]] || { echo "FAIL force-recreates-an-intermediate-req064-database :: intermediate history was migrated in place"; exit 1; }
echo "PASS force-recreates-an-intermediate-req064-database"
