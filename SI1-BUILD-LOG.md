
## TL 9:32 PM steering + build-order step (2) ui/ pages

Rulings logged:
- A43 TL Decision: hub token response keeps omitting access_token in the stand-in. **v1.1 MUST-FIX (SA):** add a hub audience to the access-token aud enum, scoped to /apps only, no roles, and have the stand-in issue it (RFC 6749 §5.1, OIDC Core 3.1.3.3 require access_token).
- A41 accepted. Checkpoint (1) accepted at 8aab98a.
- A42 CHANGED: compare in ms; refuse > 300000 ms (no rounding down, 300.4 s refused); same for hub_launch (> 60000 ms). `Handoff.Over()` compares ticks. Boundary tests at 300000/300001 (+300400, +1 tick) and 60000/60001 ms run as a startup self-test (`A42 boundary selftest ... PASS` in si1.log and evidence). [GAP] the frozen clock control advances whole seconds and runs from real time, so exact-ms cases can't be hit live; live checks are 299 s accept / 300 s refuse and 59 s accept / 60 s refuse.
- CSP: every stand-in response gets frame-ancestors 'none' (global fallback middleware); ui/ pages send the full page CSP. Driver asserts this over every response of the run.

Step (2):
- ui/login-hub templates (README lists UX sources + md5s). [A46] HTML templates instead of Angular (no packages).
- [A44] after attestation_invalid at the interstitial (5.5) the re-prompt is screen-8b wrapping the page-resign-in ids (redirect adds `&agent=down`; display-only). The banner is not kept after a failed re-prompt POST.
- [A45] the allowlist 403 body is unchanged (DEC-094, D-88); only the CSP header is added by the global fallback.
- Evidence: /tmp/sse-d0/evidence-step2.txt, 158/158 live on base 18080 (guard: all 3 ports owned by our pid).
- Evidence md5: 7edafa2569cf9c8681b810b21db2f56a

## TL 9:38 PM rulings (step 2 accepted)

- CSP: every inline script moved to same-origin files (ui/login-hub/js/signin.js, handoff.js); page CSP is now `script-src 'self'; style-src 'self'`, no 'unsafe-inline' anywhere. Driver fails if any response's CSP has 'unsafe-inline' (script-src or anywhere) and if the sign-in page has an inline <script> or style=.
- A44 fix: the re-prompt variant (resign / 8B) is held on the SignInTx; every failed password retry re-renders it, so the 8B message stays. Tests: failed retry from 8B -> screen-8b + 8B string + generic error; failed retry from plain re-prompt -> page-resign-in, no 8B. [GAP] a wrong TOTP after an 8B retry shows plain screen-totp (no 8B banner); not asked for.
- v1.1: change the contract/ui-driver.md header to 'FROZEN v1 @ e363090' (text only; contract/ frozen, not edited here).
- A46 accepted: pages are server-rendered (Razor in production). Angular later needs a TL package ruling first: npm registry only, exact pins, a lock file and integrity hashes.
- signout-confirm and interstitial testids accepted for v1.1.
- Evidence: /tmp/sse-d0/evidence-csp.txt, 165/165 live on base 18080 (guard ok, pid 660610), md5 c92e44a1b444f647d6cfa76b272ac736.

## TL 9:42 PM: SSE2 CONTRACT-REVIEW-v1 findings (step 3 PAUSED, parked at /tmp/sse-d0/park-step3, not in this commit)

Source: `git show sse2/tests-docs:docs/CONTRACT-REVIEW-v1.md` (@21449f9). One commit, each item with a live test (driver /tmp/sse-d0/e2e: findings.py, logs.py, edits in drive.py/handoff.py).
- HO18/HO03b + OQ-55: TestClock gains freeze; `POST /_standin/clock/freeze {"frozen":bool}`; `/clock/advance` takes exactly one of `seconds` or `ms` (both -> 400); reset unfreezes. [v1.1 control-API addition; contract/ frozen, not edited.] Live, frozen clock: hub_launch 60000 ms accepted, 60001/60003 ms notfromhub; auth age 300000 ms accepted, 300001/300002 ms re-prompt.
- SEC76: Totp.Accept checks steps cur-1..cur+1 that are > user.LastTotpStep and records the step; used on location TOTP and /admin/mfa. Control `/users/{u}/totp` returns the earliest unused step's code, 409 when the window is spent (driver then advances 30 s). Tests: same code in a 2nd browser refused, location and /admin.
- SEC77: one per-user counter (SignIn.CountFailure) for wrong passwords and wrong TOTPs; Nevada 3, PCI 10; locked -> right code and right password refused. /admin password and mfa use it too; locked event carries admin_type and scope {partner_id, location_ids:[]}.
  - [ASSUMPTION A53] a right password no longer resets the counter; only a full sign-in success does (else password + wrong TOTP never locks). Test: 3 x (right pw + wrong TOTP) locks Nevada.
  - [ASSUMPTION A54] /admin limit = the admin's partner preset; super admin (no partner) = 3. [GAP] super-admin lockout not exercised live (no super fixture); its scope.partner_id would be null - schema fit unchecked.
