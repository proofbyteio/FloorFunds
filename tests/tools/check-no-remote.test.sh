#!/usr/bin/env bash
# Tests for tools/check-no-remote.sh (TLD-S2B-01). Throwaway repos under /tmp only; nothing is fetched or pushed.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; chk="$here/../../tools/check-no-remote.sh"
URL=https://github.com/proofbyteio/FloorFunds.git
pass=0; failc=0
mk() { local d; d="$(mktemp -d /tmp/sse2-cnr.XXXXXX)"; git -C "$d" init -q; git -C "$d" config credential.helper ""
  cat > "$d/.git/hooks/pre-push" <<'H'
#!/bin/sh
case "$2" in https://github.com/proofbyteio/FloorFunds.git|https://github.com/proofbyteio/floorfunds.git) exit 0;; esac
exit 1
H
  chmod +x "$d/.git/hooks/pre-push"; git -C "$d" remote add origin "$URL"; echo "$d"; }
t() { local id="$1" want="$2" d="$3"; out="$(bash "$chk" "$d" 2>&1)"; rc=$?
  if { [[ $want == pass ]] && [[ $rc -eq 0 ]]; } || { [[ $want == fail ]] && [[ $rc -ne 0 ]]; }; then echo "ok   $id ($want)"; pass=$((pass+1))
  else echo "FAIL $id want=$want rc=$rc :: $out"; failc=$((failc+1)); fi; rm -rf "$d"; }
d=$(mk); t CNR01-exact-origin pass "$d"
d=$(mk); git -C "$d" config remote.origin.pushurl "$URL"; t CNR02-pushurl-same pass "$d"
d=$(mk); git -C "$d" config branch.main.remote origin; t CNR03-branch-origin pass "$d"
d=$(mktemp -d /tmp/sse2-cnr.XXXXXX); git -C "$d" init -q; t CNR04-no-remote fail "$d"
d=$(mk); git -C "$d" remote set-url origin https://github.com/proofbyteio/Other.git; t CNR05-wrong-url fail "$d"
d=$(mk); git -C "$d" remote set-url origin https://github.com/proofbyteio/floorfunds.git; t CNR06-case-variant fail "$d"
d=$(mk); git -C "$d" remote set-url origin git@github.com:proofbyteio/FloorFunds.git; t CNR07-ssh-form fail "$d"
d=$(mk); git -C "$d" config remote.origin.pushurl https://example.invalid/x.git; t CNR08-other-pushurl fail "$d"
d=$(mk); git -C "$d" remote set-url --add origin https://example.invalid/x.git; t CNR09-second-fetch-url fail "$d"
d=$(mk); git -C "$d" remote add upstream "$URL"; t CNR10-second-remote fail "$d"
d=$(mk); git -C "$d" remote rename origin gh; t CNR11-renamed fail "$d"
d=$(mk); git -C "$d" config url.https://example.invalid/.insteadOf https://github.com/; t CNR12-insteadof fail "$d"
d=$(mk); git -C "$d" config url.https://example.invalid/.pushInsteadOf https://github.com/; t CNR13-pushinsteadof fail "$d"
d=$(mk); git -C "$d" config branch.main.pushRemote https://example.invalid/x.git; t CNR14-branch-pushremote fail "$d"
d=$(mk); git -C "$d" config remote.pushDefault elsewhere; t CNR15-pushdefault fail "$d"
d=$(mk); git -C "$d" config credential.helper store; t CNR16-cred-helper fail "$d"
d=$(mk); git -C "$d" config core.hooksPath /tmp; t CNR17-hookspath fail "$d"
d=$(mk); rm "$d/.git/hooks/pre-push"; t CNR18-no-hook fail "$d"
d=$(mk); printf '#!/bin/sh\nexit 0\n' > "$d/.git/hooks/pre-push"; t CNR19-hook-allows-all fail "$d"
d=$(mk); chmod -x "$d/.git/hooks/pre-push"; t CNR20-hook-not-exec fail "$d"
wt() { local d w; d=$(mk); git -C "$d" -c user.name=t -c user.email=t@local.invalid commit -q --allow-empty -m init
  w="$d-wt"; git -C "$d" worktree add -q --detach "$w"; echo "$d $w"; }
read d w < <(wt); t CNR21-linked-worktree pass "$w"; rm -rf "$d"
read d w < <(wt); rm "$d/.git/hooks/pre-push"; t CNR22-linked-worktree-no-hook fail "$w"; rm -rf "$d"
echo "check-no-remote tests: $pass pass, $failc fail"; [[ $failc -eq 0 ]]
