# S2b: Microsoft Entra ID sign-in for locations, PHASE 1 plan (SSE2)

Status: **PLAN ONLY, awaiting TL approval.** No code, no branch, no packages installed, no DB connection made.
Author: SSE2. Written Wed Nov 4, 2026, 7:21–7:45 AM PT. Requested by Mohanraj at 7:20 AM PT (via TL).
Labels: **[SRC]** read in a file (path given), **[VERIFIED]** checked this turn (nuget.org API), **[ASSUMPTION]** mine and unconfirmed, **[DRAFT-SSE]** SSE's draft interface, not final, **[OPEN]** needs a ruling (§g).

Sources read (read-only):
- `/workspace/cachd-ones/login-service-proposal.md` (SA): §3.2 L156 "Entra client library (TL Rec)", §4.1 (location_login_config) and §7.1 (Entra MFA rule and test matrix rows 1–23). TL's brief cites "§4.1" for the library; the library text is actually at L156 (§3.2), and §4.1 is the config table.
- `code/contract/contract-v1.md` rows 5.1 (hand-off rule 2), 8.10 (amr), 11.1–11.3 (end session).
- `code/contract/claims.schema.json` (closed amr enum) and `code/contract/audit-events.schema.json` (`provider_kind`, `auth.login.failed.reason`).
- SSE `src/SI1-BUILD-LOG.md` L262, L277, L303: the positive ngcmfa rule-2 path is pending until there is an Entra provider.
- `si1-fixes` (3e0b71d) `src/Login.StandIn/Login.StandIn.csproj`: net10.0, `RestorePackagesWithLockFile` + `RestoreLockedMode`, one pin `Microsoft.IdentityModel.JsonWebTokens [8.19.2]`. Box runtime is Microsoft.AspNetCore.App 10.0.14.
- `main` (9ccfcbe) contains only the GitHub README merge; no `src/` yet (see §g Q1).

## (a) Architecture

### Request flow
```
GET /l/{slug}
 └─ ILocationSlugResolver (SSE; GetBySlugAsync [DRAFT-SSE, requested])  → location_id | 404 (unchanged)
 └─ ILocationLoginConfigReader.GetAsync(location_id)                    → LocationLoginConfig?
      null                     → FAIL CLOSED: error page, no form, auth.login.failed reason=config_missing (§g Q5, TL ruling needed)
      ProviderKind=Database    → today's password+TOTP page, byte-for-byte unchanged (regression: full SI1 suite)
      ProviderKind=Entra       → EntraReadiness check (§c, cached)
           ready   → 302 Challenge("entra-oidc") → Entra /authorize (code + PKCE S256, prompt=login, state+nonce)
           down    → AllowDbFallback ? DB form + banner + audit (§c) : error page, no form + audit (§c)
GET /signin-entra (CallbackPath, front host)
 └─ OIDC handler: state/nonce/PKCE, code redemption at the token endpoint (back-channel, 5 s timeout), ID-token validation
 └─ OnTokenValidated (our checks, §a.3) → creates OUR login session (login_provider=entra, location_id) → continue the existing authorize/hub flow
```
- The slug only selects the method (TL item 3). The location is bound into the OIDC `state` (`AuthenticationProperties.Items["location_id"]` and `["row_version"]`, protected by Data Protection). On the callback the location comes **only** from the protected state, never from a query string, cookie or `tid`.
- No password form is ever rendered at an Entra location unless fallback is active (§c).

### Where the per-request authority is set
- One named scheme `entra-oidc` (AddOpenIdConnect), registered once. It is not one scheme per location.
- **OnRedirectToIdentityProvider:** load the config from `Properties.Items["location_id"]`. Set `ProtocolMessage.IssuerAddress` to that tenant's `/oauth2/v2.0/authorize`, and set `ClientId`, `Scope = "openid profile offline_access? api://<our-app>/login.verify"` (§g Q10 on offline_access), `Prompt = "login"` (7.2 item 0) and `max_age=0`? (§g Q11).
- **Configuration:** tenant-specific metadata comes from `{OidcAuthority}/v2.0/.well-known/openid-configuration`, through a per-authority `ConfigurationManager<OpenIdConnectConfiguration>` cache keyed by `(authority, row_version)`. The handler's `ConfigurationManager` is replaced by a small `PerLocationConfigurationManager` that picks the entry using the location in the current request (`IHttpContextAccessor`). **[ASSUMPTION]** This is the cleanest hook in ASP.NET Core 10's handler. The fallback is to set `Options.Configuration` per request inside events, which is thread-unsafe, so it is rejected.
- **OnAuthorizationCodeReceived:** set `TokenEndpointRequest.ClientId` and the client credential from `SecretRef`, resolved through `ISecretResolver` (fake in tests; AWS SM later; §g Q7).
- **IssuerValidator** = `AadIssuerValidator.GetAadIssuerValidator(authority).Validate` (Microsoft.IdentityModel.Validators). It requires the `iss` GUID to equal `tid`.

