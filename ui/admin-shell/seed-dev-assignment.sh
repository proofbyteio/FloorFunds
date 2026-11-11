#!/bin/bash
# Dev-only seed. Inserts the fixture global Super Admin and exactly one fixture Partner Admin.
# Refuses unless REQ064_DEV_SEED=1 and the target is a local disposable kit database.
# Does not read /tmp/req064-env.sh. Connection comes from CONNECTION_STRING, or from
# the file ui/admin-shell/start.sh writes at /tmp/ff-db/adminshell.env.
set -euo pipefail
if [[ "${REQ064_DEV_SEED:-}" != "1" ]]; then
  echo "refused: set REQ064_DEV_SEED=1 to run this dev-only seed" >&2
  exit 2
fi
if [[ "${ASPNETCORE_ENVIRONMENT:-Development}" == "Production" ]]; then
  echo "refused: ASPNETCORE_ENVIRONMENT is Production" >&2
  exit 2
fi

cs="${CONNECTION_STRING:-}"
if [[ -z "$cs" ]]; then
  envfile=/tmp/ff-db/adminshell.env
  [[ -f "$envfile" ]] || { echo "refused: CONNECTION_STRING is unset and ${envfile} is missing" >&2; exit 2; }
  cs="$(sed -n "s/^CONNECTION_STRING='\\(.*\\)'$/\\1/p" "$envfile")"
fi
[[ -n "$cs" ]] || { echo "refused: connection string is empty" >&2; exit 2; }

field() {
  local name="$1" part
  IFS=';' read -ra parts <<< "$cs"
  for part in "${parts[@]}"; do
    if [[ "${part,,}" == "${name,,}="* ]]; then
      printf '%s' "${part#*=}"
      return
    fi
  done
  return 1
}
host="$(field Host)" || { echo "refused: connection string is missing Host" >&2; exit 2; }
port="$(field Port || true)"
port="${port:-5432}"
db="$(field Database)" || { echo "refused: connection string is missing Database" >&2; exit 2; }
user="$(field Username)" || { echo "refused: connection string is missing Username" >&2; exit 2; }
pass="$(field Password)" || { echo "refused: connection string is missing Password" >&2; exit 2; }

case "$host" in
  127.0.0.1|localhost|::1) ;;
  *) echo "refused: host ${host} is not a local disposable database" >&2; exit 2 ;;
esac
case "$port" in
  5432|5433) ;;
  *) echo "refused: port ${port} is not a local disposable database" >&2; exit 2 ;;
esac
case "$db" in
  postgres|template0|template1)
    echo "refused: ${db} is a system database" >&2
    exit 2
    ;;
esac
[[ "$db" =~ ^ff_kit_[a-z0-9_]+$ ]] || { echo "refused: database ${db} is not a disposable ff_kit_ database" >&2; exit 2; }

marker="$(PGPASSWORD="$pass" psql -X -q -tA -v ON_ERROR_STOP=1 -h "$host" -p "$port" -U "$user" -d "$db" \
  -c "SELECT coalesce(shobj_description(oid, 'pg_database'), '') FROM pg_database WHERE datname = current_database()")"
marker="${marker//$'\n'/}"
[[ "$marker" == "floorfunds-kit" ]] || { echo "refused: ${db} is not marked floorfunds-kit" >&2; exit 2; }

iss="${STANDIN_ISSUER:-http://127.0.0.1:5088/standin}"
case "$iss" in
  http://127.0.0.1:*/standin|http://localhost:*/standin) ;;
  *) echo "refused: stand-in issuer is not a loopback /standin URL" >&2; exit 2 ;;
esac
PGPASSWORD="$pass" psql -X -q -v ON_ERROR_STOP=1 -h "$host" -p "$port" -U "$user" -d "$db" -v iss="$iss" <<'SQL'
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
SQL
echo "dev seed applied on ${db}"
