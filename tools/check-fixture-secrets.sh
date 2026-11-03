#!/bin/bash
# SSE2 (Mon Nov 2 2026, 10:05 PM PT) [TL 9:26 PM (d) wiring; TL 9:50 PM PT fixture-secrets ruling].
# Runs SSE's check-fixture-secrets.sh, read from SCRIPT_REF (never edited, never copied into the repo), for each REF against a
# throwaway index built from that REF's tree AND over that REF's whole history (script arg 2 = REF), then applies the TL 9:50 rule:
#   secrets found ONLY in commits eb0fa5d..c4da996 (inclusive) may WARN; any secret in any other commit must FAIL.
# SSE's script at 10a1ca4 reports every history secret as WARN, attributed to one commit (blobs de-duplicated); at 96a629a it
# WARNs only for its KNOWN list of the 13 commits eb0fa5d..c4da996 and FAILs elsewhere. Either way, for every WARN this
# wrapper (1) re-checks that the blob really holds a secret (SSE's sechit flags fixtures*.json with no "secret" key at all: shell
# precedence in `A && ! B || echo`; those are printed FP-SSE and ignored), and (2) lists EVERY commit of REF's history that carries
# the blob (any path); one such commit outside the range -> FAIL. Private keys (FAIL in SSE's script) stay FAIL unless the exact
# blob is in tools/fixture-secrets-fp.txt and passes the fake-marker gate (printed FP, OQ-SSE2-59).
# Usage: tools/check-fixture-secrets.sh [SCRIPT_REF] [REF...]
#   SCRIPT_REF default feat/si1-standin; REFs default: feat/si1-standin sse2/tests-docs feat/contract-v1
#   env FSEC_REPO (default: this code dir), FSEC_RANGE (default eb0fa5d..c4da996), FSEC_FP_FILE (default tools/fixture-secrets-fp.txt)
# Exit 0 = clean (WARN-in-range allowed); 1 = a private key or an out-of-range secret; 2 = script missing.
set -u
code="$(cd "$(dirname "$0")/.." && pwd)"
fp="${FSEC_FP_FILE:-$code/tools/fixture-secrets-fp.txt}"
cd "${FSEC_REPO:-$code}"
sref="${1:-feat/si1-standin}"; shift || true
refs=("$@"); [ ${#refs[@]} -gt 0 ] || refs=(feat/si1-standin sse2/tests-docs feat/contract-v1)
range="${FSEC_RANGE:-eb0fa5d..c4da996}"; lo="${range%%..*}"; hi="${range##*..}"
tmp=$(mktemp -d /tmp/sse2-fsec.XXXX); trap 'rm -rf "$tmp"' EXIT
git show "$sref:src/Login.StandIn/scripts/check-fixture-secrets.sh" > "$tmp/s.sh" 2>/dev/null || { echo "PENDING script not found at $sref"; exit 2; }
echo "script $sref md5 $(md5sum < "$tmp/s.sh" | cut -c1-32); WARN range $range (inclusive)"
inrange() { # commit -> 0 if lo <= c <= hi by ancestry
  git rev-parse -q --verify "$lo^{commit}" >/dev/null && git rev-parse -q --verify "$hi^{commit}" >/dev/null || return 1
  git merge-base --is-ancestor "$lo" "$1" && git merge-base --is-ancestor "$1" "$hi"
}
realsecret() { # blob -> 0 if it really holds a fixture client secret / recovery code
  local t; t=$(git cat-file -p "$1" 2>/dev/null | tr -d '\0')
  printf '%s' "$t" | grep -E '"secret"[[:space:]]*:[[:space:]]*"[^"]+"' | grep -qvE '"GENERATED"' && return 0
  printf '%s' "$t" | grep -qE '"recovery_codes"[[:space:]]*:[[:space:]]*\[' && return 0
  printf '%s' "$t" | grep -qE 'FIXTURE-RC-[0-9a-f]{6,}' && return 0
  return 1
}
fpok() { # blob -> 0 if allow-listed and every BEGIN line is a fake marker
  [ -f "$fp" ] && grep -qE "^$1 " "$fp" || return 1
  local t; t=$(git cat-file -p "$1" 2>/dev/null)
  ! printf '%s\n' "$t" | grep -- '-----BEGIN' | grep -qv 'SSE2-FAKE-TEST-ONLY'
}
rc=0
for r in "${refs[@]}"; do
  git rev-parse -q --verify "$r^{commit}" >/dev/null || { echo "skip $r (no such ref)"; continue; }
  rm -f "$tmp/idx"; GIT_INDEX_FILE="$tmp/idx" git read-tree "$r"
  out=$(bash "$tmp/s.sh" "$tmp/idx" "$r" 2>&1); x=$?
  echo "== $r $(git rev-parse --short "$r"): SSE script rc=$x"
  git rev-list "$r" > "$tmp/revs"
  bad=0; declare -A done=()
  while IFS= read -r l; do
    case "$l" in
      "FAIL index "*" in: "*)
        p="${l##* in: }"; b=$(GIT_INDEX_FILE="$tmp/idx" git ls-files -s -- "$p" | awk '{print $2}')
        if [ -n "$b" ] && fpok "$b"; then echo "  FP   $l (blob ${b:0:10} allow-listed fake markers, OQ-SSE2-59)"; else echo "  FAIL $l"; bad=1; fi;;
      "FAIL history "*" @"*)
        c="${l##*@}"; c="${c%% *}"; p="${l#FAIL history * in }"; p="${p% @*}"; b=$(git rev-parse -q --verify "$c:$p" 2>/dev/null)
        [ -n "$b" ] && [ -n "${done[F$b]:-}" ] && continue; [ -n "$b" ] && done[F$b]=1
        if [ -n "$b" ] && fpok "$b"; then echo "  FP   $l (blob ${b:0:10} allow-listed fake markers, OQ-SSE2-59)"; else echo "  FAIL $l"; bad=1; fi;;
      "WARN history "*)
        c="${l##*@}"; c="${c%% *}"; p="${l#WARN history * in }"; p="${p% @*}"; b=$(git rev-parse -q --verify "$c:$p" 2>/dev/null)
        [ -n "${done[$b]:-}" ] && continue; done[$b]=1
        if [ -z "$b" ] || ! realsecret "$b"; then echo "  FP-SSE $p @$c blob ${b:0:10}: no secret in blob (SSE sechit precedence)"; continue; fi
        out_c=(); in_n=0
        while read -r k; do
          git ls-tree -r "$k" | awk '{print $3}' | grep -qx "$b" || continue
          if inrange "$k"; then in_n=$((in_n+1)); else out_c+=("${k:0:7}"); fi
        done < "$tmp/revs"
        if [ ${#out_c[@]} -eq 0 ]; then echo "  WARN secret blob ${b:0:10} ($p) only in $in_n commit(s) inside $range"
        else echo "  FAIL secret blob ${b:0:10} ($p) in commit(s) outside $range: ${out_c[*]}"; bad=1; fi;;
      FAIL*) echo "  FAIL $l"; bad=1;;
      *) echo "  $l";;
    esac
  done <<< "$out"
  unset done
  [ $x -ne 0 ] && [ $bad -eq 0 ] && ! grep -q '^FAIL' <<< "$out" && { echo "  FAIL SSE script rc=$x without a FAIL line"; bad=1; }
  echo "  => $r $([ $bad -eq 0 ] && echo PASS || echo FAIL)"
  [ $bad -eq 0 ] || rc=1
done
exit $rc