### OnTokenValidated checks, in order (first failure stops; each failure → `auth.login.failed` with `provider_kind=entra`, `HandleResponse()`, generic error page)
1. **State binding:** `location_id` exists in the protected properties. The config is re-read and its `row_version` must equal the one stored at challenge time; otherwise `config_changed` (§g Q12 on the reason name).
2. **Tenant:** `tid` claim present, a GUID, `tid ∈ EntraAllowedTenantIds`, and `tid ≠ 9188040d-6c67-4c5b-b112-36a304b66dad` (MSA, §7.1 row 13) → `tenant_mismatch` (existing reason).
3. **Guest:** `acct = 1` or `idp ≠ iss` → reject (§7.1 row 18; reason `guest_account`, §g Q12).
4. **Access token for our API** (from `TokenEndpointResponse.AccessToken`, validated with `JsonWebTokenHandler` against the same tenant JWKS; §7.1 rule 1, rows 8–12, 19): RS256, `kid` in JWKS, lifetime ±30 s skew (G1), `ver=2.0`, `aud` = our API client id, `scp ∋ login.verify`, `azp` = our client id, `iss` GUID = `tid`, and its `tid`/`oid` equal the ID token's.
5. **Replay:** `uti` of the access token is stored for its lifetime; a second use → reject (row 20). **[ASSUMPTION]** Stored in memory for the stand-in phase; Postgres later (§g Q13).
6. **auth_time** present and ≤ 5 min old (row 17) → `stale_auth_time` (existing).
7. **External login lookup** (TL item 3): `auth.external_login` on `(provider_kind='entra', issuer, tid, subject)`. `issuer` is the normalized v2 issuer `https://login.microsoftonline.com/{tid}/v2.0`, and `subject` is the **`oid`** (stable across apps in the tenant) **[OPEN §g Q6: `oid` vs pairwise `sub`]**. No row → `unknown_subject` (§g Q12). **Never** matched by email, `preferred_username` or `upn`.
8. **Location assignment:** the linked user is assigned to the state's `location_id` (role at that location), else `location_not_assigned` (§g Q12). This covers the slug swap: a user from location A starting at `/l/B` is refused even with the same tenant.
9. **User state:** disabled or locked users are refused with existing reasons.
10. **MFA (§7.1 rule 2–4):** compute `amr_out` (below). If `MfaRequired` and `mfa ∉ amr_out` → `mfa_missing` (existing). The `acrs`/`c1` step-3 rule is **out of S2b** unless TL says otherwise (§g Q14).
11. Success: our login session is created (`login_provider=entra`, `amr=amr_out`, `auth_time` = Entra `auth_time`, `session_kind=normal`, `location_id`) and `auth.login.succeeded` is written with `provider_kind=entra`, `tenant_id`, `amr` (raw Entra list in `details.amr`? see §g Q15), `acct`, `idp`, `auth_time`. Then the external cookie is discarded (it is only a transient sign-in scheme; we never keep Entra tokens).

### amr mapping (contract 8.10, closed enum)
| Entra `amr` value (access token) | Our token `amr` |
|---|---|
| `mfa` or `ngcmfa` (either, set membership) | adds `mfa` |
| `pwd` | `pwd` |
| `otp` | `otp` |
| `wia` | `wia` |
| `rsa` | **dropped** [ASSUMPTION: RFC 8176 has no `rsa` registered in our enum; `hwk`/`swk` are not inferable; §g Q16] |
| `wiaormfa` | **dropped**, never `mfa` (LS-OQ-20 default no) |
| `fed`, `none`, anything unknown | dropped |
| result empty | reject (`mfa_missing` if MfaRequired; else `amr_missing`, §g Q12), because the schema needs `minItems:1` |
- **Hand-off rule 2 positive path:** the fake issuer gives `amr=["ngcmfa"]` → our `amr=["mfa"]` → the hub hand-off succeeds (contract 5.1 rule 2). This is test ENT-HO-01, and it closes SSE's SI1-BUILD-LOG L277/L303 item.

