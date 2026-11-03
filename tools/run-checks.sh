#!/usr/bin/env bash
# run-checks.sh — run the S1 suites and print PASS / FAIL / PENDING / NOT-RUN per S1 check (plan §5.1, checks 1-12, 4a, 4b, pw-class).
# Owner: SSE2. Usage: tools/run-checks.sh [standin|real]   (default: $LOGIN_CONTRACT_TARGET or standin)
# Per check: FAIL if any test with that check trait failed; PASS if >=1 passed and none failed; PENDING if all skipped; NOT-RUN if none.
# "PASS" = every test of the check ran and passed; "PARTIAL" = runnable tests passed but some are pending (skip PENDING OQ-..); neither is S1 sign-off.
# Check 12 (manual walkthrough) is always reported MANUAL. Exit code 1 if any test failed.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; code="$(cd "$here/.." && pwd)"
export LOGIN_CONTRACT_TARGET="${1:-${LOGIN_CONTRACT_TARGET:-standin}}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" PATH="$DOTNET_ROOT:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
out="${RESULTS_DIR:-$(mktemp -d /tmp/sse2-results.XXXX)}"; mkdir -p "$out"
# [TL Decision 9:26 PM PT (c)] port-owner check before every run: abort if anyone but our stand-in (STANDIN_PIDFILE) holds the ports.
if [ "$LOGIN_CONTRACT_TARGET" = standin ]; then
  ports=$(for u in "${LOGIN_FRONT_URL:-}" "${LOGIN_BACK_URL:-}" "${LOGIN_STANDIN_CONTROL_URL:-${LOGIN_CONTROL_URL:-}}"; do [ -n "$u" ] && python3 -c 'import sys,urllib.parse as u;print(u.urlsplit(sys.argv[1]).port)' "$u"; done)
  if [ -n "$ports" ]; then
    if [ -n "${STANDIN_PIDFILE:-}" ]; then "$here/check-ports.sh" mine "$STANDIN_PIDFILE" $ports || { echo "ABORT: port owner check failed; no traffic sent"; exit 3; }
    else echo "WARN: STANDIN_PIDFILE not set; port owners not checked ($ports)"; fi
  fi
fi
# [TL Decision 9:26 PM PT (d)] fixture-secrets check (SSE's script, every branch); also runs as FixtureSecretsTests (check fixture-secrets).
"$here/check-fixture-secrets.sh"; fsec=$?
[ $fsec -eq 1 ] && { echo "FAIL fixture-secrets: a private key is tracked"; }
dotnet build "$code/tests/Cachd.Tests.slnx" -nologo -v q >"$out/build.log" 2>&1 || { echo "BUILD FAILED (see $out/build.log)"; exit 2; }
for p in Login.ContractTests Login.SecurityTests e2e; do
  exe=$(ls "$code/tests/$p/bin/Debug/net10.0/"Login.*Tests "$code/tests/$p/bin/Debug/net10.0/Login.E2E" 2>/dev/null | head -1)
  "$exe" -noColor -result-xml "$out/$p.xml" >"$out/$p.log" 2>&1
done
python3 - "$out" <<'PY'
import sys, glob, xml.etree.ElementTree as ET, collections
out = sys.argv[1]; per = collections.defaultdict(lambda: collections.Counter()); tot = collections.Counter(); pend = collections.defaultdict(set)
for f in sorted(glob.glob(out + "/*.xml")):
    for t in ET.parse(f).iter("test"):
        r = t.get("result"); tr = {x.get("name"): x.get("value") for x in t.iter("trait")}
        tot[r] += 1; chk = tr.get("check", "(none)"); per[chk][r] += 1
        if r == "Skip":
            reason = (t.findtext("reason") or "").strip()
            pend[chk].add(reason.split(":")[0][:60])
order = ["fixture-secrets","1","2","3","4","4a","4b","5","6","7","8","9","10","11","12","pw-class","session-cap","(none)"]
print(f"{'check':9} {'status':8} pass fail skip")
anyfail = False
for c in order + sorted(set(per) - set(order)):
    n = per.get(c, collections.Counter())
    st = "MANUAL" if c == "12" and not n else ("NOT-RUN" if not n else "FAIL" if n["Fail"] else ("PARTIAL" if n["Skip"] else "PASS") if n["Pass"] else "PENDING")
    anyfail |= st == "FAIL"
    print(f"{c:9} {st:8} {n['Pass']:4} {n['Fail']:4} {n['Skip']:4}  {'; '.join(sorted(pend[c]))[:150]}")
print(f"TOTAL tests={sum(tot.values())} passed={tot['Pass']} failed={tot['Fail']} skipped(pending)={tot['Skip']} not-run={tot['NotRun']} xfail=0 (xUnit v3 has no xfail; pending = skip 'PENDING OQ-..')")
print("results:", out)
sys.exit(1 if anyfail else 0)
PY
rc=$?; [ "${fsec:-0}" -eq 1 ] && rc=1; exit $rc
