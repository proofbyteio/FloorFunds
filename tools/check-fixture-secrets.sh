#!/bin/bash
# SSE2 (Mon Nov 2 2026, 9:40 PM PT) [TL Decision 9:26 PM PT (d)]: wiring of SSE's check-fixture-secrets.sh (B2, TL 9:16 PM).
# Runs SSE's script, read from the ref under test (never edited, never copied into the repo), against a throwaway index
# built from each ref's tree, so no branch is checked out and the repo's HEAD/index stay untouched.
# Usage: tools/check-fixture-secrets.sh [SCRIPT_REF] [REF...]
#   SCRIPT_REF default feat/si1-standin; REFs default: feat/si1-standin sse2/tests-docs feat/contract-v1
# Exit 0 = every ref clean; 1 = a private key is tracked somewhere; 2 = script missing.
set -u
code="$(cd "$(dirname "$0")/.." && pwd)"; cd "$code"
sref="${1:-feat/si1-standin}"; shift || true
refs=("$@"); [ ${#refs[@]} -gt 0 ] || refs=(feat/si1-standin sse2/tests-docs feat/contract-v1)
tmp=$(mktemp -d /tmp/sse2-fsec.XXXX); trap 'rm -rf "$tmp"' EXIT
git show "$sref:src/Login.StandIn/scripts/check-fixture-secrets.sh" > "$tmp/s.sh" 2>/dev/null || { echo "PENDING script not found at $sref"; exit 2; }
echo "script $sref md5 $(md5sum < "$tmp/s.sh" | cut -c1-32)"
rc=0
for r in "${refs[@]}"; do
  git rev-parse -q --verify "$r^{commit}" >/dev/null || { echo "skip $r (no such ref)"; continue; }
  GIT_INDEX_FILE="$tmp/idx" git read-tree "$r"
  out=$(bash "$tmp/s.sh" "$tmp/idx" 2>&1); x=$?
  echo "$r $(git rev-parse --short "$r"): $out"
  [ $x -eq 0 ] || rc=1
done
exit $rc
