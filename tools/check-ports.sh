#!/bin/bash
# SSE2 (Mon Nov 2 2026, 9:35 PM PT) [TL Decision 9:26 PM PT (c)]: check who owns the stand-in ports before every run.
# Usage:
#   tools/check-ports.sh free  PORT...          -> exit 0 only if every PORT is free (use before starting a stand-in)
#   tools/check-ports.sh mine  PIDFILE PORT...  -> exit 0 only if every PORT is listened on by the pid in PIDFILE
# Any other owner -> prints pid + command line and exits 2 (abort). Never 8080/8081 (exit 4).
# Background: 9:18 PM PT another team member's driver hit SSE2's stand-in on 18080-18082 (BUILD-LOG incident entry).
set -u
mode="${1:-}"; shift || true
want=""
if [ "$mode" = mine ]; then pf="${1:-}"; shift || true; want=$(cat "$pf" 2>/dev/null) || { echo "ABORT no pid file $pf"; exit 2; }; fi
[ "$mode" = free ] || [ "$mode" = mine ] || { echo "usage: $0 free PORT... | mine PIDFILE PORT..."; exit 64; }
[ $# -gt 0 ] || { echo "usage: no ports"; exit 64; }
rc=0
for p in "$@"; do
  case "$p" in 8080|8081) echo "ABORT port $p is forbidden (HARD RULE)"; exit 4;; esac
  line=$(ss -ltnpH "sport = :$p" 2>/dev/null | head -1)
  pid=$(printf '%s' "$line" | grep -oE 'pid=[0-9]+' | head -1 | cut -d= -f2)
  if [ "$mode" = free ]; then
    if [ -n "$line" ]; then echo "ABORT port $p busy: pid=${pid:-?} $( [ -n "$pid" ] && tr '\0' ' ' </proc/$pid/cmdline | cut -c1-160)"; rc=2; else echo "ok   port $p free"; fi
  else
    if [ -z "$line" ]; then echo "ABORT port $p: nothing listening (expected pid $want)"; rc=2
    elif [ "$pid" != "$want" ]; then echo "ABORT port $p owned by pid=${pid:-?} not $want: $( [ -n "$pid" ] && tr '\0' ' ' </proc/$pid/cmdline | cut -c1-160)"; rc=2
    else echo "ok   port $p owned by pid $want"; fi
  fi
done
exit $rc
