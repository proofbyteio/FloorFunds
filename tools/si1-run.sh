#!/bin/bash
# Owner: SSE2. SI1 regression run against a stand-in built from a git ref, with an optional source overlay.
# TL ruling 2026-11-04 11:03 PT. Derived from SSE2's local runner (md5 0479ead6); machine paths and ports removed.
usage() {
  cat <<'USAGE'
Usage: tools/si1-run.sh [-h] TAG [OVERLAY_REF|none]

Builds the stand-in from SI1_STANDIN_REF in a temporary detached worktree, checks out this repo's tests/tools/docs
(SI1_TESTS_REF) on top, optionally overlays the Entra sources from OVERLAY_REF, starts the stand-in on loopback,
runs tools/run-checks.sh standin, then stops the stand-in and removes the worktree.

  TAG           label for this run (letters, digits, . _ -); names the results dir and the stand-in log
  OVERLAY_REF   git ref whose src/Login.Entra src/Login.Abstractions tests/Login.EntraTests tests/Login.FakeEntra
                replace the stand-in ref's copies; "none" (default) = no overlay

Environment (all optional):
  SI1_REPO          git repo that holds the refs (default: the repo this script is in)
  SI1_STANDIN_REF   ref to build the stand-in from (default: f76d18a)
  SI1_TESTS_REF     ref for tests/ tools/ docs/ README.md (default: HEAD of SI1_REPO)
  SI1_PORT_BASE     front = base, back = base+1, control = base+2, loopback only (default: 18140;
                    must be 18090-18197; 8080/8081 are always refused)
  SI1_WORK_DIR      run state: publish output, stand-in state, HOME, client keys, allowlist, pid
                    (default: a new directory from mktemp -d)
  SI1_RESULTS_DIR   test results (default: $SI1_WORK_DIR/results-$TAG)
  SI1_STANDIN_LOG   stand-in stdout/stderr log; exported to the tests as LOGIN_STANDIN_LOG
                    (default: $SI1_WORK_DIR/standin-$TAG.log)
  SI1_MOCKUPS_DIR   login-hub mockups dir for CACHD_MOCKUPS_DIR (default: unset; mockup checks then report pending)
  DOTNET_ROOT       .NET SDK root (default: $HOME/.dotnet if it exists)

Guards: ss port check before start (ports must be free) and after start (owned by our pid); the run aborts unless
LOGIN_STANDIN_LOG is fd 1 of the live stand-in process (HO17 must never read a stale log).
Exit: run-checks.sh's code, or 2 ports busy, 3 worktree, 5 publish, 6 port owner, 7 log guard, 64 usage.
USAGE
}
set -u
[ "${1:-}" = -h ] || [ "${1:-}" = --help ] && { usage; exit 0; }
[ $# -ge 1 ] && [ $# -le 2 ] || { usage >&2; exit 64; }
TAG=$1; OV=${2:-none}
[[ $TAG =~ ^[A-Za-z0-9._-]+$ ]] || { echo "bad TAG '$TAG'" >&2; exit 64; }

HERE=$(cd "$(dirname "$0")" && pwd)
REPO=${SI1_REPO:-$(git -C "$HERE" rev-parse --show-toplevel)}
STANDIN_REF=${SI1_STANDIN_REF:-f76d18a}
TESTS_REF=${SI1_TESTS_REF:-$(git -C "$REPO" rev-parse HEAD)}
P=${SI1_PORT_BASE:-18140}
[[ $P =~ ^[0-9]+$ ]] && [ "$P" -ge 18090 ] && [ "$P" -le 18197 ] || { echo "SI1_PORT_BASE $P not in 18090-18197" >&2; exit 64; }
P1=$((P + 1)); P2=$((P + 2))
W=${SI1_WORK_DIR:-$(mktemp -d -t si1-run.XXXXXX)}
RES=${SI1_RESULTS_DIR:-$W/results-$TAG}
LOG=${SI1_STANDIN_LOG:-$W/standin-$TAG.log}
if [ -z "${DOTNET_ROOT:-}" ] && [ -d "$HOME/.dotnet" ]; then DOTNET_ROOT=$HOME/.dotnet; fi
[ -n "${DOTNET_ROOT:-}" ] && export DOTNET_ROOT PATH=$DOTNET_ROOT:$PATH
command -v dotnet >/dev/null || { echo "dotnet not found (set DOTNET_ROOT)" >&2; exit 64; }
B=$W/wt-$TAG; PIDF=$W/pid; ST=$W/state

refs=("$STANDIN_REF" "$TESTS_REF"); [ "$OV" != none ] && refs+=("$OV")
for r in "${refs[@]}"; do
  git -C "$REPO" rev-parse -q --verify "$r^{commit}" >/dev/null || { echo "unknown ref $r" >&2; exit 64; }
done
echo "si1-run: tag=$TAG standin=$STANDIN_REF tests=$TESTS_REF overlay=$OV ports=$P-$P2 work=$W"

# shellcheck disable=SC2329  # invoked by the EXIT trap
cleanup() {
  [ -s "$PIDF" ] && kill "$(cat "$PIDF")" 2>/dev/null && sleep 2
  rm -f "$PIDF"
  [ -d "$B" ] && git -C "$REPO" worktree remove --force "$B"
}
trap cleanup EXIT

bash "$HERE/check-ports.sh" free "$P" "$P1" "$P2" || exit 2
mkdir -p "$W" || exit 64
git -C "$REPO" worktree add -q --detach "$B" "$STANDIN_REF" || exit 3
git -C "$B" checkout -q "$TESTS_REF" -- tests tools docs README.md || exit 3
if [ "$OV" != none ]; then
  git -C "$B" checkout -q "$OV" -- src/Login.Entra src/Login.Abstractions tests/Login.EntraTests tests/Login.FakeEntra || exit 3
fi

[ -z "$(ls -A "$RES" 2>/dev/null)" ] || { echo "results dir $RES is not empty; pick another TAG or SI1_RESULTS_DIR" >&2; exit 64; }
rm -rf "${W:?}/bin" "${ST:?}"; mkdir -p "$ST" "$RES" "$W/home" "$W/client-keys" "$(dirname "$LOG")"
chmod 700 "$W/client-keys"
printf '%s\n' '{"source":"sse2 test allowlist (loopback only, not from allowlist.db)","entries":[{"value":"127.0.0.1/32"},{"value":"127.0.0.2/32"}]}' > "$W/allowlist-test.json"
dotnet publish "$B/src/Login.StandIn" -c Release -o "$W/bin" -p:IncludeStandIn=true > "$W/publish-$TAG.log" 2>&1 \
  || { tail "$W/publish-$TAG.log"; exit 5; }

env -i HOME="$W/home" PATH="$PATH" DOTNET_ROOT="${DOTNET_ROOT:-}" STANDIN_PORT_BASE="$P" STANDIN_STATE_DIR="$ST" \
  STANDIN_CLIENT_KEY_DIR="$W/client-keys" STANDIN_UI_DIR="$B/ui/login-hub" STANDIN_ALLOW=1 \
  ASPNETCORE_ENVIRONMENT=Development StandIn__AllowlistJson="$W/allowlist-test.json" \
  "$W/bin/Login.StandIn" > "$LOG" 2>&1 &
echo $! > "$PIDF"
for _ in $(seq 60); do curl -s -o /dev/null "http://127.0.0.1:$P2/" && break; sleep 1; done
bash "$HERE/check-ports.sh" mine "$PIDF" "$P" "$P1" "$P2" || exit 6

# HO17 guard: the log the tests read must be the live stand-in's stdout, never an older file.
[ "$(readlink -f "/proc/$(cat "$PIDF")/fd/1")" = "$(readlink -f "$LOG")" ] \
  || { echo "ABORT LOGIN_STANDIN_LOG $LOG is not fd 1 of stand-in pid $(cat "$PIDF")"; exit 7; }
echo "ok   LOGIN_STANDIN_LOG=$LOG (fd 1 of pid $(cat "$PIDF"))"
[ -n "${SI1_MOCKUPS_DIR:-}" ] || echo "note SI1_MOCKUPS_DIR unset: mockup checks will report pending"

CACHD_CODE_ROOT="$B" CACHD_MOCKUPS_DIR="${SI1_MOCKUPS_DIR:-}" \
  LOGIN_FRONT_URL="http://127.0.0.1:$P" LOGIN_BACK_URL="http://127.0.0.1:$P1" LOGIN_STANDIN_CONTROL_URL="http://127.0.0.1:$P2" \
  LOGIN_STANDIN_LOG="$LOG" LOGIN_STANDIN_FIXTURES="$B/src/Login.StandIn/fixtures.json" \
  LOGIN_STANDIN_SECRETS="$ST/standin-$P/fixture-secrets.json" LOGIN_STANDIN_CLIENT_KEY_DIR="$W/client-keys" \
  STANDIN_PIDFILE="$PIDF" RESULTS_DIR="$RES" \
  bash "$B/tools/run-checks.sh" standin
rc=$?
echo "rc=$rc results=$RES log=$LOG"
exit $rc
