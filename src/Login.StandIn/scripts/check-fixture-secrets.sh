#!/bin/bash
# FS1 (TL 10:30 PM): this script scans itself too; its patterns are written so they never match their own text.
# [TL Decision 9:16 PM, B2; extended TL 9:42 PM, F14] Fails if a private key (PEM, or JWK private members for RSA/EC/OKP, incl. OKP "d")
# or a fixture client secret / recovery code is tracked, in the index or anywhere in git history.
# Client secrets and recovery codes are generated at stand-in start into <state dir>/fixture-secrets.json (/tmp), never committed.
# Usage: check-fixture-secrets.sh [GIT_INDEX_FILE] [REV]   (defaults: normal index; history of REV, default HEAD). Run from the repo.
# A55 [TL Decision 9:50 PM; list narrowed TL 9:58 PM]: history is NOT rewritten. A fixture client secret / recovery code hit is
#   WARN only in exactly the 7 commits below that really contain the old (dead) values; a hit in ANY other commit FAILs.
#   Private keys FAIL in every commit.
KNOWN=" 1ee0939e38f34a488ef38628adb160e29fc926fb df0b5d6a21d371d94dcd8379eb26b735526d5f2a ed0ecc127cd139fad03f8af5eaebdd5642835c83
 9a141632f8695f018d49f54c11f052a3a30aba1b 8aab98ab6d6c60437606cc9189dc6a977b7a7a6e f630a8d3211151627e1c6e5b11862aee68e86399
 c4da996adaa939d9724b221058cd4078f7b04bba "
KNOWN=" $(echo $KNOWN) "
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
    if printf '%s' "$t" | grep -E '"(secret|password)"[[:space:]]*:[[:space:]]*"[^"]+"' | grep -qvE '"(secret|password)"[[:space:]]*:[[:space:]]*"GENERATED"'; then
      printf '%s' "$t" | grep -E '"secret"[[:space:]]*:[[:space:]]*"[^"]+"' | grep -qv '"GENERATED"' && echo "client secret"
    fi
    printf '%s' "$t" | grep -qE '"recovery_codes"[[:space:]]*:' && echo "recovery codes";;
  esac
  printf '%s' "$t" | grep -qE 'FIXTURE-RC-[0-9a-f]{6,}' && echo "recovery code literal"
}
n=0
while IFS= read -r f; do
  n=$((n+1))
  case "$f" in *.pem|*.key|*.p8|*.pfx|*.p12) echo "FAIL key-like file tracked: $f"; bad=1; continue;; esac
  blob=$(g ls-files -s -- "$f" | awk '{print $2}')
  for r in $(keyhit "$blob" | tr ' ' '_'); do echo "FAIL index ${r//_/ } in: $f"; bad=1; done
  for r in $(sechit "$blob" "$f" | tr ' ' '_'); do echo "FAIL index ${r//_/ } in: $f"; bad=1; done
done < <(g ls-files)
# history: every commit reachable from REV, every blob in it (results cached per blob; verdict is per commit).
declare -A kc sc
hn=0
if git rev-parse -q --verify "$rev^{commit}" >/dev/null; then
  while read -r c; do
    hn=$((hn+1))
    while read -r mode type blob path; do
      [ "$type" = blob ] || continue
      case "$path" in *.pem|*.key|*.p8|*.pfx|*.p12) echo "FAIL history key-like file $path @${c:0:7}"; bad=1;; esac
      [ -n "${kc[$blob]+x}" ] || kc[$blob]=$(keyhit "$blob" | tr '\n' ';')
      [ -n "${sc[$blob]+x}" ] || sc[$blob]=$(sechit "$blob" "$path" | tr '\n' ';')
      [ -n "${kc[$blob]}" ] && { echo "FAIL history ${kc[$blob]} in $path @${c:0:7}"; bad=1; }
      if [ -n "${sc[$blob]}" ]; then
        case "$KNOWN" in *" $c "*) echo "WARN history ${sc[$blob]} in $path @${c:0:7} (A55 known commit, dead value)"; warn=1;;
          *) echo "FAIL history ${sc[$blob]} in $path @${c:0:7} (not in the A55 list)"; bad=1;; esac
      fi
    done < <(git ls-tree -r "$c" | sed 's/\t/ /')
  done < <(git rev-list "$rev")
fi
[ $bad -eq 0 ] && echo "OK no private key or fixture secret tracked ($n index files; $hn history commits of $rev scanned; warn=$warn)"
exit $bad
