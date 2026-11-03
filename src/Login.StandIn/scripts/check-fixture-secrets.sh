#!/bin/bash
# [TL Decision 9:16 PM, B2; extended TL 9:42 PM, F14] Fails if a private key (PEM, or JWK private members for RSA/EC/OKP, incl. OKP "d")
# or a fixture client secret / recovery code is tracked, in the index or anywhere in git history.
# Client secrets and recovery codes are generated at stand-in start into <state dir>/fixture-secrets.json (/tmp), never committed.
# Usage: check-fixture-secrets.sh [GIT_INDEX_FILE] [REV]   (defaults: normal index; history of REV, default HEAD). Run from the repo.
# [ASSUMPTION A55] secrets/recovery codes in commits made BEFORE this rule are reported as WARN (those values are dead: the stand-in
#   no longer reads them); private keys anywhere in history stay FAIL. Rewriting history needs a TL ruling (not done).
set -u
idx="${1:-}"; rev="${2:-HEAD}"
g() { if [ -n "$idx" ]; then GIT_INDEX_FILE="$idx" git "$@"; else git "$@"; fi; }
bad=0; warn=0
keyhit() { # $1 blob -> prints reason if a private key is inside
  local t; t=$(git cat-file -p "$1" 2>/dev/null | tr -d '\0') || return 0
  printf '%s' "$t" | grep -qE -- '-----BEGIN ([A-Z]+ )?PRIVATE KEY-----' && echo "PEM private key"
  if printf '%s' "$t" | grep -qE '"kty"[[:space:]]*:[[:space:]]*"(RSA|EC|OKP)"' && printf '%s' "$t" | grep -qE '"(d|p|q|dp|dq|qi)"[[:space:]]*:[[:space:]]*"'; then echo "JWK private member"; fi
}
sechit() { # $1 blob $2 path -> fixture client secret or recovery code
  local t; t=$(git cat-file -p "$1" 2>/dev/null | tr -d '\0') || return 0
  case "$2" in *fixtures*.json)
    printf '%s' "$t" | grep -qE '"secret"[[:space:]]*:[[:space:]]*"[^"]+"' && ! printf '%s' "$t" | grep -E '"secret"[[:space:]]*:' | grep -qvE '"GENERATED"' || echo "client secret"
    printf '%s' "$t" | grep -qE '"recovery_codes"[[:space:]]*:' && echo "recovery codes";;
  esac
  printf '%s' "$t" | grep -qE 'FIXTURE-RC-[0-9a-f]{6,}' && echo "recovery code literal"
}
n=0
while IFS= read -r f; do
  n=$((n+1))
  case "$f" in *.pem|*.key|*.p8|*.pfx|*.p12) echo "FAIL key-like file tracked: $f"; bad=1; continue;; esac
  [ "$f" = "src/Login.StandIn/scripts/check-fixture-secrets.sh" ] && continue
  blob=$(g ls-files -s -- "$f" | awk '{print $2}')
  for r in $(keyhit "$blob" | tr ' ' '_'); do echo "FAIL index ${r//_/ } in: $f"; bad=1; done
  for r in $(sechit "$blob" "$f" | tr ' ' '_'); do echo "FAIL index ${r//_/ } in: $f"; bad=1; done
done < <(g ls-files)
# history: every blob reachable from REV
declare -A seen
hn=0
if git rev-parse -q --verify "$rev^{commit}" >/dev/null; then
  while read -r c; do
    while read -r mode type blob path; do
      [ "$type" = blob ] || continue; [ -n "${seen[$blob]:-}" ] && continue; seen[$blob]=1; hn=$((hn+1))
      [ "$path" = "src/Login.StandIn/scripts/check-fixture-secrets.sh" ] && continue
      case "$path" in *.pem|*.key|*.p8|*.pfx|*.p12) echo "FAIL history key-like file $path @${c:0:7}"; bad=1;; esac
      for r in $(keyhit "$blob" | tr ' ' '_'); do echo "FAIL history ${r//_/ } in $path @${c:0:7}"; bad=1; done
      for r in $(sechit "$blob" "$path" | tr ' ' '_'); do echo "WARN history ${r//_/ } in $path @${c:0:7} (pre-F14, dead value; A55)"; warn=1; done
    done < <(git ls-tree -r "$c" | sed 's/\t/ /')
  done < <(git rev-list "$rev")
fi
[ $bad -eq 0 ] && echo "OK no private key or fixture secret tracked ($n index files; $hn history blobs of $rev scanned; warn=$warn)"
exit $bad
