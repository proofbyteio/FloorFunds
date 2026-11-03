# Test matrix S1 (SSE2)

- **Author:** SSE2. Written Mon Nov 2, 2026, 12:18 PM PT.
- **Sources (md5):**

  | Source | md5 |
  |---|---|
  | PLAN §4.1 L232-251 | `ae18d66482606f9fdf334f0b953065b4` |
  | LD rev 11:41 | `88878681a39e23b82e30d6ad0c505647` |
  | contract `6e8a106` (v1-d2, not frozen) | `contract-v1.md` `bd0d2085` |

- **Run used:** `tools/run-checks.sh standin` at 12:17 PM PT (results in `/tmp/sse2-results4`, summary in `/tmp/sse2-run4.txt`; xml start times are UTC 20:17Z).
- **Totals:** **226 tests: 156 passed, 0 failed, 70 skipped (pending), 0 not run, 0 xfail.** xUnit v3 has no xfail. Pending means a Skip whose reason starts `PENDING <OQ>`.

  | Project | Passed | Skipped |
  |---|---|---|
  | ContractTests | 57 | 31 |
  | SecurityTests | 24 | 39 |
  | E2E | 75 | 0 |

- **What the statuses mean:**
  - **PASS / PARTIAL is not S1 sign-off.** There is no stand-in yet (`code/src/` is empty; SSE writes it after the freeze), so **no server-side behaviour has been tested**.
  - The passes are contract-artifact tests (schemas), mockup e2e tests against `login-hub-mockups/` (read only, `file://`, no network), oracle and tool self-tests.
  - Live tests skip with a reason when `LOGIN_STANDIN_URL`/`LOGIN_FRONT_URL` is unset.

## Per check

