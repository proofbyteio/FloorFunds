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
# TL 10:36 PM (extends G7): a literal (non-GENERATED) "password" in a tracked fixtures*.json FAILs in the index and in any new commit.
#   History is NOT rewritten: the commits below (before 9b7efc9 made every fixture password GENERATED) are WARN only, as A55.
KNOWN_PW="
 10a1ca4c20c859584835bc357b09d3b00ef3b6d7 1685eeaf616a4c59487cae55f1a6fa6e6396f1b1 
 1ee0939e38f34a488ef38628adb160e29fc926fb 3087f582d44ffd3a339eff4b04c32d2f94dda29f 
 3feb66c4e06e81429f0bd4c3d3ab1b6874818587 40da81dcdcc27c5291a36bf1f56808c38a6f7d98 
 43a37fcb3cb2a97d9e543161bc19675715911c93 4b330efa299ae06624981f18f2494052f3f48cdd 
 8aab98ab6d6c60437606cc9189dc6a977b7a7a6e 96a629a780f59097cd50697e5fc4f2cd4302a32f 
 97bfdb2fee1fe5205b8f766c511cf99350f7377a 9a141632f8695f018d49f54c11f052a3a30aba1b 
 baaef0ed2adf3f94c1ed7cab9f75751c24e7f11a c4da996adaa939d9724b221058cd4078f7b04bba 
 d39af1f4fee8f18adf57e806e6c5449c92c0b70c dd59e8582ee44dd17b66ba0d81e9d23ae45244fa 
 df0b5d6a21d371d94dcd8379eb26b735526d5f2a eb0fa5d532c86c9b7d6e6d4b67a246e0b3804436 
 ed0ecc127cd139fad03f8af5eaebdd5642835c83 f630a8d3211151627e1c6e5b11862aee68e86399 
 f63b2c8b86c85d9fa316acccaea3bab18bb154c9 faea706d95cfa4effae75e270a9049abe798d8a1 "
KNOWN_PW=" $(echo $KNOWN_PW) "
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
pwhit() { # $1 blob $2 path -> a literal fixture password (anything but GENERATED)
  local t; t=$(git cat-file -p "$1" 2>/dev/null | tr -d '\0') || return 0
  case "$2" in *fixtures*.json)
    printf '%s' "$t" | grep -E '"password"[[:space:]]*:[[:space:]]*"[^"]+"' | grep -qv '"GENERATED"' && echo "fixture password literal";;
  esac
}
n=0
while IFS= read -r f; do
  n=$((n+1))
  case "$f" in *.pem|*.key|*.p8|*.pfx|*.p12) echo "FAIL key-like file tracked: $f"; bad=1; continue;; esac
  blob=$(g ls-files -s -- "$f" | awk '{print $2}')
  for r in $(keyhit "$blob" | tr ' ' '_'); do echo "FAIL index ${r//_/ } in: $f"; bad=1; done
  for r in $(sechit "$blob" "$f" | tr ' ' '_'); do echo "FAIL index ${r//_/ } in: $f"; bad=1; done
  for r in $(pwhit "$blob" "$f" | tr ' ' '_'); do echo "FAIL index ${r//_/ } in: $f"; bad=1; done
done < <(g ls-files)
# history: every commit reachable from REV, every blob in it (results cached per blob; verdict is per commit).
declare -A kc sc pc
hn=0
if git rev-parse -q --verify "$rev^{commit}" >/dev/null; then
  while read -r c; do
    hn=$((hn+1))
    while read -r mode type blob path; do
      [ "$type" = blob ] || continue
      case "$path" in *.pem|*.key|*.p8|*.pfx|*.p12) echo "FAIL history key-like file $path @${c:0:7}"; bad=1;; esac
      [ -n "${kc[$blob]+x}" ] || kc[$blob]=$(keyhit "$blob" | tr '\n' ';')
      [ -n "${sc[$blob]+x}" ] || sc[$blob]=$(sechit "$blob" "$path" | tr '\n' ';')
      [ -n "${pc[$blob]+x}" ] || pc[$blob]=$(pwhit "$blob" "$path" | tr '\n' ';')
      if [ -n "${pc[$blob]}" ]; then
        case "$KNOWN_PW" in *" $c "*) echo "WARN history ${pc[$blob]} in $path @${c:0:7} (known pre-G7 commit)"; warn=1;;
          *) echo "FAIL history ${pc[$blob]} in $path @${c:0:7} (not in the KNOWN_PW list)"; bad=1;; esac
      fi
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