- HO17: Microsoft.AspNetCore logs below Warning dropped (request lines carried query strings). Test: after the full run, every secret-bearing value the driver sent/received (hub_launch, code, code_challenge/verifier, state, nonce, attestation, passwords, tokens, antiforgery, client secrets, recovery codes; values >= 6 chars, pure-digit TOTP codes excluded [GAP]) is grepped in every log in the state dir: none found.
- F7 X-Frame-Options: antiforgery's SAMEORIGIN suppressed; global `X-Frame-Options: DENY`. Test: no response SAMEORIGIN, all DENY.
- OQ-11: PasswordMatches always runs one PBKDF2 (dummy credential for unknown users, also before the lockout check). Test: median timing unknown vs known-wrong within 0.5x-2x.
- OQ-52: RefreshRec.Successor caches the successor body; a replay inside 30 s returns the same pair, mints nothing, no event. Test: r2 == r1.
- F12 discovery per listener: front copy omits token/revocation endpoints and their auth-method keys; back copy gives them at the back listener URL. [TL DECISION / v1.1] this departs from frozen contract 2.1/2.9 ("identical copies"). Tests: key sets + every listed endpoint on that listener answers (not 404).
- OQ-56 (replaces A41): hub_launch is looked up and burned for any client; only Cash Advance uses it. Test changed: back office ignores it (normal SSO), then Cash Advance -> notfromhub.
- F14 fixtures: client secrets and recovery codes removed from fixtures.json ("secret": "GENERATED", "recovery_codes_count": 2); generated once per process start into <state dir>/fixture-secrets.json (0600), stable across reset; driver reads it. check-fixture-secrets.sh now also catches OKP "d", client secrets / recovery codes, and scans all of git history (`git rev-list`). Self-tested on a throwaway repo (OKP d only in history -> FAIL). SSE2 wires it into tools/.
  - [ASSUMPTION A55] the old secrets/recovery codes remain in earlier commits (eb0fa5d .. c4da996); they are dead values now, reported as WARN, not FAIL. Rewriting history needs a TL ruling (not done).

## TL 9:50 PM rulings (one commit; 10a1ca4 accepted)
1. **A55 [TL Decision 9:50 PM]: history NOT rewritten.** check-fixture-secrets.sh carries an explicit list (KNOWN) of the 13 commits eb0fa5d..c4da996:
   eb0fa5d532c86c9b7d6e6d4b67a246e0b3804436, 97bfdb2fee1fe5205b8f766c511cf99350f7377a, 3087f582d44ffd3a339eff4b04c32d2f94dda29f, dd59e8582ee44dd17b66ba0d81e9d23ae45244fa, f63b2c8b86c85d9fa316acccaea3bab18bb154c9, 40da81dcdcc27c5291a36bf1f56808c38a6f7d98, 1ee0939e38f34a488ef38628adb160e29fc926fb, df0b5d6a21d371d94dcd8379eb26b735526d5f2a, ed0ecc127cd139fad03f8af5eaebdd5642835c83, 9a141632f8695f018d49f54c11f052a3a30aba1b, 8aab98ab6d6c60437606cc9189dc6a977b7a7a6e, f630a8d3211151627e1c6e5b11862aee68e86399, c4da996adaa939d9724b221058cd4078f7b04bba.
   - The scan is now per commit (rev-list, ls-tree; results cached per blob). A client-secret/recovery-code hit is WARN only if the commit is in KNOWN and FAIL in any other commit. A private key is FAIL in every commit.
   - **Found and fixed:** the old client-secret test had a `A && !B || echo` precedence bug. It reported a "client secret" for every fixtures.json, even with no "secret" key at all. With the fix, the real hits on feat/si1-standin are in 7 of the 13 listed commits: 1ee0939, df0b5d6, ed0ecc1 (client secret), and 9a14163, 8aab98a, f630a8d, c4da996 (secret + recovery codes). eb0fa5d, 97bfdb2, 3087f58, dd59e85, f63b2c8 and 40da81d have no hit. The list is kept exactly as ruled. Branch result: OK, warn=1, 72 commits scanned.
   - Throwaway repo test (GIT_INDEX_FILE unset, mktemp under /tmp/sse-d0, deleted afterwards): T1 leak commit not listed -> FAIL rc=1; T2 the same commit added to the list -> WARN rc=0; T3 private key in a listed commit -> FAIL rc=1.
2. **Discovery reverted to frozen 2.1/2.9:** one Build(issuer, keys), with byte-identical discovery+JWKS on the front and back listeners. Test TL950 compares the bytes. The per-listener F12 tests are removed. **v1.1 for SA:** SSE2's F12 finding. The front copy lists token/revocation URLs that 404 on the front listener, so the SA should define how URLs map between the front and back listeners.
3. **v1.1 for standin-control.md** (contract/ not edited): /_standin/clock/freeze and ms-granularity /_standin/clock/advance (`{seconds|ms}`, exactly one).
4. **A53** accepted (no change).
5. **A54 [TL Decision 9:50 PM, interim]: super admin lockout limit 3.**
   - Added a FIXTURE user super_admin (admin_type super, no partner_id, user_id ...0c0a). Its password is "GENERATED": it is generated at process start into fixture-secrets.json["passwords"], and the driver reads it from there.
   - AdminScope omits partner_id when it is null, so the key is ABSENT (not null) in super events.
   - Live tests: 2 wrong -> not locked; the 3rd -> auth.account.locked admin_type super; the right password is then refused. Every super event has no scope.partner_id and validates against contract/audit-events.schema.json.
   - [ASSUMPTION A56] super_admin has no TOTP secret, so the super MFA lockout path is not tested (TL listed it as optional).
- Live run: base 18080, own pid 711044 (port guard ok before start), **TOTAL 188 / 188**. That is 189 - 4 F12 + 3 new.
- Evidence /tmp/sse-d0/evidence-950.txt, md5 **a88e6e466f1528bee57dd2f25650bc51**. Server stopped. allowlist.db is untouched (temp copy only).
