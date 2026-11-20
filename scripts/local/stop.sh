#!/usr/bin/env bash
# Stop processes started by scripts/local/start.sh (PID files under .local-run/).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
LOCAL_DIR="${FF_LOCAL_DIR:-$ROOT/.local-run}"

stop_pid() {
  local name="$1" file="$LOCAL_DIR/$2"
  [[ -f "$file" ]] || return 0
  local pid
  pid="$(tr -d '[:space:]' < "$file")"
  if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
    kill "$pid" 2>/dev/null || true
    sleep 0.3
    if kill -0 "$pid" 2>/dev/null; then
      kill -9 "$pid" 2>/dev/null || true
    fi
    echo "stopped $name pid=$pid"
  fi
  rm -f "$file"
}

stop_pid "admin-api" "admin-api.pid"
stop_pid "issuer" "issuer.pid"
stop_pid "stand-in" "standin.pid"
# Shell is usually foreground; if a pid was recorded, stop it too
stop_pid "shell" "shell.pid"
echo "local-run services stopped (DB left running)."
