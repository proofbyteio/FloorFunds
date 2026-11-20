#!/usr/bin/env bash
# Print a current TOTP code for a local-run stand-in user (control port only).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
LOCAL_DIR="${FF_LOCAL_DIR:-$ROOT/.local-run}"
USER="${1:-}"
[[ -n "$USER" ]] || { echo "Usage: scripts/local/totp.sh <username>" >&2; exit 2; }

STANDIN_PORT=18280
if [[ -f "$LOCAL_DIR/ports.env" ]]; then
  # shellcheck disable=SC1090
  source "$LOCAL_DIR/ports.env"
fi
CONTROL_PORT=$((STANDIN_PORT + 2))

code_json="$(curl -fsS "http://127.0.0.1:${CONTROL_PORT}/_standin/users/${USER}/totp")"
# Prefer jq if present; else python
if command -v jq >/dev/null; then
  echo "$code_json" | jq -r '.code // .error'
else
  python3 -c 'import json,sys; o=json.load(sys.stdin); print(o.get("code") or o.get("error") or "")' <<<"$code_json"
fi
