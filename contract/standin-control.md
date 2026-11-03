# Stand-in control API (`standin-only`; DRAFT v1-d4, not frozen)

Extends contract-v1.md §1.5. **Never part of the client contract.** Every test that uses it is tagged `standin-only`.
Source for every rule here: [TL Decision 2026-11-02, CR-05] (rulings md5 5bcaedc9, Addendum 2, §A), unless another tag is shown.
Labels as in `README.md`. "SSE proposal" = SSE's proposed value, for the TL to freeze with v1.

## 1. Listener

| # | Rule | Source |
|---|---|---|
| 1.1 | Binds **127.0.0.1 only**; port from config. Tests read the base URL from `LOGIN_STANDIN_CONTROL_URL` | [TL Decision 2026-11-02, CR-05] |
| 1.2 | Runs only when the environment is `Development` **and** `STANDIN_ALLOW=1`; otherwise the stand-in does not start (PLAN check 11) | [TL Decision 2026-11-02, CR-05]; [SRC: PLAN check 11]; contract §6.8 |
| 1.3 | The **front** listener answers **404** to any `/_standin/*` path | [TL Decision 2026-11-02, CR-05] |
| 1.4 | JSON request and response bodies; every response `Cache-Control: no-store` | [TL Decision 2026-11-02, CR-05] |
| 1.5 | A caller whose remote address is not loopback → **403** | [TL Decision 2026-11-02, CR-05] |

## 2. Routes

| # | Route | Behaviour | Source |
|---|---|---|---|
| 2.1 | `POST /_standin/reset` | Puts everything back to fixtures: users, sessions, nonces, `hub_launch`, lockouts, audit, faults and clock | [TL Decision 2026-11-02, CR-05] |
| 2.2 | `GET /_standin/clock`; `POST /_standin/clock/advance` `{"seconds":n}` | `n` ≥ 0 (time never goes back; `n` < 0 → 400). **Every** server time check uses this clock: code, nonce, `hub_launch`, idle, cap, lockout, token `exp`. Response shape of `GET` (SSE proposal): `{"now":"<RFC 3339 UTC>"}` | rule [TL Decision 2026-11-02, CR-05]; response shape [SSE proposal → TL at freeze] |
| 2.3 | `PUT /_standin/pcs/{pc_id}` `{"mode":…, "location_slug":…}` | `pc_id` = the fake `device_id` / `kid`, must start `standin-`. Modes: `valid`, `missing`, `bad_signature`, `expired`, `replayed`, `unknown_device`, `remote_session` (contract §6.8) plus `revoked_device` and `other_location` ([TL Decision 2026-11-02, CR-22]). Returns `{"cookie":{"name":"standin_pc","value":"<pc_id>"}}`; the test driver installs that cookie for the front origin. The control listener does **not** rely on `Set-Cookie` (browsers don't separate cookies by port). The front listener only **reads** `standin_pc`, never sets it; the real service never reads it | [TL Decision 2026-11-02, CR-05, refines OQ-33]; modes [TL Decision 2026-11-02, CR-22] |
| 2.4 | Fake agent | A loopback listener inside the stand-in process; the page calls it exactly like the real agent (contract §6.5). It finds the PC from the nonce's sign-in or hand-off transaction: the front records `standin_pc` on the transaction when it mints the nonce. No cross-origin cookie is needed | [TL Decision 2026-11-02, CR-05] |
| 2.5 | `PUT /_standin/users/{username}` `{password, roles:[{app_code, role}], location_slugs, status}` → `{user_id}`; `GET /_standin/users/{username}/totp` → `{"code":"######"}` at the test clock | A password set here runs **the same policy validator** as the real path; a violation → **422** `{"violations":[rule ids]}` (ids in §3). S1 has no user-facing set-password route (OQ-SSE2-29) | [TL Decision 2026-11-02, CR-05, OQ-SSE2-29] |
| 2.6 | `PUT /_standin/faults` `{"outbox":"ok\|fail","device_registry":"ok\|unavailable"}` | Fault injection (PLAN check 9, CR-24) | [TL Decision 2026-11-02, CR-05] |
| 2.7 | `GET /_standin/audit?since=<seq>` → `{"events":[…],"next":<seq>}` | In emit order; each event validates against `audit-events.schema.json` | [TL Decision 2026-11-02, CR-05] |
| 2.8 | (none) | **No** `hub_launch` or nonce mint/expire route. Tests get `hub_launch` from the hub tile and expire it by advancing the clock | [TL Decision 2026-11-02, CR-05] |

## 3. Password policy rule ids (returned in `violations`)

The ids are an **SSE proposal** (the TL ruled that SSE lists them, CR-05 / OQ-SSE2-29); the rules themselves are LD §7 (L327-L343) and Addendum 2 OQ-SSE2-31/32.

| id | Rule | Preset | Source |
|---|---|---|---|
| `min_length` | ≥12 Unicode code points (`Rune`) after NFKC | both | [SRC: LD L327]; counting [TL Decision 2026-11-02, OQ-SSE2-31] |
| `max_length` | ≤128 code points after NFKC; never truncated | both | [TL Decision 2026-11-02, OQ-SSE2-31] |
| `letters_and_numbers` | at least one letter and one number | PCI | [SRC: LD L327, L340]; non-ASCII classes **OPEN (SA → compliance owner) → OQ-SSE2-30**, ASCII cases only until answered |
| `two_of_four_types` | at least 2 of 4 character types | Nevada MICS #7 | [SRC: LD L341] |
| `contains_username` | the password contains the username: username normalised as `username_hmac` (trim, NFKC, lowercase invariant) vs password after NFKC + lowercase invariant, ordinal substring; no minimum length | both | [TL Decision 2026-11-02, OQ-SSE2-32] |
| `reused` | one of the last 4 passwords | both | [SRC: LD L328, L340] |

## 4. Fixtures after reset

| Fixture | Content | Source |
|---|---|---|
| Users | `cashier_db`, `no_role_user` (PLAN check 1) | [TL Decision 2026-11-02, CR-05]; [SRC: PLAN check 1] |
| Partners | `pci` (PCI baseline preset) and `nevada` (Nevada MICS #7 preset), one location each; check 10 uses `pci` | [TL Decision 2026-11-02, CR-13, OQ-SSE2-13] |
| Lockout duration | config value, fixture 30 min | [TL Decision 2026-11-02, CR-13] |
| Location slugs, fixture user passwords, partner/location ids | **not ruled**: SSE2 reads them from the stand-in fixture file; not frozen in v1 | [OPEN → TL at freeze] |
