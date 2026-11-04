#!/usr/bin/env bash
# TEST-ONLY stand-in for the superuser psql (db/tests/k1-kit-tests.sh). Never connects anywhere.
# Logs every script it is sent to $KIT_STUB_LOG (one "=== call" block per invocation) and answers the
# read-only preflight with the canned catalog findings in $KIT_STUB_PREFLIGHT.
in="$(cat)"
{ echo "=== call"; printf '%s\n' "$in"; } >>"$KIT_STUB_LOG"
if [[ "$in" == *"floorfunds-kit preflight"* ]]; then cat "$KIT_STUB_PREFLIGHT"; fi
exit 0
