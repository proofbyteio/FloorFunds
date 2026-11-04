#!/usr/bin/env bash
# check-no-remote.sh [repo-dir]   (default: the code/ repo this script lives in)
# Remote rule (TLD-S2B-01, TL 7:27 AM PT 2026-11-04; Mohanraj approved pushing to FloorFunds): the ONLY allowed remote
# is origin = https://github.com/proofbyteio/FloorFunds.git (fetch AND push URL). Only TL pushes; agents never push.
# PASS only if all of these hold:
#   - exactly one remote, named origin; its fetch URL(s) and push URL(s) are exactly ALLOWED_URL (one each)
#   - no url.*.insteadOf / pushInsteadOf rewrites (they could redirect origin silently)
#   - every branch.*.remote / branch.*.pushRemote / remote.pushDefault is "origin"
#   - no generic credential.helper (URL-scoped helpers are TL's setup and are not checked)
#   - core.hooksPath not redirected; pre-push hook exists, is executable, and refuses any other URL
# (Replaces the SI1 "no remote at all" check, build plan §5.1.)
set -uo pipefail
ALLOWED_URL="https://github.com/proofbyteio/FloorFunds.git"
repo="${1:-$(cd "$(dirname "$0")/.." && pwd)}"
bad=0
fail() { echo "FAIL: $*"; bad=1; }
git -C "$repo" rev-parse --git-dir >/dev/null 2>&1 || { echo "FAIL: '$repo' is not a git repo"; exit 2; }
gitdir="$(cd "$repo" && cd "$(git rev-parse --git-dir)" && pwd)"
g() { git -C "$repo" "$@"; }

remotes="$(g remote)"
[[ "$remotes" == "origin" ]] || fail "remotes must be exactly 'origin', found: '$(echo $remotes)'"
if g config --get-regexp '^remote\.' >/dev/null 2>&1; then
  others="$(g config --get-regexp '^remote\.' | awk '{print $1}' | grep -v '^remote\.origin\.' | grep -vx 'remote.pushdefault' || true)"
  [[ -z "$others" ]] || fail "config for other remotes: $(echo $others)"
fi
fetch_urls="$(g config --get-all remote.origin.url 2>/dev/null || true)"
[[ "$fetch_urls" == "$ALLOWED_URL" ]] || fail "origin fetch URL(s) must be exactly $ALLOWED_URL, found: '$(echo $fetch_urls)'"
push_cfg="$(g config --get-all remote.origin.pushurl 2>/dev/null || true)"
[[ -z "$push_cfg" || "$push_cfg" == "$ALLOWED_URL" ]] || fail "origin pushurl(s) must be unset or exactly $ALLOWED_URL, found: '$(echo $push_cfg)'"
eff_push="$(g remote get-url --push --all origin 2>/dev/null || true)"
eff_fetch="$(g remote get-url --all origin 2>/dev/null || true)"
[[ "$eff_push" == "$ALLOWED_URL" ]] || fail "effective push URL must be $ALLOWED_URL, found: '$(echo $eff_push)'"
[[ "$eff_fetch" == "$ALLOWED_URL" ]] || fail "effective fetch URL must be $ALLOWED_URL, found: '$(echo $eff_fetch)'"
rew="$(g config --get-regexp '^url\..*\.(insteadof|pushinsteadof)$' 2>/dev/null || true)"
[[ -z "$rew" ]] || fail "URL rewrite config present: $(echo $rew)"
br="$(g config --get-regexp '^branch\..*\.(remote|pushremote)$' 2>/dev/null | awk '$2!="origin"' || true)"
[[ -z "$br" ]] || fail "branch remote other than origin: $(echo $br)"
pd="$(g config --get remote.pushDefault 2>/dev/null || true)"
[[ -z "$pd" || "$pd" == "origin" ]] || fail "remote.pushDefault='$pd'"
helpers="$(g config --get-all credential.helper 2>/dev/null | grep -v '^$' || true)"
[[ -z "$helpers" ]] || fail "generic credential.helper set: $helpers"
hp="$(g config --get core.hooksPath 2>/dev/null || true)"
[[ -z "$hp" ]] || fail "core.hooksPath redirected to '$hp' (pre-push guard could be bypassed)"
hook="$gitdir/hooks/pre-push"
if [[ ! -x "$hook" ]]; then fail "pre-push hook missing or not executable ($hook)"
else
  # Run the hook only with NON-allowed URLs (nothing is pushed; the hook is just executed). Each must be refused.
  for u in https://example.invalid/x.git https://github.com/someone-else/FloorFunds.git https://github.com/proofbyteio/Other.git; do
    if (cd "$(mktemp -d)" && timeout 5 "$hook" other "$u" </dev/null >/dev/null 2>&1); then fail "pre-push hook allows $u"; fi
  done
fi
[[ $bad -eq 0 ]] || exit 1
echo "PASS: $repo remotes are exactly origin=$ALLOWED_URL (fetch+push), no rewrites, pre-push refuses other URLs"
