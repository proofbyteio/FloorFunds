# Contract review v1: adversarial pass (SSE2)

- **Reviewer:** SSE2 (agent). Written Mon Nov 2, 2026, 12:10 PM-12:40 PM PT.
- **Range:** `feat/contract-v1` **`c0f7016..6e8a106`**, reviewed read only (15 commits; `ef57eed..6e8a106` = 9 files, +270/−127). Nothing on the branch was modified. Working-tree `contract/*` md5s equal the `6e8a106` blobs.
- **Contract files at `6e8a106` (md5, first 8 characters):**

  | File | md5 |
  |---|---|
  | `contract-v1.md` | `bd0d2085` |
  | `README.md` | `df880cb7` |
  | `OPEN-QUESTIONS.md` | `8696694b` |
  | `errors.md` | `97249efc` |
  | `openid-configuration.schema.json` | `c7e6bd7a` |
  | `claims.schema.json` | `f5d100ac` |
  | `audit-events.schema.json` | `8e16c410` |
  | `device-attestation.schema.json` | `22cfd478` |
  | `SSE-BUILD-LOG-PENDING.md` | `c21a965e` |

- **Sources (md5 verified by SSE2 at 12:10 PM PT):**

  | Source | md5 | Snapshot |
  |---|---|---|
  | LD `login-service-proposal.md` rev 11:41 | `88878681a39e23b82e30d6ad0c505647` (file time 11:41:20; equal to SSE's `/tmp/sse-d0/login-doc-88878681.md`) | `/tmp/sse2-snap/88878681a39e23b82e30d6ad0c505647.md` |
  | PLAN `plans/login-hub-build-plan.md` | `ae18d66482606f9fdf334f0b953065b4` | `/tmp/sse2-snap/plan-ae18d664.md` |

  The AUD and ALW md5s were taken from the contract README and not re-hashed by SSE2.
- **Executable form:** each finding links to a test in `tests/`. "PENDING" means an xUnit Skip whose reason names the OQ.

## Verdict: **NOT READY TO FREEZE**

**Blockers (3):**

| ID | Blocker |
|---|---|
| CR-05 | No contract for the screens and the control API. |
| CR-25 | The plain sign-in remote-session check silently lapses after 60 s. |
| CR-26 | Session cap range is OPEN (OQ-40, Mohanraj). |

If the TL accepts a "freeze with the range as a single config value and only the >15 min check" interim, CR-26 drops to major.

**Majors (11):**

| ID | Major |
|---|---|
| CR-01 | `format` is not asserted. |
| CR-02 | `amr` enum conflicts with Entra pass-through. |
| CR-03 | Refresh ID token `nonce`. |
| CR-04 | `unsupported_*` error codes are missing. |
| CR-12 | No enforcement contract for 4b cage actions. |
| CR-13 | Nevada lockout preset is missing. |
| CR-14 | Discovery `token_endpoint` 404s on the front. |
| CR-15 | Exact discovery key set vs real OpenIddict. |
| CR-20 | Attestation has no purpose / audience binding. |
| CR-22 | Device revocation and location binding are unstated. |
| CR-23 | Attestation freshness relies on `iat`/`exp` the agent chose. |

Minors and nits are listed below. Everything else is consistent with LD 88878681 and PLAN ae18d664, as far as SSE2 can check.

## 1. Attestation path (contract §5.2, §5.5, §5.8, §6.4-6.8, `device-attestation.schema.json`; LD §16.18.5 L1381-1386)

| Property | What the contract says | Verdict |
|---|---|---|
| **alg allowlist** | Header `alg` `const: "ES256"`; required | **OK.** `none`, `HS256` (key confusion), `RS256` and lower-case `es256` are rejected by the schema (AT02, runs). The server must also pin ES256 in its verifier and never pick a verifier from the header. That is not stated → CR-21 (minor). |
| **Embedded keys** | `not anyOf required jwk/jku/x5u/x5c` | **OK** (AT03, runs) |
| **kid handling** | `kid` = enrolled device id; lookup `kid → key` in the registry; `device_id` = `kid` | **OK if the lookup is exact match only.** The contract should say exact, case-sensitive and no fetch. Unknown `kid` = `attestation_invalid` is implied by "any failure"; SEC11 `kid_unknown`. OQ-35 proposes "device id a UUID", but the schema allows any 1-128 character string and §6.8 uses `standin-` ids. The proposal and the schema disagree → CR-27 (minor). |
| **Nonce binding** | Login-service created, server-stored, single use, ≤60 s, bound to the transaction (6.4); must match an unconsumed nonce "of this transaction" (6.7) | **Mostly OK.** Gaps: (a) **purpose**. A nonce minted for a plain sign-in transaction and a hand-off transaction are the same kind of thing, and there is no `aud`/purpose claim (OQ-35 proposes none). See CR-20. (b) **Entropy** is not stated. The schema only says `minLength 16`, so require ≥128-bit CSPRNG (CR-28, minor). (c) **Consume on failure.** 6.7 says "(then consumed)" after a match, but it does not say the nonce is burnt when a later check (signature, exp) fails. Without that, one nonce allows several attempts → CR-23b. |
| **Replay** | Single use through nonce consumption | **OK if consumption is atomic** (compare-and-delete in one statement). The contract should say so. SEC10 `replayed` and SEC11 `nonce_already_consumed` are pending OQ-33. |
| **Expiry** | `exp` not passed; `exp - iat` ≤60 s; skew 5 s (OQ-35 proposal) | **Gap, CR-23 (major).** `iat` is never checked against server time, so an agent with a fast clock can mint `iat = now+10 min`, `exp = iat+60`. The real bound is the server-side nonce TTL (6.4 ≤60 s), but 6.7 does not list "nonce not older than 60 s" as a check. Require: nonce age ≤60 s at check time, `iat` ≤ now+skew, `exp` > now−skew, `exp − iat` ≤60. SEC11 `iat_in_future_beyond_skew`, `nonce_older_than_60s`. |
| **Device id** | `device_id` claim = `kid` | **OK.** Revocation, disablement and location binding of an enrolled device are unstated → CR-22 (major). |
| **typ / crit / unknown header params** | `typ` const but not required; header has no `additionalProperties:false`; `crit` not mentioned | **CR-21 (minor).** Make `typ` required and allowlist the header to {alg, kid, typ}, refusing `crit`. AT09 is pending. |
| **Fail closed** | 6.7 "any failure = attestation_invalid; fails closed for hand-off and cage actions" | **OK for validation failures.** The **error path** (registry or DB unavailable, verifier exception, malformed compact JWS) is not named. It must be `attestation_invalid` and must never be a 500 that a client retries into a different path → CR-24 (minor). SEC11 `registry_unavailable`. |
| **Precedence** | Invalid signature with `remote_session=true` | **Unstated → CR-29 (minor).** A claim inside an unverified JWS must not be trusted, so the result must be `attestation_invalid`, not `remote_session`. Mockup 07 (attest=invalid + remote=yes → agentdown) agrees: E2E12, runs and passes. SEC17 is pending. |
| **Plain sign-in, invalid (6.2, OQ-11)** | Go ahead with no event | **Needs TL.** It is consistent with "best effort" (LD L1364-1367). But an invalid attestation (bad signature or replay) is a tampering signal. SSE2 recommends an audit event (e.g. `auth.login.attestation_invalid`, or a `attestation=invalid` field on `auth.login.succeeded`). Combined with CR-25, today the remote-session block on plain sign-in can be lost without any trace. |
| **Transport (6.6)** | Form field, **or an authorize parameter** | **CR-30 (minor).** With GET, the JWS ends up in URLs, logs and Referer. Single use limits the damage, but require POST only (OQ-34 already proposes POST for the hand-off). |
| **Stand-in fake agent (6.8, OQ-33)** | PC id from a dev-only cookie; `standin-` ids and keys | **OK for S1**, provided check 11 also covers the fake agent: the real build must contain no fake-agent route and must refuse any `kid` starting with `standin-`. Added to TOOL/SEC pending (OQ-SSE2-17). |

**CR-25 (BLOCKER): the plain sign-in remote-session check lapses after 60 s.**

1. 6.1 and 6.4 create the nonce when `/l/{slug}` is served, and it is valid for ≤60 s.
2. If the page fetches the attestation at page load, a cashier who takes more than 60 s to type a username and password submits an expired attestation.
3. By 6.2 (OQ-11) an invalid attestation means "go ahead, no event".
4. So a remote session (attestation `remote_session=true`) gets through plain sign-in whenever the user is slow, or whenever an attacker simply waits. Nothing is logged.

Either the page gets nonce + attestation **at submit** (state it in 6.5), or an expired attestation that said `remote_session=true` must still refuse (it cannot, because it can't be trusted after expiry). The contract has to pick one. Plan check 4 ("remote session → refused") is affected; tests SEC15 and SEC16 are pending.

**CR-20 (major): no purpose binding.** OQ-35 proposes "no `jti` / `aud`; binding comes from the nonce". That holds only if every verifier of agent JWSs shares one nonce store and every nonce records its purpose. 4b cage actions are also agent-signed (LD 11:27 rev item 12: "server token ≤60 s, signed both directions"). If Cash Advance verifies agent JWSs with the same key and the same claim shape, a JWS minted for a cage-action challenge could be presented to the login service, or the other way round, whenever the nonce formats overlap. Add `aud` (login-service issuer) and a `purpose` claim (`signin` | `handoff`), or state that nonces are namespaced per verifier and per purpose. Needs TL / SA / Device Agent. Test AT10 is pending.

**CR-22 (major): device registry semantics.** A `kid` of a device that has been unenrolled or revoked, or that is enrolled at a **different location / partner** than the `/l/{slug}` transaction, is not stated as `attestation_invalid`. A stolen or re-imaged cage PC from location A could hand off at location B. Tests: SEC11 `kid_of_revoked_device`, `kid_of_device_at_other_location` (OQ-SSE2-39).

## 2. hub_launch (§5.7, OQ-7 TL Decision, OQ-36, OQ-41)

- **CR-09 (earlier, "the hub proof is a `state` anyone can forge") is RESOLVED.** `hub_launch` is opaque, server-stored, single use, ≤60 s, and bound to the login session id and the target `client_id`. Missing or invalid → `notfromhub`.
- **Consume on first check even if another rule fails (OQ-36):** OK to freeze. It closes "probe rule 1-3 failures with one value".
- **CR-31 (minor):** the value travels in the client start URL `?hub_launch=` and then to `/connect/authorize`. It can leak through Referer, proxy logs or browser history. Single use and session binding make a leaked value useless to anyone else. Still, require `Referrer-Policy: no-referrer` on the hub and on the login pages that carry it, and never log it in full (log a hash).
- **CR-32 (minor):** the entropy of "128-bit" is stated only in the OQ-36 proposal, not in §5.7.
- **OQ-41 needs TL/SA:** the user sees a re-prompt or a refusal on `notfromhub`. The LD contradicts itself here (L1170/L1391 "refuse" vs §16.16.1 sign-in page). The SSE Proposal (sign-in page + event) matches 5.3. SSE2 agrees but cannot decide it.

## 3. Discovery / JWKS / errors

- **CR-14 → reworked (major), §2.3 / OQ-3.** Discovery on the front advertises `token_endpoint` and `revocation_endpoint` under the public issuer, but those paths 404 on the front (token/revoke live only on 8084, §1.4).
  - Standard discovery-driven clients (ASP.NET Core OIDC handler, OpenIddict client, Microsoft.IdentityModel) call `token_endpoint` from discovery, so every client needs an override.
  - Options: (a) the 8084 discovery copy (OQ-31) advertises 8084 URLs and server-side clients use that copy as their Authority metadata, while the front copy omits back-channel endpoints; or (b) keep public URLs and route them to 8084 at the proxy.
  - **Needs TL.**
- **CR-15 (major), OQ-4 "assert the exact key set".** Real OpenIddict publishes extra members by default: `claims_supported`, `scopes_supported`, `grant_types_supported` with more values, `authorization_response_iss_parameter_supported`, `require_pushed_authorization_requests`, `device_authorization_endpoint` when enabled, `end_session_endpoint`, and others. An exact-set test would fail the real target at S2 for reasons that do not affect any client. Proposal: assert **required present + forbidden absent** (`userinfo_endpoint`, `frontchannel_logout_supported`, `implicit`/`hybrid` response types, `none` alg). **Needs TL.** CT01 is pending OQ-4.
- **CR-04 (major, persists):** `errors.md` still has no `unsupported_response_type` (RFC 6749 §4.1.2.1) or `unsupported_grant_type` (§5.2). It folds `response_type ≠ code` into `invalid_request`, which contradicts the RFC that OpenIddict follows. Either list them, or state the deviation so the stand-in and the real service agree.
- **OQ-17 (no `error_description`/`error_uri`):** OK to freeze, but note that OpenIddict emits `error_description` by default, so the real service needs a handler to strip it (S2 work, not a contract problem).
- **OQ-30 cache 3600:** OK. **OQ-32 allowlist on discovery/JWKS:** OK *if* CR-14 option (a) is chosen; otherwise external resource servers cannot fetch JWKS.

## 4. Tokens, claims, sessions

- **CR-01 (major, persists):** the schemas rely on `"format"` (`uuid`, `uri`, `date-time`), which JSON Schema 2020-12 treats as **annotation only**. A conformant validator with default options accepts `"sub":"not-a-uuid"`. The contract must say "validators MUST assert format" (SSE2's tests do; CS08 proves a bad uuid fails only with `RequireFormatValidation`), or replace `format` with `pattern`.
- **CR-02 (major, persists):** `amr` items are an RFC 8176 enum, while the OQ-22 proposal says "Entra values passed through". Entra emits values such as `rsa`, `ngcmfa`, `wia`, `fido`, some of which are outside 8176 (e.g. `ngcmfa`). Either the enum must be opened for `login_provider=entra`, or the login service must map Entra values onto 8176. CS10 is pending SSE OQ-22.
- **CR-03 (major, persists):** `id_token` requires `nonce`, but a refresh-issued ID token SHOULD NOT carry one (OIDC Core §12.2), and OpenIddict omits it. Make `nonce` conditional (code grant only). CS09 is pending OQ-SSE2-35.
- **CR-06 (minor, persists):** `x_standin` is optional in both token defs. Check 11 needs "stand-in tokens always carry it; real ones never do", so state it as required in the stand-in and forbidden in the real service.
- **CR-07 (nit, persists):** "`exp - iat == 300`" lives only in a `$comment` (claims.schema.json L60). It is fine as a test rule, but put it in §8.1 text.
- **CR-08 (minor), OQ-15 `location_id` always present:** OK for location users. For partner-level users or `/admin`, LD L227 makes the Cachd client id NULL for partner-level. Confirm that partner-level users also always pick a location. **Needs SA.**
- **Two skews (nit):** 30 s for tokens (OQ-15) and 5 s for attestation (OQ-35). Both are fine; just make sure they are not merged into one setting.
- **cachd_client_id:** OK. OQ-37 (AUD §7A.4 still says `client_id`) needs the SA; until then two envelope spellings exist across services.

**Session cap (§7.4, OQ-13 resolved, OQ-40 OPEN):**

- The 12 h default and the setting name `LoginService:Session:AbsoluteLifetime` are fine.
- "The cap must be greater than the 15-minute idle timeout or the service does not start" means a configured **15 min or less is rejected**, which matches the TL instruction of 12:08. Tests SEC61 (theory: `00:15:00`, `00:14:59`, `00:01:00`, `00:00:00`, `-01:00:00`) skip until `src/Login.StandIn` exists.
- **CR-26 (blocker until decided):** who may change the cap and its range are OPEN (Mohanraj, asked 12:01 PM PT). No test asserts any other bound (SEC62 is pending OQ-40).
- **CR-33 (minor):** the contract does not say whether a hub hand-off (same login session, original `auth_time`, 5.6) gives the *second* client's refresh family the remaining cap or a fresh 12 h. "Login session and its refresh-token family end at sign-in + cap" implies the remaining cap. State it (SEC64, OQ-SSE2-37).
- **Nit:** say whether a changed cap applies to existing sessions. SSE's withdrawn proposal said "new sessions only", and that point is now unstated.

## 5. Lockout, audit, 4b

- **CR-13 (major, persists):** §6.3 still has only the PCI preset (10 tries / 30 min). LD 88878681 L338 (Nevada MICS #7 preset, rev 11:30) says **lockout after 3 failed tries, for ≥30 min or until an admin unlocks, location users only; `/admin` never by hand**. The contract needs the preset switch and both values, or a statement that S1 is PCI only.
- **CR-11 (minor), OQ-24 reasons:** the proposal list now has `remote_session` but no `rate_limited`, and nothing for a throttled username. If S1 throttles, add it. `attestation_invalid` is rightly absent on plain sign-in (6.2).
- **CR-12 (major, persists):** 5.5 says "Cash Advance opens but cage actions stay blocked until the agent attests again" (check 4b). The contract does not say **which component enforces it** (Cash Advance BFF via a token claim? the device agent locally, as `local_agent.action_blocked`? a per-action server challenge?). It also does not say whether the scope is the PC or the session. No login-service claim carries "attested=false". If enforcement is outside the login service, say so explicitly, so 4b is tested against the stand-in Cash Advance mock and not the login contract. SEC50-54 are pending OQ-SSE2-34.
- **§8 "defined, not emitted in S1" events:** OK. Contract tests assert their schemas only, and (pending) that the S1 stand-in never emits them.
- **OQ-38:** allowing exactly one `iam.*` name is OK as an interim. **Needs SA.**

## 6. UI / testability

- **CR-05 (BLOCKER, persists):** there is still no contract for the **screens** (`/l/{slug}` form field names, the error text "Sign-in didn't work.", screen 8 variants A/B and their element ids). There is also no **control API** for the stand-in: clock advance, attestation mode per PC (OQ-33 says cookie, but not its name or values), user and role seeding, audit read-back.
  - About 60 of SSE2's 70 pending tests wait on this (`Pending.ControlApi`, OQ-SSE2-04).
  - The mockups are the only UI source, and they changed during SSE2's run (E2E02/06/12/15 had to be rewritten at 11:42).
  - The freeze needs at least: control-API endpoints and their shapes, and the stable element ids or names the e2e tests may rely on.

## 7. Earlier findings: status at 6e8a106

| ID | Finding (earlier pass, ≤ef57eed) | Status |
|---|---|---|
| CR-01 | `format` not asserted | **Persists** (major) |
| CR-02 | `amr` Entra vs RFC 8176 enum | **Persists** (major); OQ-22 proposal contradicts the schema |
| CR-03 | ID-token `nonce` on refresh | **Persists** (major) |
| CR-04 | No `unsupported_response_type` / `unsupported_grant_type` | **Persists** (major) |
| CR-05 | No UI contract / control API | **Persists** (blocker) |
| CR-06 | `x_standin` optional | **Persists** (minor) |
| CR-07 | `exp-iat=300` only in `$comment` | **Persists** (nit) |
| CR-08 | `location_id` for partner-level users | **Proposal** (OQ-15: always present), needs SA |
| CR-09 | Hub proof forgeable | **Resolved** by `hub_launch` (§5.7); residual leakage CR-31 (minor) |
| CR-10 | Attestation format and binding unstated | **Mostly resolved** (§6.4-6.7, schema); residual CR-20/22/23/21/24 |
| CR-11 | `auth.login.failed` reason list | **Proposal** (OQ-24); `rate_limited` missing (minor) |
| CR-12 | 4b enforcement point | **Persists** (major) |
| CR-13 | Nevada lockout preset | **Persists** (major) |
| CR-14 | Back-channel URLs in discovery | **Changed**: public URLs that 404 on the front (major, needs TL) |

## 8. SSE Proposals: OK to freeze, or needs a decision

| OQ | Topic | SSE2 judgement |
|---|---|---|
| 3 | Public URLs for back-channel endpoints | **Needs TL** (CR-14) |
| 4 | Exact discovery key set | **Needs TL**; SSE2 proposes required + forbidden (CR-15) |
| 5 | Bad `redirect_uri`/`client_id` → error page | OK to freeze (RFC 6749 §4.1.2.1) |
| 6 | Scope, aud and client names; `offline_access` | OK to freeze for S1 (names are POC); **SA** confirms the names for S2 |
| 10 | No event on a 1-3/5/6 re-prompt | OK to freeze |
| 11 | Plain sign-in, invalid attestation → proceed, no event | **Needs TL** (audit it; CR-25) |
| 12 | `invalid_grant`, family revoke | OK to freeze |
| 14 | `at+jwt`; `at_hash` allowed | OK to freeze (OpenIddict default) |
| 15 | Types, single-string `aud`, 5 min ID token, 30 s skew, `location_id` always | OK except `location_id` → **SA** (CR-08) |
| 16 | RP-Initiated Logout params | OK to freeze |
| 17 | No `error_description`/`error_uri`; `iss` on errors | OK to freeze (S2 must strip OpenIddict's defaults) |
| 18 | `login_required` vs `interaction_required` | OK to freeze |
| 19 | 503 `temporarily_unavailable` + Retry-After | OK to freeze |
| 20 | 8084 block = same minimal 403 | OK to freeze |
| 21 | `private_key_jwt` RS256 | OK to freeze |
| 22 | `amr` Entra pass-through | **Needs TL/SA** (CR-02) |
| 23 (rest) | Flat `partner_id`/`location_id` | **Needs SA** |
| 24 | Reason lists | OK, with the `rate_limited` question to the TL (CR-11) |
| 25 | Envelope only for undefined event fields; `x_standin=true` | OK to freeze |
| 30 | `max-age=3600` | OK to freeze |
| 31 | Discovery copy on 8084 | OK to freeze; ties to CR-14 |
| 32 | Allowlist on discovery/JWKS | OK only with CR-14 option (a); **TL** |
| 33 | Fake agent via a dev-only cookie, `standin-` | OK for S1; cookie name and values must be in the contract (CR-05) |
| 34 | Login-host page calls the agent; POST re-submit | OK in substance; **TL/SA/Device Agent** because it changes the agent call site |
| 35 | `typ`, no `jti`/`aud`, ≤60, skew 5 s, UUID, no embedded keys | **Needs TL/SA/Device Agent**: `aud`/purpose (CR-20), `typ` required (CR-21), UUID vs `standin-` (CR-27), `iat` freshness (CR-23) |
| 36 | `?hub_launch=` 128-bit; consume on first check | OK to freeze (+ CR-31/32 wording) |
| 37 | AUD rename to `cachd_client_id` | **Needs SA** |
| 38 | One `iam.*` exception | **Needs SA** |
| 39 | GitHub repo details | **Needs TL/Mohanraj**; not a contract item; no remote until the TL says |
| 40 | Cap: who and range | **OPEN, Mohanraj** (CR-26) |
| 41 | `notfromhub` user sees the sign-in page | **Needs TL/SA** (LD self-contradiction) |

## 9. Minor / nit index

| ID | Rank | Finding |
|---|---|---|
| CR-21 | minor | `typ` not required; `crit` and unknown header params not refused; verifier must pin ES256 |
| CR-24 | minor | Error path (registry down, exception) must equal `attestation_invalid` |
| CR-27 | minor | OQ-35 "UUID" vs schema string and `standin-` ids |
| CR-28 | minor | Nonce entropy unstated (≥128-bit) |
| CR-29 | minor | Precedence: invalid signature + `remote_session=true` → `attestation_invalid` |
| CR-30 | minor | `device_attestation` as a GET authorize parameter; require POST |
| CR-31 | minor | `hub_launch` in URLs: Referrer-Policy, log a hash |
| CR-32 | minor | 128-bit `hub_launch` only in the OQ text |
| CR-33 | minor | Hand-off and remaining cap |
| CR-23b | major (part of CR-23) | Nonce not burnt on a failed check |
| CR-07, skews, cap-change timing | nit | |

## Addendum: verification of TL Addendum 2 at feat/contract-v1 38ad62e (SSE2, Mon Nov 2, 8:26 PM PT)

Authority: `reviews/contract-v1-tl-rulings.md` "## Addendum 2", md5 5bcaedc9fc1c748743243a7dfd1a3a38 (verified).
Read-only: `git log feat/contract-v1`. First checked at 003f519 (8:11 PM PT); SSE then committed 32c4cf7 → 38ad62e (8:16–8:24 PM PT), so the table below is at **38ad62e** (8:24 PM PT).
Evidence = the gate test that runs now against the contract text/schemas (all PASS in the 8:24:36 PM PT run, tree byte-identical to 38ad62e), plus reading the row.

| CR | Ruling (short) | At 003f519 | At 38ad62e | Evidence |
|---|---|---|---|---|
| CR-05 | standin-control.md + ui-driver.md | not yet | **verified** | CS19 ×2; files read; CTL/UID tests aligned (pending: no stand-in) |
| CR-25 | fresh nonce at submit, invalid never valid, proceeds `attestation=invalid` | partial | **verified** | CS15; §6.1, §6.2, ui-driver 1.3 |
| CR-26 | cap > idle only; SEC62 pending OQ-40 | verified | verified | §7.4 |
| CR-01 | uuid pattern + format-assertion rule | not yet | **verified** | CS11, CS11b (README; schema text cites CR-21 for the rule: cite nit) |
| CR-02 | RFC 8176 only; mfa/ngcmfa → mfa; wiaormfa never; recovery → [pwd] | not yet | **verified** (dd497e1 briefly put `ngcmfa` in the enum; fixed in ce23ded) | CS10; amr description |
| CR-03 | id_token_code / id_token_refresh | not yet | **verified** | CS09; §8.6 |
| CR-04 | unsupported_response_type / unsupported_grant_type | not yet | **verified** | CS12 |
| CR-12 | §6.9 per-action at Cash Advance; no attested claim | not yet | **verified (contract)**; BFF stub endpoint specified, **not built** (SI1 held) | CS14, CS21 ×3 |
| CR-13 | per-partner preset, pci 10/30, nevada 3/30 | not yet | **verified** | CS13; §6.3; standin-control §4 |
| CR-14 | clients override token/revocation endpoints | verified | verified | CS20 |
| CR-15 | = OQ-4 | needed check | **verified** | discovery schema `not` userinfo/frontchannel; §2.7 |
| CR-20 | aud + purpose; cross-purpose replay | verified | verified | AT10, AT11; §6.5 |
| CR-21 | header closed | partial | **verified** | AT09 (cty, x-extra, b64, zip refused); §6.7 (2) |
| CR-22 | unenrolled/revoked/disabled/location → invalid | not yet | **verified**; real-device location source still OPEN | CS16; §6.7 (3)(4) |
| CR-23 | ±30 s, exp > iat, exp−iat ≤ 60, nonce ≤ 60 s no skew | partial | **verified** | CS17; §6.7 (6) |
| CR-23b | compare-and-delete at start; every outcome burns | partial | **verified** | CS18; §6.7 (1) |
| OQ-SSE2-40 | reissue after password and after MFA | not in contract | **verified** | §4.11 |

Regressed: **none** at 38ad62e (dd497e1's `ngcmfa` slip was fixed 1 minute later).

**Freeze-readiness verdict: READY TO FREEZE from SSE2's side for the Addendum 2 Changes**, with these caveats the TL should accept explicitly at freeze:
(1) SSE proposals still to be frozen by the TL: data-testids, `GET /_standin/clock` shape, violation ids, `totp_code` field name; fixture slugs/passwords/ids are unruled.
(2) Still OPEN and not blockers for the contract text: OQ-SSE2-30 (PCI Unicode classes; LS-OQ-52), OQ-40 (cap range, Mohanraj), CR-22 location source, LS-OQ-52 Nevada production preset, OQ-39.
(3) Not ruled in Addendum 2: CR-06–11, CR-24, CR-27–33; OQ-SSE2-11, 12, 15, 17, 19/20, 22.
(4) Contract cites LD 560cb6ab (SA rev 8:25 PM PT); the rulings cite LD b7385794; the plan on disk is now 444ddba4, not the brief's 10fa9853 (changed 8:25 PM PT). Recorded, not resolved by SSE2.
(5) An uncommitted `contract/standin-ui.schema.json` appeared in the working tree after 38ad62e; not reviewed.
"Ready" covers the contract text only. Nothing is green: no stand-in exists in code/src/, so 89 tests stay pending.

### Addendum, continued: re-check at 99f2b7d (8:33 PM PT)

SSE kept committing while this was being written (cb4e637 → 78a792c, 8:27–8:32 PM PT): TL Decisions 8:26/8:29/8:31 PM and [Confirmed DEC-084], [Confirmed DEC-085]. Every contract gate test (CS*, AT*, contract-gate, check 12) passes against an exact `git archive` of 99f2b7d. Nothing in the table above regressed.
Changes to the caveats above:
- (1) is **resolved**: TL Decision 8:26 PM accepted the SSE proposals (testids, `totp_code`, clock shape, rule ids, stub paths; `standin-ui.schema.json`). Fixture slugs/passwords: not re-checked.
- CR-12: §6.9 stub is now "built as part of SI1" (TL 8:26 PM); still not built. Production design still OPEN.
- (4) plan md5: the contract now cites PLAN 444ddba4 (TL 8:26 PM), matching the file on disk. The brief's 10fa9853 is superseded. LD cites: the contract's 560cb6ab govern (TL 8:26 PM).
- (5) `standin-ui.schema.json` is committed (cb4e637); gate tests for it are not written yet.
- New since 38ad62e and not yet reviewed in depth: admin `admin_type` super/partner tokens, §8.11 location mapping, DEC-084 (tests CT28/SEC56 added, pending), DEC-085 `recovery_reenroll` (no tests yet).
Verdict unchanged: **ready to freeze from SSE2's side** for Addendum 2, while SSE is still adding TL Decisions to v1-d4. Freeze a named commit, not a moving head. Not S1 sign-off: no stand-in.

## Addendum: final pre-freeze verdict at feat/contract-v1 e363090 (SSE2, Mon Nov 2, 8:55 PM PT)

**FREEZE-OK at e363090.** All three fix-before-freeze items from the TL's 8:47 PM rulings are in.

How this was checked:
- An exact export, with SSE's `check_schemas.py` pointed at the export: 166 checks, ALL PASS. SSE claimed 165; a 1-count difference.
- The full suite: export 333/1 (TOOL08, an export artifact), working tree 334/0.

1. **Re-enrol POST rate limit (7.8).** 7.8 now states:
   - Default 5 failed tries per 15 min per user, configurable as `LoginService:Mfa:ReenrollRateLimit`.
   - When it trips: 429, the same generic message, no-store, and `auth.mfa.failed step=reenroll reason=rate_limited session_kind=recovery_reenroll`.
   - The schema confines rate_limited to auth.mfa.failed with step=reenroll.
   - It is labelled a TL Decision ("the SA may revise").
2. **change_denied.**
   - Details are closed. setting, attempted_value and reason are required. The reason enum is exactly not_admin, wrong_partner and out_of_range.
   - attempted_value is restricted to TimeSpan text, so no secret or free text fits.
   - admin_type is required for wrong_partner and out_of_range and forbidden for not_admin. A partner admin requires scope.partner_id.
   - Everything the TL asked for at 8:37 PM is now covered.
3. **OQ-48.** auth.mfa.failed step=reenroll details are closed (method, step, reason), so an echoed code is rejected.

No regressions in anything confirmed at d0f75b3: session_kind, admin_type, location_ids, DEC-084/085/086/087/088/089, codes_issued normal or recovery_reenroll, reenrolled recovery_reenroll only, the /mfa/reenroll spelling, and the second codes GET returning 404.

Post-freeze, not blockers (OQ-SSE2-42/43/44/47/49 in the test matrix):
- Duplicate location_ids are accepted.
- The cap endpoint is unspecified.
- admin_type on location events can't be blocked by the schema.
- cap_changed for a partner admin doesn't carry the partner.
- The generic message is not an LD string.
- The §7.8 paths are still a "Proposal".
- DEC-086 is cited as "Confirmed OQ-40 partial", not by id.
- check_schemas.py hardcodes the working-tree path.
- The cited LD md5 (560cb6ab) differs from the LD on disk (190c62e9).
- TL items 4/6/7/8.

## Addendum: stand-in findings at feat/si1-standin 8aab98a (SSE2, Mon Nov 2, 9:45 PM PT)
Target 8aab98a [TL 9:32 PM PT], base 18090, own state dir. contract-v1 e363090 is frozen and not reopened; these are findings against the stand-in, or questions for v1.1.

| # | Sev | Finding | Evidence |
|---|---|---|---|
| F1 | **major** | Auth-age rule 1 rounds down to whole seconds (A42 `Handoff.Age` = floor), against the TL ruling of 9:32 PM PT (ms precision, no rounding down). A session 300002 ms old gets the interstitial. | HO18: "auth age at authorize >= 300002 ms / result: 200". SSE evidence row 109 "exactly 300 s accepted" relies on this. |
| F2 | **major** | Same floor on the 60 s hub_launch: 60003 ms is accepted. | HO03b: "hub_launch age >= 60003 ms -> 200". SSE row 102. |
| F3 | **major** | TOTP code replays: one code signs in a second browser. Same at /admin/mfa (±30 s window). | SEC76: "replayed TOTP code accepted: screen-hub". SignIn.cs has no last-used step. (OQ-SSE2-50) |
| F4 | **major** | No TOTP attempt limit: after 11 wrong codes the right code still signs in. | SEC77. (OQ-SSE2-50/51) |
| F5 | **major** | The stand-in stdout log (ASP.NET Core "Request starting", Information) holds raw hub_launch, code_challenge and state. Breaks the 5.7 URL exception ("app logs redact hub_launch, code and state"). | HO17: "…&nonce=n-1&hub_launch=zunH_76cMt67ZIWdnR4geQ - …" at pos 323169 of standin.log. |
| F6 | fixed | 9a14163 interstitial had no CSP. 8aab98a has it. Pinned by HO10b (exact string) and HO20 (every page). | 9a14163 headers: only Cache-Control, Pragma. 8aab98a: `default-src 'self'; script-src 'self' 'unsafe-inline'; connect-src 'self' http://localhost:18092; frame-ancestors 'none'`. |
| F7 | minor | `script-src 'unsafe-inline'` on every page vs 5.8(c) "strict CSP". X-Frame-Options SAMEORIGIN on sign-in and admin pages contradicts frame-ancestors 'none' (CSP wins in current browsers). | HO20 logs. (OQ-SSE2-58) |
| F8 | minor | Username timing difference: known user 2.3 ms vs unknown user 0.5 ms median (n=40, p=8.9e-14). | SEC79c. (OQ-SSE2-11) |
| F9 | minor | One refresh token replayed inside the 30 s leeway gives 3 live token chains. | SEC75b. (OQ-SSE2-52) |
| F10 | info | hub_launch shown to back office is ignored and stays usable (A41). The TL item says "refused". | HO06 log. (OQ-SSE2-56) |
| F11 | info | SSE's fixture-secrets script checks only the current index. A key deleted in history, and an OKP JWK with `d`, are not detected. Client secrets and recovery codes are plain text in the committed fixtures.json (labelled FIXTURE) and not checked. | FS03 logs: rc=0 "NOT detected". |
| F12 | info | Discovery advertises front token and revocation endpoints that answer 404. /admin has no lockout (A33). | earlier SEC run. |
| F13 | info | The interstitial has no ui-driver id (A38). | HO21 pending (OQ-SSE2-57). |

SSE claim "143/143, evidence md5 276d88d0": **reproduced** (SSE harness against my 8aab98a: TOTAL 143 / 143; file md5 276d88d001535ac6eac2723dbe0bb5d1). It conflicts with F1/F2 only on rows 102, 109 and 110.

## Addendum: SI1 GO/NO-GO at feat/si1-standin 3feb66c, tag si1-freeze (SSE2, Mon Nov 2, 10:25 PM PT)
contract-v1 is frozen and not reopened. Findings are against the stand-in, or questions for v1.1. Details, commands and evidence: docs/BUILD-LOG.md (same date), TEST-MATRIX-S1 "Status at 3feb66c".

- SSE claim 251/251 (evidence md5 aa782b7b): **reproduced** on my own export and stand-in (base 18090). 189 at 10a1ca4 also reproduced. 188/210/243 not run standalone.
- My suite: 0 fail (468 pass, 84 pending; D93_04/05 enabled and passing). UI01–05 14/14. Fixture secrets PASS on all three branches; SSE's 7 A55 commits are all inside eb0fa5d..c4da996.
- 8aab98a F1–F5 and F6/F7 are fixed: ms-precision boundaries, TOTP single use, TOTP lockout, no secrets in logs, CSP script-src 'self' without 'unsafe-inline', XFO DENY or absent.

| # | Sev | Finding at 3feb66c |
|---|---|---|
| G1 | major | Session cap has no upper bound; ≥ ~2,914,000 days makes every session of that partner (or all, if global) return 500, and the partner admin cannot undo it. OQ-SSE2-60 |
| G2 | major | Cage stub reads form only and answers 501 to a valid request; 6.9 shape is JSON in, 200 {"ok":true} out. Fails closed. OQ-SSE2-61 |
| G3 | major, needs ruling | remote_session=true attestation accepted for cage actions. OQ-SSE2-62 |
| G4 | minor | Cage nonce 43 chars vs schema 22. OQ-SSE2-63 |
| G5 | minor | Pre-MFA sign-in tx has no lifetime. OQ-SSE2-64 |
| G6 | minor | /admin error lacks error-area testid. |
| G7 | minor | 6 of 7 fixture passwords are static literals, not generated. |
| G8 | minor | State dir hard-coded to /tmp/sse-d0/standin-{port}. |
| G9–G10 | info | validation before authorization on session-cap (OQ-SSE2-65); favicon 404; RL window end not observable. |

**Verdict: GO with conditions** for SI1. No blocker; no failing test in the agreed suite. Conditions: TL decides G1 and G2 (fix, or accept as stand-in limits with a v1.1 item), and rules on G3, which becomes a blocker if remote_session must block cage actions.

## Addendum: 9b7efc9 re-check (SSE2, Mon Nov 2, 10:50 PM PT)
All conditions from the 3feb66c review are verified fixed at 9b7efc9: G1 (cap range (15 min, 24 h], no overflow), G2 (6.9 JSON body → 200 {"ok":true}; anything else → 403, A72/A73), G3 (remote_session refused, blocker per TLD-SI1-01, audit reason remote_session per A70), and G4–G9 and FS1. Pending MFA expires at > 300000 ms; exactly 300000 ms is accepted. New items are info only: N1 (00:60:00 → out_of_range, not 400), N2 (no event when a pending MFA expires, OQ-SSE2-66), N3 (old literal fixture passwords remain in history, WARN).

**Verdict: GO** for SI1 at 9b7efc9.
