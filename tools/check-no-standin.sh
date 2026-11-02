#!/usr/bin/env bash
# check-no-standin.sh <release-output-dir>
# Build plan §3.5: fails if any release output contains Login.StandIn* (file name OR a reference inside a file, e.g. *.deps.json).
# Owner: SSE2 (writes), SSE (reviews). Exit 0 = clean, 1 = stand-in found, 2 = usage / dir missing (never passes silently).
set -euo pipefail
dir="${1:-}"
if [[ -z "$dir" ]]; then echo "usage: $0 <release-output-dir>" >&2; exit 2; fi
if [[ ! -d "$dir" ]]; then echo "FAIL: release output dir '$dir' does not exist (refusing to pass on nothing)" >&2; exit 2; fi
found=0
while IFS= read -r -d '' f; do echo "FAIL: stand-in file in release output: $f"; found=1; done \
  < <(find "$dir" -iname '*login.standin*' -print0)
# Text and binary files (assembly names sit in metadata as plain ASCII, so -a catches them).
while IFS= read -r f; do echo "FAIL: release file references Login.StandIn: $f"; found=1; done \
  < <(grep -rail --exclude-dir=.git 'login\.standin' "$dir" || true)
if [[ $found -ne 0 ]]; then exit 1; fi
echo "PASS: no Login.StandIn* in $dir"