| Check | What (PLAN §4.1) | Runs now | Pending on | Status |
|---|---|---|---|---|
| 1 | cashier lands on hub with role tiles; `no_role_user` → `access_denied` + `reason=no_role` | E2E07 (hub tiles in mockup 06) | CT10/11: OQ-SSE2-04 control API, OQ-SSE2-05 sign-in driver | PARTIAL |
| 2 | First launch ≤5 min → no password; `method=hub_handoff`, original `auth_time` | E2E06 (02→03 TOTP→06 hub), E2E08 | CT12: OQ-SSE2-04 (`hub_launch` mint/expire) | PARTIAL |
| 3 | 2nd launch / >5 min / **no valid single-use `hub_launch` (contract §5.7; supersedes plan L130 rule 4, TL Addendum 2)** / after sign-out → password again | E2E09, E2E11 | CT13 cases second_launch, over_5_min, no_hub_launch, hub_launch_already_used, hub_launch_of_other_session, after_signout; SEC32. Pending: no stand-in in code/src/ | PARTIAL |
| 4 | Remote session → refused, screen 8 variant A, `reason=remote_session` | E2E05, E2E10, E2E16 (08 remote) | CT14: OQ-SSE2-04 | PARTIAL |
| **4a** | Fail closed on missing / bad sig / expired / replayed / unknown device; valid passes | AT01-AT08 (attestation schema: alg allowlist incl. `none`/HS256, no embedded keys, required claims, no extras); CS06; E2E12×2 (07 invalid → agentdown, also with remote=yes) | SEC10-SEC17 (SEC11 = 17 edge cases: nonce of another transaction / consumed / >60 s / burnt on failure / sign-in-for-hand-off, `device_id≠kid`, unknown / revoked / other-location kid, exp, `exp−iat` 61 s, future `iat`, alg none / HS256, embedded jwk, wrong key same kid, registry down): OQ-SSE2-04/OQ-33, OQ-SSE2-07/SSE OQ-11. AT09 (CR-21), AT10 (CR-20) | PARTIAL |
| **4b** | After `attestation_invalid` fallback: payment, ID scan, print refused until re-attest; screen 8 variant B text | E2E14 (08 agentdown banner + form → 07 agentdown), E2E15 (07 agentdown: Take payment / Scan ID / Print `aria-disabled=true` "Blocked"; Look up customer not blocked) | SEC50 (OQ-SSE2-04), SEC51/52 ×3 actions, SEC53, SEC54 (PC vs session scope): OQ-SSE2-34; contract finding CR-12 | PARTIAL |
| 5 | `cage_consumed`: backoffice cannot reuse | none | CT15: OQ-SSE2-04 | PENDING |
| 6 | 15 min idle (BFF + login cookie); `max_age=900` | E2E13 (idle sign-out states exist on sign-in and hub) | CT16/17: OQ-SSE2-04/15 | PARTIAL |
| 7 | Access-token claims vs schema; no roles; `perm_version`; org claims | CS01-CS04, CS07, CS08 (format asserted: bad uuid rejected) | CS09 (refresh ID-token nonce, OQ-SSE2-35/CR-03), CS10 (Entra `amr`, SSE OQ-22/CR-02), CT01 (live discovery; skip: no URL), CT18 | PARTIAL |
| 8 | Code replay, refresh reuse, wrong aud / bad sig / expired → 401 | CS05 | CT03/04/19-21: OQ-SSE2-04/05 | PARTIAL |
| 9 | Outbox fault → `temporarily_unavailable`, nothing issued | none | CT22: OQ-SSE2-04 | PENDING |
| 10 | 10 wrong passwords → locked 30 min | E2E02 (generic "Sign-in didn't work."), E2E03 | CT23, SEC20: OQ-SSE2-05, OQ-SSE2-13 (Nevada 3-try preset, CR-13) | PARTIAL |
| 11 | `check-no-standin.sh` on release build; stand-in refuses start without Development + `STANDIN_ALLOW=1`; API stub rejects `x_standin` in Production | TOOL01-TOOL08 (scripts' self-tests on fixtures) | CT02, CT24/25, SEC40×3: src absent, OQ-SSE2-17 | PARTIAL |
| 12 | **Manual (TL):** screens match UX 2/6/7/8; WCAG keyboard walk | E2E01×36 (every mockup page/state: no network request, exactly one h1), E2E20-22 keyboard walk incl. 08 agentdown "Continue", 08 remote "Try again" | the TL's manual walk (not automatable) | aid only, MANUAL sign-off |
| **pw-class** | Password rule (LD §7 L337-338 + TL 11:32): NFKC, then Lu / Ll / Nd / other; Nevada ≥12 and 2 of 4; PCI ≥12 letter + number; ≤128, no truncation, not containing the username, not on breached list | PW00 oracle theory ×23 (PW01-PW23: ASCII classes, ǅ, ᾈ, combining marks, ᵃ, ², 𝐀, Arabic-Indic digit, 11/12/128/129 lengths, username, `aaaaaaaaaaa1`) | PW30/31 server Nevada/PCI rejection (OQ-SSE2-05/29/13), PW32 length unit (OQ-SSE2-31), PW33 username match (OQ-SSE2-32), PW34 NFKC-equivalent sign-in (OQ-SSE2-33); PCI "letter/number" for Lt/Lo/non-ASCII Nd (OQ-SSE2-30) marked pending in the table | PARTIAL |
| **session-cap** | §7.4: default 12 h, configurable; **only** (a) 12 h default, (b) ≤15 min rejected | SEC60b (contract names `LoginService:Session:AbsoluteLifetime` = `12:00:00`) | SEC60 (live 12 h end: OQ-SSE2-04/15), SEC61×5 (`00:15:00`, `00:14:59`, `00:01:00`, `00:00:00`, `-01:00:00` → no start: skips until `src/Login.StandIn` exists), **SEC62 bounds: PENDING OQ-40 (Mohanraj)**, SEC63 real target (OQ-SSE2-36), SEC64 hand-off cap (OQ-SSE2-37) | PARTIAL |
| (none) | Enumeration / timing / session hygiene (SEC21-31), TestKit self-tests (KIT01-08), mockup smoke (E2E04) | KIT01-07, E2E04 | SEC21-31: OQ-SSE2-04/05/11/12/19/20/22; KIT08 (needs an unset target) | — |

## Per test id (from the run's xml)

| check | tests (P = passed, S = skipped/pending, F = failed; ×n = theory rows) |
|---|---|
| 1 | CT10:S, CT11:S, E2E07:P×4 |
| 2 | CT12:S, E2E06:P, E2E08:P |
| 3 | CT13:S, E2E09:P×4, E2E11:P×9, SEC32:S |
| 4 | CT14:S, E2E05:P, E2E10:P, E2E16:P |
| 4a | AT01:P, AT02:P×5, AT03:P×4, AT04:P, AT05:P×5, AT06:P, AT07:P, AT08:P, AT09:S, AT10:S, CS06:P, E2E12:P×2, SEC10:S, SEC11:S, SEC12:S, SEC13:S, SEC14:S, SEC15:S, SEC16:S, SEC17:S |
| 4b | E2E14:P, E2E15:P, SEC50:S, SEC51:S, SEC52:S, SEC53:S, SEC54:S |
| 5 | CT15:S |
| 6 | CT16:S, CT17:S, E2E13:P |
| 7 | CS01:P, CS02:P, CS03:P×8, CS04:P, CS07:P, CS08:P, CS09:S, CS10:S, CT01:S, CT18:S |
| 8 | CS05:P, CT03:S, CT04:S×3, CT19:S, CT20:S, CT21:S |
| 9 | CT22:S |
| 10 | CT23:S, E2E02:P, E2E03:P, SEC20:S |
| 11 | CT02:S, CT24:S, CT25:S, SEC40:S×3, TOOL01:P, TOOL02:P×3, TOOL03:P, TOOL04:P, TOOL05:P, TOOL06:P×3, TOOL07:P, TOOL08:P |
| 12 | E2E01:P×36, E2E20:P×7, E2E21:P, E2E22:P |
| pw-class | PW00:P×23, PW30:S, PW31:S, PW32:S, PW33:S, PW34:S |
| session-cap | SEC60:S, SEC60b:P, SEC61:S×5, SEC62:S, SEC63:S, SEC64:S |
| (none) | E2E04:P, KIT01:P, KIT02:P, KIT03:P, KIT04:P×5, KIT05:P, KIT06:P, KIT07:P, KIT08:S, SEC21:S, SEC22:S, SEC23:S, SEC24:S, SEC25:S, SEC26:S, SEC27:S, SEC28:S, SEC29:S, SEC30:S, SEC31:S |

## Open questions that drive pending tests (OQ-SSE2-n, for the TL)

| OQ | Question |
|---|---|
| 04 | Stand-in control API (`/_standin/*`): routes and payloads for clock, faults, attestation mode per PC (OQ-33 cookie name and values), `hub_launch` mint/expire, audit read-back, reset. **Blocks about 60 tests** (CR-05). |
| 05 | How a test drives DB sign-in + TOTP on the stand-in: field names, antiforgery, TOTP secret of fixture users. |
| 07 | = SSE OQ-11: an invalid attestation on plain sign-in. With CR-25 (expiry after 60 s drops the remote check). |
| 11 | Pass threshold for "same timing" (enumeration). |
| 12 | Rate limit: 429 vs the shared message; session-id rotation on sign-in (fixation) is tracked as 40. |
| 13 | Lockout preset of the stand-in fixture location: PCI 10/30 or Nevada 3 (LD L338) (CR-13). |
| 15 | How the BFF stub's 15-min idle follows the test clock. |
| 17 | Start command, exit code and log line for refused starts (stand-in without `STANDIN_ALLOW`, API stub in Production). |
| 19/20 | `redirect_uri` / `post_logout_redirect_uri` matching rules. |
| 22 | How a test presents a non-allowlisted source IP. |
| 29 | Route by which a test sets a password (pw-class server tests). |
| 30 | PCI "letter" and "number" for Lt / Lm / Lo / non-ASCII Nd. |
| 31 | Length unit (UTF-16, code points, graphemes), and counted before or after NFKC. |
| 32 | Username match: case, NFKC, spaces. |
| 33 | Does an NFKC-equivalent password verify at sign-in? |
| 34 | 4b: which component refuses payment / ID scan / print, and is the scope the PC or the login session? (CR-12) |
| 35 | Refresh-issued ID token `nonce` (CR-03). |
| 36 | Real target: how a test starts the real service with a bad cap. |
| 37 | Does a hub hand-off's refresh family keep the remaining cap? (CR-33) |
| 38 | **New:** is the attestation nonce burnt when a later check fails? (CR-23b) |
| 39 | **New:** is a revoked device or a device at another location `attestation_invalid`? (CR-22) |
| 40 | **New:** is the login-session id rotated on sign-in (session fixation)? |

OQ-SSE2-01..03, 06, 08-10, 14, 16, 18, 21, 23-28 were raised in SSE2's 11:4x report. They are either answered by contract v1-d2 (06, 18 → `hub_launch` and attestation format) or no longer drive a test.
**Mohanraj:** OQ-40 (cap: who changes it, the range).


## Revision Mon Nov 2, 8:26 PM PT (SSE2): TL Addendum 2 (rulings md5 5bcaedc9) at feat/contract-v1 38ad62e

Run: `tools/run-checks.sh standin` at 8:24:36-8:25:12 PM PT, contract working tree byte-identical to 38ad62e before and after.
Added 8:32 PM PT for [Confirmed DEC-084] (contract e0f946a, 5.12/6.9): **CT28** ×2 (check 3, /admin super|partner never gets the Cash Advance hand-off, `access_denied`, audit `not_location_user`; pending ControlApi) and **SEC56** ×2 (check 4b, /admin cage action → 403 `access_denied`; pending CageAction). **E2E07** admin row changed to Back office + Reports (mockup 06 rev 8:29 PM PT follows DEC-084). Not yet covered: [Confirmed DEC-085] `recovery_reenroll` sessions (99f2b7d), next pass.

**TOTAL 291 tests: 200 pass, 0 fail, 91 skip (pending), 0 xfail** (run 8:32:16 PM PT, tree = 99f2b7d at start; a snapshot run of 99f2b7d's contract gave the same apart from TOOL08, which needs a git repo). Not green: no stand-in exists, so every server-behaviour test is pending.

| New / changed ID | Check | Ruling | Status at 38ad62e |
|---|---|---|---|
| CS09 id_token_code needs nonce / id_token_refresh forbids it | 7 | CR-03 | PASS |
| CS10 amr enum closed RFC 8176 subset (no ngcmfa / wiaormfa) | 7 | CR-02 | PASS |
| CS11 $defs.id rejects non-UUID with format assertion OFF; CS11b README states format-assertion rule | 7 | CR-01 | PASS |
| CS12 errors.md has unsupported_response_type / unsupported_grant_type; invalid_request row has no live response_type case | 8 | CR-04 | PASS |
| CS13 §6.3 per-partner preset, pci + nevada | 10 | CR-13 | PASS |
| CS14 §6.9 cage_action at Cash Advance | 4b | CR-12 | PASS |
| CS15 attest-nonce in contract | 4a | CR-25 | PASS |
| CS16 §6.7 revoked/disabled/location | 4a | CR-22 | PASS |
| CS17 §6.7 ±30 s, ≤60 s, exp > iat | 4a | CR-23 | PASS |
| CS18 §6.7 nonce taken at start | 4a | CR-23b | PASS |
| CS19 ×2 standin-control.md / ui-driver.md exist | contract-gate | CR-05 | PASS |
| CS20 §1.6 token endpoint override | 8 | CR-14 | PASS |
| CS21 ×3 no attested / device_id / attestation claim in access token | 4b | CR-12 | PASS |
| AT01-AT11 rewritten (aud, purpose, typ; AT09 closed header) | 4a | CR-20/21 | PASS |
| SEC10 + revoked_device, other_location | 4a | CR-22 | pending (stand-in) |
| SEC11 cases rewritten: skew ±30 s, nonce burnt on every outcome, parallel submit, kid unenrolled/revoked/disabled/other location, exp > iat, crit, unknown header | 4a | CR-21/22/23/23b | pending |
| SEC15 verified remote_session refuses (8A); SEC16 ×6 invalid attestation proceeds, attestation=invalid, no 8A, next hand-off attestation_invalid; SEC17 attest-nonce invalidates earlier; SEC18 ×4 cross-purpose; SEC19 slow typing | 4a | CR-25/CR-20 | pending |
| SEC51/52 ×3 actions, SEC54 one attestation one action, SEC55 ×4 | 4b | CR-12 | pending (BFF stub not built) |
| SEC24 reissue after password; SEC24b again after MFA | (none) | OQ-SSE2-40 | pending |
| CT23 pci 10/30; CT26 nevada 3/30; CT27 per-partner | 10 | CR-13 | pending |
| CTL01-CTL13 (control API), CTL14 doc violation ids (runs, PASS), UID01-UID05 (UI driver) | ctl | CR-05 | pending except CTL14 |
| PW24-PW28 new cases; PW00 now uses Rune length after NFKC + username rule; PW32/PW33 oracle self-tests | pw-class | OQ-SSE2-31/32 | PASS (server PW30/31/34 pending) |
| SEC63 deferred to S2; SEC64 ruled (cap from original sign-in) | session-cap | OQ-SSE2-36/37 | deferred / pending |

Also fixed: CS02 sample discovery document lacked the three keys added by TL Decisions OQ-17/OQ-21 (my stale fixture, not a contract fault).

## Revision Mon Nov 2, 8:55 PM PT (SSE2): freeze candidate feat/contract-v1 e363090

Totals at e363090 (`tools/run-checks.sh`): working tree 428 tests / 334 pass / 0 fail / 94 pending. On an exact export (`git archive e363090 contract`): 428 / 333 / 1 / 94. The one failure is TOOL08, which can't pass on an export because it isn't a git repo; the TL ruled at 8:47 PM that this is not a blocker.

New tests in `tests/Login.ContractTests/PreFreezeSchemaTests.cs` (check 7). Every baseline is built explicitly, with no builder defaults.
- **SK01-SK13 (`session_kind`, 7.8, DEC-085/087).** Required in every token; every bad value is rejected. A `recovery_reenroll` token is rejected for every app. A normal pwd-only token is accepted (the amr revert). Covers the audit `session_kind` rules, reenrolled (`recovery_reenroll` only) and codes_issued (`normal` or `recovery_reenroll`, required). Also: the 7.8 text, the `/mfa/reenroll` spelling, and auth.mfa.failed step=reenroll.
- **SK14 (TL Decision 8:47/8:48 PM).** The 7.8 text has 5 tries per 15 min per user, configurable (`LoginService:Mfa:ReenrollRateLimit`), 429, the same generic message, and `auth.mfa.failed reason=rate_limited`. The server version is PENDING until the stand-in exists.
- **SK15 (OQ-SSE2-48).** auth.mfa.failed step=reenroll details are closed, so a totp_code or code echo is rejected.
- **SK16.** `reason=rate_limited` is valid only with step=reenroll, a recovery_reenroll session and auth.mfa.failed.
- **AD01-AD04 (`admin_type`, 8.12).**
- **LOC01-LOC02 (8.11/8.13).** LOC03 (duplicates, OQ-SSE2-42) and LOC04 (OQ-SSE2-44) are pending.
- **CAP01-CAP04 (7.4/8.15).**
  - cap_changed is closed.
  - change_denied: `actor.user_id` null is allowed. Details are closed and require `setting`, `attempted_value` (TimeSpan only, so no free text and no secret) and `reason`.
  - The reasons are exactly `not_admin`, `wrong_partner` and `out_of_range`.
  - `admin_type` is required for wrong_partner and out_of_range, and forbidden for not_admin. A partner admin requires `scope.partner_id`.
- **PH01 (DEC-088/089).** The placeholder app codes are marked PLACEHOLDER, and back_office and reports are separate.

### Safe defaults for open questions (the test runs this default until ruled)
| OQ | Safe default used by tests |
|---|---|
| OQ-SSE2-11 | "Same timing" for enumeration: the median over 50 tries differs by under 50 ms, and no case is consistently slower. Pending until ruled. |
| OQ-SSE2-12 | Password sign-in rate limit: the shared generic message, with no 429 that would show the limit was hit. The re-enrol POST is the exception: 429 + the generic message, per the TL 8:47 PM ruling. |
| OQ-SSE2-15 | The BFF stub's idle clock follows the stand-in's test clock (`/standin/control` time advance). No wall-clock sleeps. |
| OQ-SSE2-17 | A refused start exits non-zero (≠0) and logs one line naming the missing guard. Tests assert only the non-zero exit and that nothing is listening on the port. |
| OQ-SSE2-19/20 | Pending. No assertion until ruled. |
| OQ-SSE2-22 | Non-allowlisted source IP is presented through the stand-in control (a test-only override). The test never uses X-Forwarded-For, never uses ports 8080/8081 and never touches `ip-allowlist-poc/data/allowlist.db`. |
| OQ-SSE2-30 | PCI letter = Unicode L* (all categories); number = Nd only. Lt/Lm/Lo and non-ASCII Nd are pending. |
| OQ-SSE2-42 | Duplicate `location_ids` are treated as invalid at the server. Pending, because the schema has no uniqueItems. |
| OQ-SSE2-43 | Cap-change endpoint not specified. Server tests SEC80-86 stay pending. |
| OQ-SSE2-44 | The stand-in never writes `admin_type` on a `/l/{slug}` session event. Pending at the server. |
| OQ-SSE2-45 | **Ruled (TL 8:47 PM).** 5 per 15 min per user, configurable, 429, the generic message, auth.mfa.failed reason=rate_limited. |
| OQ-SSE2-46 | **Ruled (TL 8:47 PM).** Fixed in e363090. |
| OQ-SSE2-47 | cap_changed for a partner admin doesn't require `scope.partner_id`. Post-freeze. |
| OQ-SSE2-48 | **Ruled (TL 8:47 PM).** Fixed in e363090. |
| OQ-SSE2-49 | The re-enrol "generic message" is not named. The only error-area string is "Sign-in didn't work.", which comes from a TL ruling (CR-05), not the LD. Tests assert only `error-area` plus 429, never the text. Post-freeze. |

## Revision Mon Nov 2, 9:45 PM PT (SSE2): SI1 stand-in feat/si1-standin 8aab98a, base 18090
Command: `. /tmp/sse2-work/env.sh && STANDIN_PIDFILE=/tmp/sse2-si1-8aab98a/pid tools/run-checks.sh standin` → **514 / 420 pass / 5 fail / 89 pending / 0 not run** (21:36:55–21:38:02 PST).

| Id | Contract | What | 8aab98a |
|---|---|---|---|
| HO01 | 5.6, 5.10 | happy path: code+iss, one signed_in method=hub_handoff, original auth_time+sid, schema-valid | PASS |
| HO02 / HO03 / HO03b | 5.7 | missing hub_launch; 61 s expired (control +58 s); **60000 ms + latency (TL ms ruling)** | PASS / PASS / **FAIL** (60003 ms accepted, A42 floor) |
| HO04 / HO05 / HO07 | 5.7 | replay; another session's value (consumed); no session (consumed) | PASS |
| HO06 | 5.7, A41 | different client: no hand-off, no cage_consumed; consumption = OQ-SSE2-56 | PASS |
| HO08 / HO09 | 5.12 / 5.13 | admin → not_location_user; recovery → recovery_session, single-use code | PASS |
| HO10 / HO10b | 5.8(c) | interstitial no-store, antiforgery, connect-src; **exact CSP pin (regression for 9a14163 miss)** | PASS / PASS |
| HO11–HO14 | 5.5, 5.8 | attestation only in POST body; completion replay; tx/session mismatch; bad attestations; remote_session → screen-8a | PASS |
| HO15 | 5.1 r3/6 | second hand-off re-prompt no event; **no SSO after cage_consumed**; prompt=none → interaction_required | PASS |
| HO16 / HO18 / HO18b / HO18c | 5.1 r1 | +301 s; **> 300000 ms (300002) refused**; < 300000 ms accepted; exactly 300000 ms | PASS / **FAIL** / PASS / PENDING OQ-SSE2-55 |
| HO17 | 5.7 URL exception | app log has no raw hub_launch/code/state | **FAIL** |
| HO19 (×2) | 5.1 r2, 8.10 | amr [pwd] / [wiaormfa] refused, no event, no tx | PASS |
| HO20 (×9) | 5.8(c), TL 9:32 item 6 | CSP frame-ancestors 'none' + default-src + no-store on signin, totp, hub, apps, admin, admin-mfa, interstitial, re-sign-in, screen-8a | PASS |
| HO21 | ui-driver | interstitial ui-driver id | PENDING OQ-SSE2-57 |
| D93_01–03 | DEC-093 | cashier at /admin no session; admin at /l/ no session; admin authorize never sent to /l/ | PASS |
| D93_04 / 05 | DEC-093 | admin → /admin, location user → own /l/{slug} after re-enrolment | PENDING OQ-SSE2-54 (A36 not built) |
| PKJ01–12 | 7.6 | private_key_jwt negatives (alg none/HS/PS, keys, jwk/jku, iss/sub, aud, jti, lifetime, malformed, Basic+assertion, type, rotation) | PASS 12/12 |
| SEC70–79c | 4.4, 7.2, 9, 10, 11, LD | adversarial set | 15 PASS, **SEC76, SEC77 FAIL**, SEC75b/79c PENDING (OQ-SSE2-52/11) |
| FS01 | B2 | no private key tracked on si1, sse2, contract | PASS |
| FS02 (×7) | B2 | negative controls: SSE script fails on .pem/.key, PEM markers (PKCS8, RSA, ENCRYPTED), RSA/EC JWK with d | PASS |
| FS03 (×2) | B2 | probe: OKP JWK, key deleted in history | logged: **not detected** (info) |
| FS04 | B2 | FIXTURE client keys under /tmp, mode 0600 | PASS |

### DEC-095 (post-S1, matrix rows only, no tests) [Steering 9:25 PM PT]
| Id | Rule | Status |
|---|---|---|
| D95-01 | super admin can issue a re-enrolment code for any partner | post-S1, no test |
| D95-02 | partner admin can issue only for its own partner; other partner → refused `wrong_partner` | post-S1, no test |
| D95-03 | location user issuing → refused `not_admin` | post-S1, no test |
| D95-04 | the code is single use and short-lived | post-S1, no test |

### New open questions (for the TL)
- **OQ-SSE2-50**: TOTP single use and attempt lockout come from the LD (L331, L335, L218), not contract-v1. SEC76/77 fail on that basis. Are they S1 requirements?
- **OQ-SSE2-51**: what is the TOTP attempt limit (count and window)?
- **OQ-SSE2-52**: 7.2 does not define replay inside the 30 s leeway. 8aab98a gives 3 live chains from one refresh token.
- **OQ-SSE2-53**: stand-in builds without the hand-off or private_key_jwt: tests skip as pending, not fail. OK?
- **OQ-SSE2-54**: DEC-093 re-enrolment routes are not built (A36). D93_04/05 stay pending until SI1 step 3.
- **OQ-SSE2-55**: the exact 300000 ms / 60000 ms boundary is not testable. standin-control 2.2 has whole-second advance on a running wall clock. Add a clock freeze or set, or accept the ±latency method?
- **OQ-SSE2-56**: hub_launch presented to another client: "ignore" (5.7, A41: still usable) vs the TL's "refused". Must it be consumed?
- **OQ-SSE2-57**: what is the ui-driver id for the hand-off interstitial (5.8)? There is none in ui-driver.md or SSE code (A38).
- **OQ-SSE2-58**: 5.8(c) "strict CSP" vs `script-src 'self' 'unsafe-inline'` on every page. Also X-Frame-Options SAMEORIGIN on some pages next to frame-ancestors 'none'.
