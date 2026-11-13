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
# Synthetic disposable database. Passwords are generated here. This script does not
# read /tmp/req064-env.sh or any other file outside the repo. The fixture Super Admin
# assignment is not inserted here; that is ui/admin-shell/seed-dev-assignment.sh.
DB_NAME=ff_kit_adminshell
DB_HOST=127.0.0.1
DB_PORT=5432
gen_pw() { od -An -N18 -tx1 /dev/urandom | tr -d ' \n'; }
OWNER_PW="$(gen_pw)"
ADMIN_PW="$(gen_pw)"
LOGIN_PW="$(gen_pw)"
ISSUER_PW="$(gen_pw)"
export FF_DB_PASSWORD="$OWNER_PW" FF_ADMIN_API_PASSWORD="$ADMIN_PW" FF_LOGIN_PASSWORD="$LOGIN_PW"
# Synthetic owner-test database only. A database left from 67d2546 or 9ebb0eb is
# dropped and created again, then seeded. Any other name or host is refused.
if [[ "$DB_NAME" != "ff_kit_adminshell" || "$DB_HOST" != "127.0.0.1" ]]; then
  echo "admin shell refused to start: the owner-test database must be the synthetic loopback database ff_kit_adminshell" >&2
  exit 2
fi
kit_state="$(sudo -u postgres psql -tA -c "SELECT CASE
  WHEN EXISTS (
    SELECT 1 FROM pg_database d
     WHERE d.datname = 'ff_kit_adminshell' AND NOT d.datistemplate
       AND shobj_description(d.oid, 'pg_database') = 'floorfunds-kit'
  ) THEN 'marked'
  WHEN EXISTS (SELECT 1 FROM pg_database d WHERE d.datname = 'ff_kit_adminshell') THEN 'foreign'
  ELSE 'absent'
END")"
case "$kit_state" in
  foreign)
    echo "admin shell refused to start: ff_kit_adminshell exists and is not a synthetic kit database" >&2
    exit 2
    ;;
  marked)
    echo "Dropping synthetic database ff_kit_adminshell so a database migrated at an older tip does not survive"
    FF_ISSUER_PASSWORD="$ISSUER_PW" bash db/setup.sh --db "$DB_NAME" --role "$DB_NAME" --host "$DB_HOST" --port "$DB_PORT" --force --drop --no-migrate
    ;;
  absent)
    echo "Synthetic database ff_kit_adminshell is absent; it will be created from the current migrations"
    ;;
  *)
    echo "admin shell refused to start: could not read whether ff_kit_adminshell is a synthetic kit database" >&2
    exit 2
    ;;
esac
echo "Creating synthetic database ff_kit_adminshell from the current migrations and loading the seed"
FF_ISSUER_PASSWORD="$ISSUER_PW" bash db/setup.sh --db "$DB_NAME" --role "$DB_NAME" --host "$DB_HOST" --port "$DB_PORT" --force --seed
export CONNECTION_STRING="Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_NAME};Password=${OWNER_PW}"
export ADMIN_API_CONNECTION_STRING="Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_NAME}_admin_api;Password=${ADMIN_PW}"
export LOGIN_CONNECTION_STRING="Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_NAME}_login;Password=${LOGIN_PW}"
export AUTH_CHECKS_DB="$DB_NAME"
umask 077
: > /tmp/ff-db/adminshell.env
chmod 600 /tmp/ff-db/adminshell.env
cat > /tmp/ff-db/adminshell.env <<EOF
CONNECTION_STRING='${CONNECTION_STRING}'
ADMIN_API_CONNECTION_STRING='${ADMIN_API_CONNECTION_STRING}'
LOGIN_CONNECTION_STRING='${LOGIN_CONNECTION_STRING}'
AUTH_CHECKS_DB='${AUTH_CHECKS_DB}'
EOF
echo "disposable database ${DB_NAME} on ${DB_HOST}:${DB_PORT}"
printf '%s\n' '{"entries":[{"value":"127.0.0.1/32"},{"value":"::1/128"}]}' > "$StandIn__AllowlistJson"
dotnet build src/Login.StandIn/Login.StandIn.csproj -p:IncludeStandIn=true -v q --nologo
dotnet build src/Auth.Issuer/Auth.Issuer.csproj -v q --nologo
dotnet build src/Admin.Api/Admin.Api.csproj -v q --nologo
# shellcheck source=own-listener.sh
source "$(dirname "$0")/own-listener.sh"
require_port_free 18080 "stand-in"
require_port_free 18081 "stand-in token"
require_port_free 18090 "admin API"
require_port_free 18091 "issuer"
nohup dotnet run --project src/Login.StandIn/Login.StandIn.csproj --no-build -p:IncludeStandIn=true > /tmp/ff-db/admin-shell-standin.log 2>&1 &
echo $! > /tmp/ff-db/admin-shell-standin.pid
wait_own_listener "$(cat /tmp/ff-db/admin-shell-standin.pid)" 18080 "stand-in"
wait_own_listener "$(cat /tmp/ff-db/admin-shell-standin.pid)" 18081 "stand-in token"
if ! curl -fsS "http://127.0.0.1:18080/standin/.well-known/jwks" -o /tmp/ff-db/admin-shell-jwks.json; then
  echo "admin shell refused to start: stand-in pid $(cat /tmp/ff-db/admin-shell-standin.pid) owns port 18080 but did not serve its JWKS" >&2
  exit 1
