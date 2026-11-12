#!/bin/bash
# The listener helper accepts a process this script started and refuses a pid
# that is not under the root it was given.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=own-listener.sh
source "$HERE/own-listener.sh"
fail() { echo "FAIL own-listener :: $*" >&2; exit 1; }

port=""
for candidate in $(seq 18110 18140); do
  if ! port_is_open "$candidate"; then port="$candidate"; break; fi
done
[[ -n "$port" ]] || fail "no free port"
python3 -m http.server "$port" --bind 127.0.0.1 >/tmp/ff-own-listener-test.log 2>&1 &
py="$!"
for _ in $(seq 1 40); do
  port_is_open "$port" && break
  sleep 0.1
done
got="$(listener_pid "$port")"
[[ "$got" == "$py" ]] || fail "listener pid ${got} is not the python pid ${py}"
pid_is_under "$got" "$$" || fail "the python listener is not under this script"
if pid_is_under "$got" "$$"; then
  :
else
  fail "own listener was refused"
fi
sleep 30 &
other="$!"
if pid_is_under "$got" "$other"; then
  kill "$py" "$other" 2>/dev/null || true
  wait "$py" "$other" 2>/dev/null || true
  fail "a foreign root was accepted"
fi
if require_port_free "$port" "stand-in"; then
  kill "$py" "$other" 2>/dev/null || true
  wait "$py" "$other" 2>/dev/null || true
  fail "a taken port was treated as free"
fi
if wait_own_listener "$other" "$port" "stand-in"; then
  kill "$py" "$other" 2>/dev/null || true
  wait "$py" "$other" 2>/dev/null || true
  fail "wait_own_listener accepted a foreign pid"
fi
wait_own_listener "$$" "$port" "stand-in" || { kill "$py" "$other" 2>/dev/null || true; wait "$py" "$other" 2>/dev/null || true; fail "wait_own_listener refused this script"; }
kill "$py" "$other" 2>/dev/null || true
wait "$py" "$other" 2>/dev/null || true
require_port_free "$port" "stand-in" || fail "the port stayed taken after the listener exited"
echo "PASS own-listener-refuses-a-foreign-pid"