### Sign-out order (contract 11.1)
1. `/connect/endsession` as today: revoke every authorization and token of **that login session**, delete our session and cookie, and write one `auth.session.revoked reason=sign_out` (11.3). **Ours first, always**, even if step 2 fails.
2. Then, at an Entra location only, 302 to that tenant's `end_session_endpoint` with `id_token_hint`? **No:** we don't keep the Entra ID token (above). Send `post_logout_redirect_uri` = our `/l/{slug}`, `client_id` and `logout_hint`? (§g Q17).
3. Entra redirects back to `/l/{slug}` (11.2(c)). If the Entra end-session is unreachable, the user still ends up signed out of ours; the result is logged (`entra_endsession: redirected|skipped`) **in our app log only** (no new audit field; v1.1 if wanted).
Test ENT-SO-01 asserts the order with timestamps: our session is revoked and the event written **before** the 302 to the fake Entra, and the fake records the end-session hit afterwards.

## (b) Package list (exact pins, nuget.org, verified 7:35 AM PT via `api.nuget.org/v3-flatcontainer`)
Goal: one Microsoft.IdentityModel family version in the whole graph, aligned with the existing pin **8.19.2**.

| Package | Exact pin | Why | Already in solution? | Transitive additions |
|---|---|---|---|---|
| `Microsoft.AspNetCore.Authentication.OpenIdConnect` | **[10.0.14]** (latest stable, published 2026-10-25 17:07 UTC = 10:07 AM PT; matches the box runtime 10.0.14) | `AddOpenIdConnect` handler (code+PKCE, state, nonce, ID-token validation). Not in the shared framework. | No | `Microsoft.IdentityModel.Protocols.OpenIdConnect 8.19.2` → `Protocols`, `Tokens`, `Logging`, `Abstractions`, `System.IdentityModel.Tokens.Jwt 8.19.2` (all 8.19.2; `JsonWebTokens 8.19.2` already pinned) |
| `Microsoft.IdentityModel.Validators` | **[8.19.2]** | `AadIssuerValidator`. Pinned to 8.19.2, **not** latest 8.23.0, so the graph stays on one version (the OIDC 10.0.14 nuspec depends on 8.19.2 exactly). | No | none beyond the row above (same 8.19.2 set) |
| `Microsoft.IdentityModel.JsonWebTokens` | [8.19.2] (unchanged) | Access-token validation (§a.3 step 4) and the fake issuer's signing | **Yes** (stand-in) | — |
| Tests: none new | — | The fake issuer uses Kestrel + `JsonWebTokens` (already pinned); tests use xunit.v3.mtp-v2 4.0.2, JsonSchema.Net 9.6.0 and Microsoft.Playwright 1.64.1 (all already in tests/) | — | — |

