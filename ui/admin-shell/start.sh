#!/bin/bash
# One start script for the dev-only admin shell. Loopback only.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
PORT=5088
if [[ "${ASPNETCORE_ENVIRONMENT:-Development}" != "Development" ]]; then
  echo "admin shell refused to start: ASPNETCORE_ENVIRONMENT must be Development" >&2
  exit 2
fi
export ASPNETCORE_ENVIRONMENT=Development
export ADMIN_DEV_SHELL=1
export ADMIN_DEV_SHELL_REDIRECT="http://127.0.0.1:${PORT}/signin-oidc"
export ADMIN_SHELL_PORT="$PORT"
export STANDIN_ALLOW=1
export STANDIN_PORT_BASE=18080
export STANDIN_STATE_DIR=/tmp/ff-db/admin-shell-standin
export StandIn__Issuer="http://127.0.0.1:${PORT}/standin"
export StandIn__AllowlistJson=/tmp/ff-db/admin-shell-allowlist.json
export ADMIN_API_ORIGIN=http://127.0.0.1:18090
export STANDIN_FRONT_ORIGIN=http://127.0.0.1:18080
export STANDIN_TOKEN_URL=http://127.0.0.1:18081/standin/connect/token
mkdir -p /tmp/ff-db "$STANDIN_STATE_DIR"
python3 - << 'PY'
import os, pathlib, subprocess
text = pathlib.Path("/tmp/req064-env.sh").read_text()
vals = {}
for line in text.splitlines():
    if "=" not in line:
        continue
    key, value = line.split("=", 1)
    key = key.replace("export", "").strip()
    vals[key] = value.strip().strip("'").strip('"')
owner = vals["CONNECTION_STRING"]
def field(cs, name):
    for part in cs.split(";"):
        if part.lower().startswith(name.lower() + "="):
            return part.split("=", 1)[1]
    raise SystemExit("connection string is missing " + name)
sql_src = pathlib.Path("src/Auth.Data/Migrations/Req064Assignment.sql").read_text()
start = sql_src.index("CREATE FUNCTION auth.list_existing_partners")
end = sql_src.index("REVOKE ALL ON FUNCTION auth.list_existing_partners()", start)
role = field(owner, "Database") + "_admin_api"
fn = sql_src[start:end].replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", 1)
fn += "REVOKE ALL ON FUNCTION auth.list_existing_partners() FROM PUBLIC;\n"
fn += 'GRANT EXECUTE ON FUNCTION auth.list_existing_partners() TO "' + role.replace('"', '""') + '";\n'
fn += """
INSERT INTO auth.admin_assignment (assignment_id, user_id, role_id, scope_kind, status, version, granted_by, granted_at)
SELECT '5f0c3e1a-0064-4000-8000-0000000000d1', u.user_id, 'super_admin', 'global', 'active', 1, u.user_id, now()
  FROM auth.app_user u
 WHERE u.user_id = '5f0c3e1a-0003-4000-8000-000000000c0a' AND u.status = 'active'
   AND NOT EXISTS (
     SELECT 1 FROM auth.admin_assignment a
      WHERE a.user_id = u.user_id AND a.role_id = 'super_admin' AND a.scope_kind = 'global'
        AND a.status = 'active' AND a.revoked_at IS NULL);
"""
env = os.environ.copy()
env["PGPASSWORD"] = field(owner, "Password")
proc = subprocess.run(["psql", "-h", field(owner, "Host"), "-p", field(owner, "Port") or "5432", "-U", field(owner, "Username"), "-d", field(owner, "Database"), "-v", "ON_ERROR_STOP=1", "-q"], input=fn, text=True, env=env, capture_output=True)
if proc.returncode != 0:
    raise SystemExit("admin shell database setup failed")
check = subprocess.run(["psql", "-h", field(owner, "Host"), "-p", field(owner, "Port") or "5432", "-U", field(owner, "Username"), "-d", field(owner, "Database"), "-At", "-c",
    "SELECT count(*) FROM auth.app_user WHERE user_id = '5f0c3e1a-0003-4000-8000-000000000c0a' AND status = 'active'"],
    text=True, env=env, capture_output=True)
if check.stdout.strip() != "1":
    raise SystemExit("fixture super_admin is not an active auth.app_user")
print("database setup ok")
PY
printf '%s\n' '{"entries":[{"value":"127.0.0.1/32"},{"value":"::1/128"}]}' > "$StandIn__AllowlistJson"
dotnet build src/Login.StandIn/Login.StandIn.csproj -p:IncludeStandIn=true -v q --nologo
dotnet build src/Admin.Api/Admin.Api.csproj -v q --nologo
nohup dotnet run --project src/Login.StandIn/Login.StandIn.csproj --no-build -p:IncludeStandIn=true > /tmp/ff-db/admin-shell-standin.log 2>&1 &
echo $! > /tmp/ff-db/admin-shell-standin.pid
for _ in $(seq 1 80); do
  if curl -fsS "http://127.0.0.1:18080/standin/.well-known/jwks" -o /tmp/ff-db/admin-shell-jwks.json; then
    break
  fi
  sleep 0.25
done
if ! kill -0 "$(cat /tmp/ff-db/admin-shell-standin.pid)" 2>/dev/null; then
  echo "admin shell refused to start: the stand-in exited" >&2
  exit 1
fi
test -s /tmp/ff-db/admin-shell-jwks.json
set -a
# shellcheck disable=SC1091
source /tmp/req064-env.sh
set +a
export Entra__Authority=https://login.microsoftonline.com
export Entra__ClientId=00000000-0000-0000-0000-000000000001
export Entra__SecretRef='arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/admin-shell-AbCdEf'
export SecretRef__AllowedAccountIds=000000000000
export SecretRef__AllowedRegions=us-west-2
export BackofficeAuth__Issuer="http://127.0.0.1:${PORT}/standin"
export BackofficeAuth__JwksPath=/tmp/ff-db/admin-shell-jwks.json
export Urls=http://127.0.0.1:18090
nohup dotnet run --project src/Admin.Api/Admin.Api.csproj --no-build > /tmp/ff-db/admin-shell-api.log 2>&1 &
echo $! > /tmp/ff-db/admin-shell-api.pid
ok=0
for _ in $(seq 1 80); do
  code="$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18090/api/nav || true)"
  if [[ "$code" == "401" ]]; then ok=1; break; fi
  sleep 0.25
done
test "$ok" = 1
node ui/admin-shell/server.mjs