fi
test -s /tmp/ff-db/admin-shell-jwks.json
export Entra__Authority=https://login.microsoftonline.com
export Entra__ClientId=00000000-0000-0000-0000-000000000001
export Entra__SecretRef='arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/admin-shell-AbCdEf'
export SecretRef__AllowedAccountIds=000000000000
export SecretRef__AllowedRegions=us-west-2
export BackofficeAuth__Issuer="http://127.0.0.1:${PORT}/standin"
export BackofficeAuth__IssuerUrl=http://127.0.0.1:18091
export ISSUER_URL=http://127.0.0.1:18091
unset BackofficeAuth__JwksPath ISSUER_CONNECTION_STRING ISSUER_CALLER_SECRET ISSUER_JWKS ISSUER_ISS FF_ISSUER_PASSWORD
export Urls=http://127.0.0.1:18090
# The issuer holds the database password and the JWKS. The host and the shell do not.
(
  export ISSUER_CONNECTION_STRING="Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_NAME}_issuer;Password=${ISSUER_PW}"
  export ISSUER_ISS="http://127.0.0.1:${PORT}/standin"
  export ISSUER_JWKS=/tmp/ff-db/admin-shell-jwks.json
  exec dotnet run --project src/Auth.Issuer/Auth.Issuer.csproj --no-build -- http://127.0.0.1:18091
) > /tmp/ff-db/admin-shell-issuer.log 2>&1 &
echo $! > /tmp/ff-db/admin-shell-issuer.pid
wait_own_listener "$(cat /tmp/ff-db/admin-shell-issuer.pid)" 18091 "issuer"
issuer_code="$(curl -s -o /dev/null -w '%{http_code}' -X POST --data 'x' http://127.0.0.1:18091/v1/session || true)"
if [[ "$issuer_code" != "401" ]]; then
  echo "admin shell refused to start: issuer pid $(cat /tmp/ff-db/admin-shell-issuer.pid) owns port 18091 but returned ${issuer_code}" >&2
  exit 1
fi
nohup dotnet run --project src/Admin.Api/Admin.Api.csproj --no-build > /tmp/ff-db/admin-shell-api.log 2>&1 &
echo $! > /tmp/ff-db/admin-shell-api.pid
wait_own_listener "$(cat /tmp/ff-db/admin-shell-api.pid)" 18090 "admin API"
api_code="$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18090/api/nav || true)"
if [[ "$api_code" != "401" ]]; then
  echo "admin shell refused to start: admin API pid $(cat /tmp/ff-db/admin-shell-api.pid) owns port 18090 but /api/nav returned ${api_code}" >&2
  exit 1
fi
echo "admin shell listeners are the stand-in, issuer, and API this script started"
node ui/admin-shell/server.mjs
