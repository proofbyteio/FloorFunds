# Test matrix S1 (SSE2)

- **Author:** SSE2. Written Mon Nov 2, 2026, 12:45 PM PT.
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
| 3 | 2nd launch / >5 min / not from hub / after sign-out → password again | E2E09, E2E11 | CT13, SEC32: OQ-SSE2-04. Plan text still says "no hub `state`"; contract now uses `hub_launch` (§5.7) | PARTIAL |
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
