#!/usr/bin/env bash
# Start stand-in, Auth.Issuer, Admin.Api, and the admin shell on 127.0.0.1.
# Expects scripts/local/init-db.sh to have created the DB and secrets.env.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
LOCAL_DIR="${FF_LOCAL_DIR:-$ROOT/.local-run}"
SECRETS="$LOCAL_DIR/secrets.env"
[[ -f "$SECRETS" ]] || { echo "missing $SECRETS — run scripts/local/init-db.sh first" >&2; exit 2; }
# shellcheck disable=SC1090
set -a; source "$SECRETS"; set +a

if [[ "${ASPNETCORE_ENVIRONMENT:-Development}" != "Development" ]]; then
  echo "refused: ASPNETCORE_ENVIRONMENT must be Development" >&2
  exit 2
fi
export ASPNETCORE_ENVIRONMENT=Development

SHELL_PORT="${FF_SHELL_PORT:-5099}"
STANDIN_PORT="${FF_STANDIN_PORT:-18280}"
# Token port tracks the stand-in base (+1) unless explicitly overridden — Login.StandIn binds base/base+1/base+2.
STANDIN_TOKEN_PORT="${FF_STANDIN_TOKEN_PORT:-$((STANDIN_PORT + 1))}"
API_PORT="${FF_API_PORT:-18290}"
ISSUER_PORT="${FF_ISSUER_PORT:-18291}"

export ADMIN_DEV_SHELL=1
export ADMIN_DEV_SHELL_REDIRECT="http://127.0.0.1:${SHELL_PORT}/signin-oidc"
export ADMIN_SHELL_PORT="$SHELL_PORT"
export STANDIN_ALLOW=1
export STANDIN_PORT_BASE="$STANDIN_PORT"
export STANDIN_STATE_DIR="$LOCAL_DIR/standin-state"
# DEC-341: templates from login-hub; ThemeRoot = Dir/../theme → ui/theme (also copied into build output).
export STANDIN_UI_DIR="$ROOT/ui/login-hub"
export StandIn__Issuer="http://127.0.0.1:${SHELL_PORT}/standin"
export StandIn__AllowlistJson="$LOCAL_DIR/allowlist.json"
export StandIn__Fixtures="$ROOT/scripts/local/review-fixtures.json"
export ADMIN_API_ORIGIN="http://127.0.0.1:${API_PORT}"
export STANDIN_FRONT_ORIGIN="http://127.0.0.1:${STANDIN_PORT}"
export STANDIN_TOKEN_URL="http://127.0.0.1:${STANDIN_TOKEN_PORT}/standin/connect/token"

mkdir -p "$LOCAL_DIR" "$STANDIN_STATE_DIR"
umask 077
printf '%s\n' '{"entries":[{"value":"127.0.0.1/32"},{"value":"::1/128"}]}' > "$StandIn__AllowlistJson"

# Re-bind user_issuer / refresh review rows for this stand-in issuer URL (superuser; FORCE RLS)
if id postgres >/dev/null 2>&1 && [[ "${FF_DB_HOST}" == "127.0.0.1" || "${FF_DB_HOST}" == "localhost" ]]; then
  sudo -u postgres psql -p "$FF_DB_PORT" -d "$FF_DB_NAME" \
    -v ON_ERROR_STOP=1 -v iss="$StandIn__Issuer" -f "$ROOT/scripts/local/review-seed.sql" >/dev/null
else
  PGPASSWORD="${PGPASSWORD:-}" psql -h "$FF_DB_HOST" -p "$FF_DB_PORT" -U "${FF_PG_SUPERUSER:-postgres}" -d "$FF_DB_NAME" \
    -v ON_ERROR_STOP=1 -v iss="$StandIn__Issuer" -f "$ROOT/scripts/local/review-seed.sql" >/dev/null
fi

# shellcheck source=../../ui/admin-shell/own-listener.sh
source "$ROOT/ui/admin-shell/own-listener.sh"
CONTROL_PORT=$((STANDIN_PORT + 2))
require_port_free "$STANDIN_PORT" "stand-in"
require_port_free "$STANDIN_TOKEN_PORT" "stand-in token"
require_port_free "$CONTROL_PORT" "stand-in control"
require_port_free "$API_PORT" "admin API"
require_port_free "$ISSUER_PORT" "issuer"
require_port_free "$SHELL_PORT" "admin shell"
export STANDIN_FRONT_ORIGIN="http://127.0.0.1:${STANDIN_PORT}"
export STANDIN_TOKEN_URL="http://127.0.0.1:${STANDIN_TOKEN_PORT}/standin/connect/token"

