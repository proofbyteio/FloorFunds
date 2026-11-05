#!/bin/bash
# F-SURR-LOGIN regression runner (TLD 16:48). Usage: surr-regress.sh TREE BASE WORK
#   TREE = repo tree whose src/Login.StandIn is built (Release, IncludeStandIn=true); BASE = port base (18090-18197, not 18130-18139)
#   WORK = scratch dir (created; holds state, log, results). Exit 0 only if static checks, 322 live probes and 5 Allowlist cases pass.
# Processes are stopped only by their recorded pid; the script checks pid gone and ports closed and never deletes anything.
set -u
TREE=$(cd "$1" && pwd); BASE=$2; W=$3; mkdir -p "$W/state"; S=$TREE/src/Login.StandIn; HERE=$(cd "$(dirname "$0")" && pwd)
[[ $BASE =~ ^[0-9]+$ ]] && [ "$BASE" -ge 18090 ] && [ "$BASE" -le 18197 ] && { [ "$BASE" -gt 18139 ] || [ $((BASE + 2)) -lt 18130 ]; } || { echo "bad BASE $BASE" >&2; exit 64; }
fail=0
# --- static: one parse helper; no raw JsonNode.Parse outside it (Allowlist.cs frozen, excluded); helper never catches Exception.
raw=$(grep -nE 'JsonNode\.Parse(Async)?\(' "$S"/*.cs | grep -vE '/(StandInJson|Allowlist)\.cs:')
[ -z "$raw" ] && echo "STATIC PASS no raw JsonNode.Parse outside StandInJson.cs" || { echo "STATIC FAIL raw parse: $raw"; fail=1; }
grep -qE 'catch *\((System\.)?Exception\b' "$S/StandInJson.cs" && { echo "STATIC FAIL StandInJson.cs catches Exception"; fail=1; } || echo "STATIC PASS StandInJson.cs catches only JsonException/InvalidOperationException"
[ "$(grep -cE 'catch \((System\.Text\.Json\.)?(JsonException|InvalidOperationException)\)' "$S/StandInJson.cs")" = 4 ] && echo "STATIC PASS 4 narrow catches" || { echo "STATIC FAIL narrow catch count"; fail=1; }
dotnet build -c Release -p:IncludeStandIn=true "$S" -o "$W/bin" >"$W/build.log" 2>&1 || { echo "BUILD FAIL (see $W/build.log)"; exit 5; }
start() { # $1 base $2 allowlist $3 log -> echoes pid
  for p in $1 $(($1 + 1)) $(($1 + 2)); do ss -ltnH "sport = :$p" | grep -q . && { echo "port $p busy" >&2; return 2; }; done
  ( cd "$W" && ASPNETCORE_ENVIRONMENT=Development STANDIN_ALLOW=1 STANDIN_PORT_BASE=$1 STANDIN_STATE_DIR=$W/state STANDIN_UI_DIR=$TREE/ui/login-hub \
      exec dotnet "$W/bin/Login.StandIn.dll" --StandIn:AllowlistJson="$2" >"$3" 2>&1 ) & local pid=$!
  for _ in $(seq 60); do ss -ltnpH "sport = :$1" | grep -q "pid=$pid," && { echo $pid; return 0; }; kill -0 $pid 2>/dev/null || break; sleep 0.5; done
  echo "start failed (log $3)" >&2; return 3
}
stop() { # $1 pid $2 base
  kill -TERM "$1" 2>/dev/null; for _ in $(seq 40); do kill -0 "$1" 2>/dev/null || break; sleep 0.25; done
  kill -0 "$1" 2>/dev/null && { echo "STOP FAIL pid $1 alive"; return 1; }
  for p in $2 $(($2 + 1)) $(($2 + 2)); do ss -ltnH "sport = :$p" | grep -q . && { echo "STOP FAIL port $p open"; return 1; }; done
  echo "stopped pid $1; ports $2-$(($2 + 2)) closed"
}
printf '{"entries":[{"value":"127.0.0.1/32"},{"value":"::1/128"}]}' >"$W/allow.json"
PID=$(start "$BASE" "$W/allow.json" "$W/standin.log") || exit 2
echo "$PID" >"$W/pid"
python3 "$HERE/surr-regress.py" "$BASE" "$W/state" "$W/standin.log" "$W/probes.jsonl" >"$W/probes.out" 2>&1; rc=$?
tail -1 "$W/probes.out"; [ $rc = 0 ] || { grep -vE '^PASS' "$W/probes.out" | head -40; fail=1; }
stop "$PID" "$BASE" || exit 9
# --- Allowlist startup file (Allowlist.cs frozen; behaviour pinned): undecodable -> loaded=False -> front 403 text/plain.
declare -A AL=(
  [lone_high]='{"entries":[{"value":"127.0.0.1/32"},{"value":"10.0.0.1\ud83d"}]}'
  [lone_key]='{"entries":[{"value":"127.0.0.1/32","\ude00":1}]}'
  [reversed]='{"entries":[{"value":"127.0.0.1/32"}],"\ude00\ud83d":{"x":["\ud83d"]}}'
  [CTRL_valid_pair]='{"entries":[{"value":"127.0.0.1/32"}],"note":"\ud83d\ude00"}'
  [raw_invalid]=$'{"entries":[{"value":"127.0.0.1/32"}],"note":"\xed\xa0\xbd\xff"}')
for f in lone_high lone_key reversed CTRL_valid_pair raw_invalid; do
  printf '%s' "${AL[$f]}" >"$W/al-$f.json"
  P2=$(start "$BASE" "$W/al-$f.json" "$W/al-$f.log") || { fail=1; continue; }
  r=$(curl -s -o "$W/al-body" -w '%{http_code}|%{content_type}' "http://127.0.0.1:$BASE/standin/.well-known/openid-configuration")
  ld=$(grep -o 'allowlist loaded=[A-Za-z]*' "$W/al-$f.log" | head -1)
  case $f in CTRL*|raw*) exp='200|application/json'; el='allowlist loaded=True';; *) exp='403|text/plain; charset=utf-8'; el='allowlist loaded=False';; esac
  [ "$r" = "$exp" ] && [ "$ld" = "$el" ] && echo "PASS Allowlist $f $r $ld" || { echo "FAIL Allowlist $f $r $ld (want $exp $el)"; fail=1; }
  printf '{"endpoint":"Allowlist (startup file)","placement":"file","form":"%s","status":%s,"loaded":"%s","pass":%s}\n' "$f" "${r%%|*}" "$ld" "$([ "$r" = "$exp" ] && [ "$ld" = "$el" ] && echo true || echo false)" >>"$W/probes.jsonl"
  stop "$P2" "$BASE" || exit 9
done
echo "SURR-REGRESS-TOTAL $(grep -c '"pass": *true' "$W/probes.jsonl")/$(wc -l <"$W/probes.jsonl") fail=$fail"
exit $fail