- **Not added:** Microsoft.Identity.Web (TL ruling), the OpenIddict client and Microsoft provider (TL ruling), Microsoft.Identity.Client (MSAL, not needed), Azure SDKs (no Azure).
- **Postgres (only when SSE's migration lands, not in S2b phase 1):** whatever SSE pins (Npgsql / EF provider). I add none; I consume SSE's reader. **[OPEN: SSE owns the DB pins]**
- Option for TL: 8.23.0 for Validators + JsonWebTokens + an explicit Protocols.OpenIdConnect 8.23.0 override (3 pins instead of 2, newer). I recommend **8.19.2** (fewer pins, matches the framework package's own dependency).
- `packages.lock.json` + `RestoreLockedMode` stay on; the lock diff is attached to the first code commit for TL review.

## (c) "Down": definition, timeout, fallback audit

### Definition (server-observable only)
Entra is **down for location L** when one of these fails for L's authority:
- **D1 discovery:** GET `{authority}/v2.0/.well-known/openid-configuration` fails (connect error, DNS, TLS, HTTP ≥ 500, 429, a non-JSON body, or an `issuer` that doesn't match the tenant) **or** takes longer than the timeout, **and** there is no last-known-good (LKG) config younger than 24 h **[ASSUMPTION]** in the cache.
- **D2 JWKS:** the same for `jwks_uri` (and an empty key set), with the same LKG rule.
- **D3 token endpoint:** at the callback, code redemption fails with a connect error, timeout, HTTP ≥ 500 or 429. **Not down:** HTTP 400 `invalid_grant` or other OAuth errors (that's a user or flow failure → `auth.login.failed`, no fallback).
- **Not detectable:** Entra's `/authorize` UI failing in the user's browser (we never see it). If the user comes back with `error=temporarily_unavailable` or `server_error` from Entra, that counts as **D4 down**; anything else, or the user never returning, does not. **[OPEN §g Q18]** whether to add a "Use database sign-in" link on the error page for that case.
- The readiness check at `GET /l/{slug}` uses D1+D2 (cached). D3/D4 are found at the callback, which then renders the fallback form (if on) on the same location.
- A failure of a single fetch with a valid LKG cache is **not** down. It is logged (app log) and refresh is retried at 30 s intervals **[ASSUMPTION]**.

### Timeout: proposed **5 s** per back-channel request (discovery, JWKS, token), with **0 retries** inline
- Reasoning: ASP.NET's `BackchannelTimeout` default is 60 s, far longer than a cashier waits at a cage. Entra's endpoints normally answer in well under 1 s **[ASSUMPTION, to measure in the spike; no real tenant in S2b]**. At 5 s the worst case is about 5 s before the user sees the fallback form or the error at `/l/{slug}` (D1/D2 are usually served from cache, so it is 0 s), and 5 s at the callback (D3). 2 s risks false fallbacks on a slow but healthy link; above 10 s feels broken. Config: `LoginService:Entra:BackchannelTimeout` (`00:00:05`), plus `LkgMaxAge` (`24:00:00`) and `RefreshInterval` (`00:00:30`).
- Tests use the fake issuer's `/_fake/delay?ms=` and `/_fake/fail?ep=` controls with the timeout set to 1 s in tests to keep runs short.

### Audit (no contract change in v1 where possible)
- **Within v1 today:** `auth.login.failed` with `provider_kind=entra`, `reason=provider_unavailable` (fits `^[a-z_]+$`; the reason is free text with examples). It is written once per refused or fallen-back Entra attempt, **plus** the normal `auth.login.succeeded` / `failed` with `provider_kind=database` for the fallback sign-in itself.
- **Fallback reason enum (proposed):** `discovery_failed`, `discovery_timeout`, `jwks_failed`, `jwks_timeout`, `token_endpoint_failed`, `token_endpoint_timeout`, `idp_temporarily_unavailable` (D4). Plus `fallback`: `used` | `disabled` (fallback off → no form).
- **v1.1 CONTRACT CHANGE (V11-ENTRA-FALLBACK), not in frozen v1:** `auth.login.failed.details` gets `fallback_reason` (the enum above) and `fallback` (`used|disabled`); `auth.login.succeeded.details` gets `via_fallback: true` and `fallback_reason` on a DB sign-in at an Entra location. Optionally a dedicated event `auth.provider.fallback_used` (a new event type is a v1.1 change, since the envelope `event_type` set is controlled by the SA). **Until v1.1:** the reason enum value goes only into our structured app log, keyed by the event id. No unvalidated field is put in audit details.
- New `reason` values I would add to `auth.login.failed` (also v1.1 to list them in LD §16.19.5): `provider_unavailable`, `unknown_subject`, `location_not_assigned`, `guest_account`, `config_missing`, `config_changed`, `amr_missing`, `token_invalid`. The pattern already allows them, so **[OPEN §g Q12]** whether TL treats adding values as v1 or v1.1.

## (d) Fake Entra issuer and the test list

### Fake issuer `tests/Login.FakeEntra` (test-only project, never in src/)
- Kestrel on **18093** (port guard; never 8080/8081 or 18080–82), HTTP only on loopback. **[ASSUMPTION]** The OIDC handler requires HTTPS metadata unless `RequireHttpsMetadata=false`. That flag is set **only** under the `Testing` environment plus the build guard, and a test (ENT-CFG-01) asserts that production config refuses http authorities.
- Tenant paths `/{tid}/v2.0/.well-known/openid-configuration`, `/{tid}/discovery/v2.0/keys`, `/{tid}/oauth2/v2.0/authorize` (auto-consent page driven by a test-controlled "next user" queue), `/{tid}/oauth2/v2.0/token` (validates code, PKCE S256 verifier, client_id/secret, redirect_uri), `/{tid}/oauth2/v2.0/logout` (records hits with timestamps).
- Keys: RSA-2048 RS256 generated **at start-up** per tenant, held in memory, never written to disk. A second, rotated key is for kid tests.
- Tenants: **generated GUIDs** at start-up. Their display names carry the marker `SSE2-FAKE-TEST-ONLY` (e.g. `tenant-A SSE2-FAKE-TEST-ONLY`). The marker is also put in the `iss` comment field of the fixtures and in every token as a claim `x_fake = "SSE2-FAKE-TEST-ONLY"`, so `check-fixture-secrets.sh` can tell them apart. The client secret is generated per run and is in fixtures only.
- Control API `/_fake/*` (loopback only): `next-user {oid, tid, amr[], acct, idp, auth_time_offset, ver, scp, aud, azp, alg}`, `fail {endpoint, status|timeout}`, `delay {endpoint, ms}`, `rotate-keys`, `endsession-log`.

### Tests (xUnit v3; IDs ENT-*). Each runs against the stand-in on 18090–18092 + fake on 18093.
| ID | Case | Expect |
|---|---|---|
| ENT-01 | right tenant, linked, assigned, amr [pwd,mfa] | session; token `login_provider=entra`, `amr ⊇ mfa`; `auth.login.succeeded provider_kind=entra`, schema-valid |
| ENT-02 | wrong tenant (tid not allowed) | refused, `tenant_mismatch`, no session |
| ENT-02b | MSA tenant 9188040d-… | refused |
| ENT-02c | `iss` GUID ≠ `tid` | refused by AadIssuerValidator |
| ENT-03 | unknown subject (valid tenant, no external_login) | `unknown_subject`; also a user with the **same email** as a linked user → still refused (no email matching) |
| ENT-04 | linked but not assigned to this location | `location_not_assigned` |
| ENT-05 | slug swap: start at /l/A, tamper with state or replay callback at /l/B | refused; state is tamper-proof; location comes only from state |
| ENT-06a–h | amr mapping: [mfa]→mfa; [ngcmfa]→mfa; [wiaormfa]→ no mfa; [wia]; [pwd]; [otp]; [fed]; [none]/[] | per 8.10 table; MfaRequired → reject where no mfa |
| ENT-HO-01 | **rule-2 positive path:** ngcmfa sign-in → hub launch → Cash Advance hand-off | hand-off OK, `auth.app.signed_in method=hub_handoff` with the original auth_time |
| ENT-HO-02 | wiaormfa sign-in → hand-off | re-prompt (5.3), no event (5.10) |
| ENT-07a–f | token shape rows 8–12, 19: ID token as access token, v1.0 token, scp missing, azp wrong, alg≠RS256, unknown kid | refused, `token_invalid` |
| ENT-08 | stale auth_time (> 5 min) | `stale_auth_time` |
| ENT-09 | guest (acct=1; idp≠iss) | refused |
| ENT-10 | uti replay | second refused |
| ENT-11a–c | Entra down (discovery fail / JWKS timeout / token 503), **fallback ON** | DB form shown; `auth.login.failed reason=provider_unavailable provider_kind=entra`; DB sign-in then `succeeded provider_kind=database`; app log has the fallback reason |
| ENT-12a–c | the same, **fallback OFF** | error page, **no `<form>`/password field** (ui-driver check), audit as above |
| ENT-13 | timeout boundary: delay 900 ms vs 1100 ms with test timeout 1 s | up vs down |
| ENT-14 | LKG: discovery fails but cache fresh | not down, Entra redirect |
| ENT-15 | `invalid_grant` at the token endpoint | not down; `auth.login.failed`, no fallback |
| ENT-SO-01 | sign-out order | our revoke + `auth.session.revoked` strictly before the fake's end-session hit; ours done even if the fake is down (ENT-SO-02) |
| ENT-DB-01 | database location | page byte-identical to today (SI1 suite 468/0/84 stays green) |
| ENT-CFG-01 | config null → fail closed; http authority refused outside Testing | per §g Q5 |
| ENT-CFG-02 | row_version changes between challenge and callback | refused (`config_changed`) |
| ENT-PKCE-01 | missing or wrong code_verifier at the fake token endpoint | refused (proves PKCE is sent) |
| ENT-PROMPT-01 | authorize request carries `prompt=login`, PKCE S256, state, nonce | asserted on the fake's request log |

## (e) Code layout: **OWNERSHIP QUESTION (TL must confirm)**
> My standing rule is **tests/, docs/, tools/ only**; SSE owns `contract/`, `src/` and `ui/`. Building S2b means writing in `src/` (and possibly `ui/` for the fallback banner and error page). **I ask TL to confirm I may write in the paths below on `feat/entra-signin` only, and nothing else of SSE's.**

Proposed:
```
src/Login.Entra/                      NEW project (SSE2 owns, on feat/entra-signin)
  Login.Entra.csproj                  net10.0, lock file + RestoreLockedMode, the 2 new pins
  EntraServiceCollectionExtensions.cs AddEntraLocationSignIn(...)
  PerLocationConfigurationManager.cs  authority/discovery cache, LKG, timeouts (§c)
  EntraReadiness.cs                   "down" detection D1–D4
  EntraEvents.cs                      OnRedirectToIdentityProvider / OnAuthorizationCodeReceived / OnTokenValidated (§a.3)
  EntraAccessTokenValidator.cs        §7.1 rule 1 checks
  AmrMapper.cs                        8.10 mapping (pure, unit-tested)
  ExternalLoginLookup.cs              uses SSE's reader/repo interface (no SQL of mine)
  FallbackDecision.cs                 down × AllowDbFallback → form | error, audit reasons
  Fixtures/FixtureLocationLoginConfigReader.cs  until SSE's reader lands
src/Login.StandIn/ (SSE's)            needs ~3 small hook points: Program.cs registration, /l/{slug} branch, endsession step 2.
                                      PROPOSAL: SSE makes these hook edits (or approves a diff from me); I don't edit SSE files unasked.
ui/login-hub/ (SSE's)                 error page "sign-in unavailable" + fallback banner: SSE or UX, or me with TL's OK.
tests/Login.FakeEntra/                test-only fake issuer (mine)
tests/Login.EntraTests/               ENT-* tests (mine)
docs/plans/, docs/BUILD-LOG.md        (mine)
```
- **Alternative B:** everything inside `src/Login.StandIn` (fewer projects, but more edits in SSE's files). Not recommended.

## (f) Dependencies on SSE
1. **Step 0, the git index fix,** before `feat/entra-signin` is created. Today `main` = 9ccfcbe holds only the README merge, and the workspace shows `docs/ tests/ tools/` untracked. **Also found (7:30 AM PT): the repo now has a GitHub remote `origin` (github.com/proofbyteio/FloorFunds) with a gh credential helper, and `origin/*` tracking branches including `origin/sse2/tests-docs`. `tools/check-no-remote.sh` now FAILS.** I did not push and won't. TL must say whether the no-remote rule changed.
2. **`ILocationLoginConfigReader`** [DRAFT-SSE]: `GetAsync(Guid, CancellationToken) → LocationLoginConfig?` (LocationId, ProviderKind Database|Entra, DisplayName, EntraAllowedTenantIds, OidcAuthority?, OidcClientId?, SecretRef? (a reference only), AllowDbFallback, MfaRequired, RowVersion). My positions sent to SSE (by the parent): **no tenant-id lookup**; **add `GetBySlugAsync` or name the existing slug resolver**; `RowVersion` is used for the options cache key and traceability; **null → fail closed** (TL question).
3. **External-login and assignment reads:** an interface for `auth.external_login` lookup by (provider_kind, issuer, tid, subject) and for "user assigned to location". Neither exists yet. I ask SSE to own them alongside the config reader.
4. **DB:** local Postgres 17, DB `ff_sse2` (TL ruling). Env is loaded with `set -a; . /tmp/ff-db/ff_sse2.env` and never printed or committed; reset with `/workspace/cachd-ones/ff-db/ff-db.sh rebuild ff_sse2`. **Schema only from SSE's EF migration; I never hand-create tables or edit the migration.** SSE to list which tables are read-only and which read-write for my role (the `uti` replay store and session writes need some write access, §g Q13).
5. Hook points in `Login.StandIn` and `ui/` (§e).
6. Until 2–3 land: `FixtureLocationLoginConfigReader` plus fixture external-login and assignment data in JSON, with the same records. It is swapped by DI only, so no test changes are needed later.

## (g) Open questions (no guesses; owner in brackets)
1. **[TL]** Branch base: `main` has no `src/`. Should `feat/entra-signin` start from `main` after SSE's step 0 brings in si1-fixes/the stand-in, or from `si1-fixes` (3e0b71d)?
2. **[TL]** Ownership (§e): may I write in `src/Login.Entra/`? Who edits the `Login.StandIn` hooks and `ui/`?
3. **[TL]** Is S2b built into the **stand-in** (Tier 1 dev tooling, build-guarded) or the real login service (OpenIddict host, which doesn't exist yet)? This decides whether the session/token code I hook into is SSE's stand-in.
4. **[TL]** The remote and credential helper now configured (f.1): has the no-remote/no-push rule changed? I assume **not**.
5. **[TL]** No config row → fail closed (my position) vs database+MFA default.
6. **[SA/TL]** The external_login `subject`: Entra `oid` (stable per tenant) or `sub` (pairwise per app)? Entra recommends `oid`+`tid` for cross-app identity; `sub` changes if the app registration changes.
7. **[SSE]** Is `SecretRef` a client secret or a certificate (private_key_jwt)? The certificate path changes `OnAuthorizationCodeReceived`.
8. **[SSE]** Is `OidcAuthority` fixed per location (single tenant), or built per `tid` from `EntraAllowedTenantIds` (multi-tenant)? With several tenants and one authority, which tenant goes in the authorize URL: `organizations` (TL R10 warns issuer validation must stay on) or a tenant chooser?
9. **[SSE/TL]** What does `MfaRequired` mean at an Entra location (enforce §7.1 rule 2 vs record only), and can it be false there?
10. **[TL]** `offline_access`: we don't keep Entra tokens, so I propose **not** requesting it. Confirm.
11. **[TL]** `max_age=0` in addition to `prompt=login`?
12. **[TL/SA]** New `auth.login.failed.reason` values (§c): v1 (the pattern allows them) or v1.1?
13. **[SSE]** Where the `uti` replay store and our session rows live in Postgres (write access).
14. **[TL]** Is the §7.1 rule 3 `acrs`/`c1` auth context in S2b scope?
15. **[TL]** `auth.login.succeeded.details.amr`: raw Entra values (7.1 rule 8 says log the full amr) or our mapped values? The schema field is an untyped array.
16. **[TL]** Entra `rsa` and other non-RFC-8176 values: drop (my plan) or map?
17. **[TL]** Entra end-session: `logout_hint` needs the `login_hint` optional claim. Is it enough to send `client_id` + `post_logout_redirect_uri` (Entra may show an account picker)?
18. **[TL/UX]** D4 (Entra UI failure in the browser): add a "Use database sign-in" link on the error page when fallback is on?
19. **[TL]** Audit fallback detail: v1.1 change V11-ENTRA-FALLBACK (§c) acceptable, with app-log only until then?
20. **[TL]** Package pins: 8.19.2 (recommended) or 8.23.0 (§b).

## (h) Estimate (SSE2 working time; guesses, assuming the answers in §g arrive without rework)
| Step | Work | Duration | Build/test rounds |
|---|---|---|---|
| 0 | Wait for TL approval, SSE step 0 and the branch; create `feat/entra-signin`; restore with the 2 pins, lock file | 0.5 h | 1 |
| 1 | Fake Entra issuer (tests/Login.FakeEntra) + control API + self-tests | 3 h | 3 |
| 2 | src/Login.Entra skeleton, fixture reader, AmrMapper + unit tests | 2 h | 2 |
| 3 | AddOpenIdConnect wiring, per-location config manager, PKCE, AadIssuerValidator, callback | 4 h | 4 |
| 4 | OnTokenValidated chain (§a.3 steps 1–11), access-token validator | 4 h | 4 |
| 5 | Down detection, timeouts, LKG, fallback decision, audit | 3 h | 3 |
| 6 | Sign-out order (ours, then Entra) | 1.5 h | 2 |
| 7 | ENT-* suite (≈45 cases), the hand-off rule-2 positive path, SI1 regression (468/0/84) | 4 h | 3 |
| 8 | Switch to SSE's real reader on ff_sse2 (after the migration lands) | 1.5 h | 2 |
| 9 | Docs, BUILD-LOG, test matrix, fixture-secrets check, self-review | 1.5 h | 1 |
| **Total** | | **25 h ≈ 3.1 working days** (range 20–34 h; +50% if §g Q3 means a real OpenIddict host) | **25** |

Risks: the per-request configuration manager hook in the ASP.NET Core 10 handler (§a.2), and the timing of SSE's interfaces (§f).

## S2b TL rulings (TL approved the plan at 7:27 AM PT, Wed Nov 4, 2026; recorded by SSE2 as TLD-S2B-xx, never DEC)
| ID | Ruling |
|---|---|
| TLD-S2B-01 | Remote rule changed: Mohanraj approved pushing to proofbyteio/FloorFunds. **Only TL pushes**, and only there (a pre-push hook enforces it). SSE2 never pushes and never changes the remote or credentials; a read-only `git fetch` is OK. `tools/check-no-remote.sh` passes only if the remotes are exactly origin=https://github.com/proofbyteio/FloorFunds.git (fetch and push). |
| TLD-S2B-02 | Packages: exactly `Microsoft.AspNetCore.Authentication.OpenIdConnect [10.0.14]` and `Microsoft.IdentityModel.Validators [8.19.2]`, nothing else new. The whole IdentityModel graph stays on 8.19.2. nuget.org only, lock file, package source mapping, no NU3018 suppression. |
| TLD-S2B-03 | "Down" approved as in §c. Timeout 5 s, configurable, no inline retry. JWKS: the framework ConfigurationManager cache; a last-good copy ≤24 h old counts as available (**interim**). |
| TLD-S2B-04 | Ownership: SSE2 owns `src/Login.Entra`, `tests/Login.FakeEntra` (port 18093) and `tests/Login.EntraTests`. Stand-in hooks and ui/ pieces go as a patch file (docs/ or /tmp) for SSE to approve and commit. Login.Entra is self-contained, with no stand-in dependency (it must move to R1 unchanged). |
| TLD-S2B-05 | Branch `feat/entra-signin` off main 9ccfcbe, only if `git status` is clean; otherwise report, never force. S2b docs stay on sse2/tests-docs (`docs/plans/S2B-ENTRA-PLAN.md`, this log). |
| TLD-S2B-06 | Link users on (issuer, tid, oid). Never on email, upn or preferred_username. (Plan §g Q6.) |
| TLD-S2B-07 | No `offline_access`. (Q10) |
| TLD-S2B-08 | Cage locations: `prompt=login` + `max_age=0`. The `auth_time` optional claim is required and checked server-side, ≤5 min for the hand-off. The server check is authoritative. (Q11) |
| TLD-S2B-09 | New `reason` values are v1.1, under **V11-ENTRA-FALLBACK**. Within v1, existing reasons only. (Q12, Q19) |
| TLD-S2B-10 | Storage and grants per SSE's P1 spec. TL has told SSE to add the external-login lookup and the location-assignment check. Until they land: draft interfaces plus fixture-backed implementations, with the assumption logged. (Q13) |
| TLD-S2B-11 | acrs/c1: next slice. (Q14) |
| TLD-S2B-12 | Audit carries only the mapped amr (closed enum). Entra's raw amr is never logged. (Q15) |
| TLD-S2B-13 | Drop `rsa`. (Q16) |
| TLD-S2B-14 | The account picker at Entra sign-out is OK for now; `logout_hint` is next slice. (Q17) |
| TLD-S2B-15 | No browser-side database sign-in link; fallback is decided by the server only. (Q18) |
| TLD-S2B-16 | (TL 7:25, relayed by SSE) No config row → **fail closed**. (Q5) |
| TLD-S2B-17 | (TL 7:25) Use `GetBySlugAsync` with `auth.location`. |
| TLD-S2B-18 | (TL 7:25) Authority = base URL + `/{tid}/v2.0`, **exactly one tenant per location**. (Q8) |
| TLD-S2B-19 | (TL 7:25) `SecretRef` is opaque. (Q7) |
| TLD-S2B-20 | (TL 7:25) `mfa_required` means amr must map to `mfa`, otherwise refuse. (Q9) |
| TLD-S2B-21 | (TL 7:25) RowVersion goes on events in v1.1. |
| TLD-S2B-22 | DB: local Postgres 17, `ff_sse2`. Schema only from SSE's EF migration; the fixture reader until it lands (plan step 8 waits). |
Still open from plan §g: Q1 (resolved by TLD-S2B-05), Q3 (stand-in vs real service: Login.Entra is self-contained per TLD-S2B-04, hooks via patch), Q20 (resolved by TLD-S2B-02).