dotnet build src/Login.StandIn/Login.StandIn.csproj -p:IncludeStandIn=true -v q --nologo
dotnet build src/Auth.Issuer/Auth.Issuer.csproj -v q --nologo
dotnet build src/Admin.Api/Admin.Api.csproj -v q --nologo

nohup env STANDIN_PORT_BASE="$STANDIN_PORT" \
  dotnet run --project src/Login.StandIn/Login.StandIn.csproj --no-build -p:IncludeStandIn=true \
  > "$LOCAL_DIR/standin.log" 2>&1 &
echo $! > "$LOCAL_DIR/standin.pid"
wait_own_listener "$(cat "$LOCAL_DIR/standin.pid")" "$STANDIN_PORT" "stand-in"
wait_own_listener "$(cat "$LOCAL_DIR/standin.pid")" "$STANDIN_TOKEN_PORT" "stand-in token"
curl -fsS "http://127.0.0.1:${STANDIN_PORT}/standin/.well-known/jwks" -o "$LOCAL_DIR/jwks.json"
test -s "$LOCAL_DIR/jwks.json"

# Generated passwords live under standin-<port>/fixture-secrets.json (0600).
SECRETS_SRC="$STANDIN_STATE_DIR/standin-${STANDIN_PORT}/fixture-secrets.json"
if [[ -f "$SECRETS_SRC" ]]; then
  cp -f "$SECRETS_SRC" "$LOCAL_DIR/standin-passwords.json"
  chmod 600 "$LOCAL_DIR/standin-passwords.json"
fi

export Entra__Authority=https://login.microsoftonline.com
export Entra__ClientId=00000000-0000-0000-0000-000000000001
export Entra__SecretRef='arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/local-run-AbCdEf'
export SecretRef__AllowedAccountIds=000000000000
export SecretRef__AllowedRegions=us-west-2
export BackofficeAuth__Issuer="$StandIn__Issuer"
export BackofficeAuth__IssuerUrl="http://127.0.0.1:${ISSUER_PORT}"
export ISSUER_URL="http://127.0.0.1:${ISSUER_PORT}"
unset BackofficeAuth__JwksPath ISSUER_CALLER_SECRET ISSUER_JWKS ISSUER_ISS
export Urls="http://127.0.0.1:${API_PORT}"

(
  export ISSUER_CONNECTION_STRING
  export ISSUER_ISS="$StandIn__Issuer"
  export ISSUER_JWKS="$LOCAL_DIR/jwks.json"
  exec dotnet run --project src/Auth.Issuer/Auth.Issuer.csproj --no-build -- "http://127.0.0.1:${ISSUER_PORT}"
) > "$LOCAL_DIR/issuer.log" 2>&1 &
echo $! > "$LOCAL_DIR/issuer.pid"
wait_own_listener "$(cat "$LOCAL_DIR/issuer.pid")" "$ISSUER_PORT" "issuer"

nohup dotnet run --project src/Admin.Api/Admin.Api.csproj --no-build \
  > "$LOCAL_DIR/admin-api.log" 2>&1 &
echo $! > "$LOCAL_DIR/admin-api.pid"
wait_own_listener "$(cat "$LOCAL_DIR/admin-api.pid")" "$API_PORT" "admin API"

api_code="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${API_PORT}/api/nav" || true)"
if [[ "$api_code" != "401" ]]; then
  echo "refused: Admin.Api /api/nav returned ${api_code} (expected 401)" >&2
  exit 1
fi

cat > "$LOCAL_DIR/ports.env" <<EOF
SHELL_PORT=$SHELL_PORT
STANDIN_PORT=$STANDIN_PORT
STANDIN_TOKEN_PORT=$STANDIN_TOKEN_PORT
API_PORT=$API_PORT
ISSUER_PORT=$ISSUER_PORT
ADMIN_SHELL_URL=http://127.0.0.1:${SHELL_PORT}/
STANDIN_ADMIN_URL=http://127.0.0.1:${SHELL_PORT}/standin/admin
EOF
chmod 600 "$LOCAL_DIR/ports.env"

echo "Services up on 127.0.0.1 — shell http://127.0.0.1:${SHELL_PORT}/  admin sign-in http://127.0.0.1:${SHELL_PORT}/standin/admin"
echo "Usernames: local_sa local_pa local_ca local_la (and local_la_one). Passwords in $LOCAL_DIR (0600). Use scripts/local/totp.sh <user>."
echo "Starting admin shell (foreground). Ctrl+C stops the shell only — run scripts/local/stop.sh for the rest."
export ADMIN_API_ORIGIN="http://127.0.0.1:${API_PORT}"
node ui/admin-shell/server.mjs
