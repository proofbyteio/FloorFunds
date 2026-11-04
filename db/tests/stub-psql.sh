#!/usr/bin/env bash
# TEST-ONLY stand-in for the superuser psql (db/tests/k1-kit-tests.sh). Never connects anywhere.
# Logs every script it is sent to $KIT_STUB_LOG (one "=== call" block per invocation) and answers the
# read-only preflight with the canned catalog findings in $KIT_STUB_PREFLIGHT.
in="$(cat)"
{ echo "=== call"; printf '%s\n' "$in"; } >>"$KIT_STUB_LOG"
# The real preflight always ends with "END floorfunds-kit-preflight"; KIT_STUB_NO_END=1 omits it (KF3 tests).
if [[ "$in" == *"floorfunds-kit preflight"* ]]; then
  cat "$KIT_STUB_PREFLIGHT"
  [[ "${KIT_STUB_NO_END:-0}" == 1 ]] || echo "END floorfunds-kit-preflight"
fi
exit 0
