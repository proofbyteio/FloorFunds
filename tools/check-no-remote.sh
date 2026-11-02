#!/usr/bin/env bash
# check-no-remote.sh [repo-dir]   (default: the code/ repo this script lives in)
# Build plan §5.1: local git only. Fails if the repo has any remote, remote-ish config, a credential helper,
# a redirected hooks path, or a pre-push hook that is missing or does not refuse.
set -uo pipefail
repo="${1:-$(cd "$(dirname "$0")/.." && pwd)}"
fail() { echo "FAIL: $*"; bad=1; }
bad=0
git -C "$repo" rev-parse --git-dir >/dev/null 2>&1 || { echo "FAIL: '$repo' is not a git repo"; exit 2; }
gitdir="$(cd "$repo" && cd "$(git rev-parse --git-dir)" && pwd)"
[[ -z "$(git -C "$repo" remote)" ]] || fail "remotes configured: $(git -C "$repo" remote | tr '\n' ' ')"
if git -C "$repo" config --local --get-regexp '^(remote\.|url\.|branch\..*\.remote|branch\..*\.pushremote)' >/dev/null 2>&1; then
  fail "remote-related config: $(git -C "$repo" config --local --get-regexp '^(remote\.|url\.|branch\..*\.remote|branch\..*\.pushremote)' | tr '\n' ';')"
fi
helpers="$(git -C "$repo" config --get-all credential.helper 2>/dev/null | grep -v '^$' || true)"
[[ -z "$helpers" ]] || fail "credential.helper set: $helpers"
hp="$(git -C "$repo" config --get core.hooksPath 2>/dev/null || true)"
[[ -z "$hp" ]] || fail "core.hooksPath redirected to '$hp' (pre-push guard could be bypassed)"
hook="$gitdir/hooks/pre-push"
if [[ ! -x "$hook" ]]; then fail "pre-push hook missing or not executable ($hook)"
else
  # The hook must refuse. It is run with no stdin from a temp dir; plan hook is a fixed 'exit 1' script.
  if (cd "$(mktemp -d)" && timeout 5 "$hook" origin https://example.invalid </dev/null >/dev/null 2>&1); then fail "pre-push hook exits 0 (must always fail)"; fi
fi
if [[ $bad -ne 0 ]]; then exit 1; fi
echo "PASS: $repo is local-only (no remote, no credential helper, pre-push refuses)"
