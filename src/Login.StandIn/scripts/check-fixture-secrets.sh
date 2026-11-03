#!/bin/bash
# [TL Decision 9:16 PM, B2] Fails if any private key is tracked in git. FIXTURE client keys live only under /tmp.
# Usage: check-fixture-secrets.sh [GIT_INDEX_FILE]   (defaults to the normal index). Run from the repo.
set -u
idx="${1:-}"
g() { if [ -n "$idx" ]; then GIT_INDEX_FILE="$idx" git "$@"; else git "$@"; fi; }
bad=0
while IFS= read -r f; do
  case "$f" in *.pem|*.key|*.p8|*.pfx|*.p12) echo "FAIL key-like file tracked: $f"; bad=1; continue;; esac
  blob=$(g ls-files -s -- "$f" | awk '{print $2}')
  if git cat-file -p "$blob" 2>/dev/null | grep -qE -- '-----BEGIN ([A-Z]+ )?PRIVATE KEY-----'; then echo "FAIL PEM private key in: $f"; bad=1; fi
  if git cat-file -p "$blob" 2>/dev/null | grep -qE '"kty"[[:space:]]*:[[:space:]]*"(RSA|EC)"' && git cat-file -p "$blob" | grep -qE '"(d|p|q|dp|dq|qi)"[[:space:]]*:[[:space:]]*"'; then
    echo "FAIL JWK private member in: $f"; bad=1; fi
done < <(g ls-files)
[ $bad -eq 0 ] && echo "OK no private key tracked ($(g ls-files | wc -l) files)"
exit $bad
