# Login service contract v1 (DRAFT v1-d2)

Labels and source snapshots: see `README.md`. PLAN = build plan md5 ae18d664; LD = login doc md5 88878681 (rev 11:41).
"MUST" items are what contract tests may assert against both the stand-in and the real service.

## 1. Issuer, listeners, configuration

| # | Item | Label |
|---|---|---|
| 1.1 | Real issuer is `https://login.<domain>/`, set explicitly (never derived from the request host), so tokens issued on 8084 still carry the public `iss` | [SRC: LD §4.5 L211] |
| 1.2 | Stand-in issuer path always contains `/standin`, e.g. `http://localhost:5080/standin` | [SRC: PLAN §3.5 table; LD §16.17.5 L1317] |
| 1.3 | Front listener: browser-facing (discovery, JWKS, authorize, endsession, `/l/{slug}`, `/apps`, `/admin`). Stand-in example port 5080 | [SRC: PLAN §3.1 L104-113, §3.3]; discovery/JWKS on front [TL Decision 2026-11-02, OQ-1] |
| 1.4 | Back listener **8084**: maps only `/connect/token`, `/connect/revoke`, `/connect/introspect` (and, per LD, discovery/JWKS: see 2.9); everything else 404. On the front listener token/revoke/introspect return 404 | [SRC: LD §4.5 L207-209; TL Decision 2026-11-02, OQ-1] |
| 1.5 | Stand-in control API `/_standin/*` only on a separate 127.0.0.1 listener; never part of the client contract; tests using it are tagged `standin-only` | [SRC: PLAN §3.2, §3.3] |
| 1.6 | Clients read only `Auth:Authority`, `Auth:ClientId`, secret or key, and the back-channel base URL from config; they never hard-code a path, port, `kid` or stand-in claim | [SRC: PLAN §3.4] |
| 1.7 | Allowlist runs first on every front request; a blocked caller gets the POC's minimal 403 (errors.md §4), not an OAuth error | [SRC: PLAN L108, L146; ALW L30-50] |

## 2. Discovery: `GET /.well-known/openid-configuration` (front)

| # | Item | Label |
|---|---|---|
| 2.1 | Fields: `issuer`, `authorization_endpoint`, `token_endpoint`, `revocation_endpoint`, `end_session_endpoint`, `jwks_uri`, `response_types_supported=["code"]`, `grant_types_supported=["authorization_code","refresh_token"]`, `code_challenge_methods_supported=["S256"]`, `token_endpoint_auth_methods_supported=["client_secret_basic","private_key_jwt"]`, `id_token_signing_alg_values_supported=["RS256"]`. Schema: `openid-configuration.schema.json` | [SRC: PLAN §3.1 L106] |
| 2.2 | **Served on the front listener**: public (no secrets), read-only, cacheable; browsers and clients need the front issuer. Resolves the PLAN L106-107 / LD §16.17.2 L1299 ("front") vs LD §4.5 L204/L207 (8084 only) conflict in favour of front. LD §4.5 still to be aligned by the SA | [TL Decision 2026-11-02, OQ-1]; [SRC: PLAN L106-107; LD L1299] |
| 2.3 | URLs in the document: every endpoint URL is under the public issuer (`https://login.<domain>/...`), including `token_endpoint` and `revocation_endpoint`, which answer 404 on the front; server-side clients send token/revoke/introspect to their configured back-channel base URL (8084). No internal host name is published | [SSE Proposal → OQ-3] (was A-1: internal URLs); back-channel base URL [SRC: PLAN §3.4] |
| 2.4 | `subject_types_supported=["public"]` (required by OIDC Discovery 1.0, not in PLAN) | [ASSUME A-2 → OQ-4] |
| 2.5 | `introspection_endpoint` is **not** published (PLAN lists the endpoint but not the field; it is "optional for clients") | [ASSUME A-3 → OQ-4] |
| 2.6 | `scopes_supported`, `claims_supported`, `prompt_values_supported` not published in v1 | [ASSUME A-4 → OQ-4] |
| 2.7 | No `userinfo_endpoint`; no `frontchannel_logout_*` / `backchannel_logout_*` | [SRC: PLAN L123, §1.3 L60] |
| 2.8 | Caching: "cacheable" is decided, the header value is not. Draft: `Cache-Control: public, max-age=3600` on discovery and JWKS (a new key is published ≥24 h before it signs, 3.2) | cacheable [TL Decision 2026-11-02, OQ-1]; value [SSE Proposal → OQ-30] |
| 2.9 | LD §4.5 L207 also maps discovery/JWKS on 8084; the TL answer is silent on keeping that copy. Draft: 8084 serves the same documents too, so the BFF and API (not on the casino allowlist) can fetch them | [DOC SILENT → OQ-31] |
| 2.10 | Does the allowlist (1.7) apply to front discovery/JWKS? "Public" (TL) vs "allowlist first on every front request". Draft: yes, no exception; servers use the 8084 copy (2.9) | [DOC SILENT → OQ-32] |

## 3. JWKS: `GET /.well-known/jwks` (front; see 2.2, 2.8-2.10)

| # | Item | Label |
|---|---|---|
| 3.1 | Public keys only, each with `kid`; `kty=RSA`, `use=sig`, `alg=RS256` | [SRC: PLAN L107; alg from L106] / `use` [ASSUME A-5 → OQ-4] |
| 3.2 | Rotation: a new key is published ≥24 h before it signs; real rotation every 90 days with overlap. Stand-in fault switch may shorten this | [SRC: LD §5 L232; PLAN L107] |
| 3.3 | Stand-in keys: in memory, new at each start, `kid` starts `standin-`. A real JWKS never has a `standin-` kid | [SRC: PLAN §3.5; LD §16.17.5 L1318] |

## 4. Authorize: `GET /connect/authorize` (front)

| # | Item | Label |
|---|---|---|
| 4.1 | `response_type=code` only; PKCE required with `code_challenge_method=S256` only; `state`, `nonce`, `prompt`, `max_age` supported | [SRC: PLAN L108] |
| 4.2 | Check order: allowlist → authenticated? → `prompt` / `max_age` / hand-off policy → role in the client's `app_code` → issue code | [SRC: PLAN L108; LD §16.5 L898] |
| 4.3 | No role in the client's `app_code` → redirect with `error=access_denied`, no code, audit `auth.app_access.denied reason=no_role` | [SRC: PLAN L143, S1 check 1 L238; LD §16.5 L898] |
| 4.4 | Code: lifetime ≤60 s, single use; a second redemption → `invalid_grant` | [SRC: PLAN L108, S1 check 8] |
| 4.5 | `auth.app.signed_in` is written in the same transaction that issues the code; if the write fails, no code: `error=temporarily_unavailable` | [SRC: LD §16.5 L902; PLAN L145, S1 check 9] |
| 4.6 | `prompt=none` with no usable session → `login_required` / `interaction_required` | [SRC: PLAN L144] |
| 4.7 | SSO-eligible clients send `max_age=900`; an older `auth_time` forces re-sign-in. Login cookie: 15-min sliding idle | [SRC: LD §12 row 58 L642; PLAN S1 check 6] |
| 4.8 | `redirect_uri` must exactly match a registered value; otherwise an error page on our host, no redirect | [ASSUME A-6 → OQ-5] (RFC 6749 §4.1.2.1 behaviour) |
| 4.9 | Scope values: `openid` plus one API scope per client API; mixed-audience scopes → `invalid_scope`; `offline_access` needed for a refresh token | names [ASSUME A-7 → OQ-6]; `invalid_scope` [SRC: PLAN L141] |

## 5. Hand-off (Cash Advance client only)

| # | Item | Label |
|---|---|---|
| 5.1 | The Cash Advance authorize request drops `prompt=login`. The existing login session is accepted only if rules 1-6 all hold: (1) interactive and `auth_time` ≤5 min, proven by this sign-in's upstream token; (2) `mfa` in `amr`; (3) one-shot (`cash_advance_handoff_used`); (4) from the hub with a hub-issued value; (5) allowlist and location checks as normal; (6) the session then becomes `cage_consumed` (not SSO-eligible for anything) | [SRC: LD §16.16.1 L1167-1172; PLAN L126-132] |
| 5.2 | **Plus a valid agent attestation** (6.4-6.8): signed with the enrolled device's TPM key, ≤60 s, single use, bound to the transaction nonce and device id, `remote_session=false`. Fails closed | [SRC: PLAN §1.2 L51, L133; LD §16.16.1 L1174]; format [TL Decision 2026-11-02, OQ-8/OQ-9] |
| 5.3 | Any rule 1-6 failure → the sign-in page again (re-prompt, not an OAuth error) | [SRC: PLAN L134; LD L1301] |
| 5.4 | Attestation says `remote_session=true` → `auth.app_access.denied reason=remote_session`, **screen 8 (Session), variant A "remote session"**; fallback sign-in on that PC is refused too until it ends (`auth.login.failed reason=remote_session`); if the user cancels → `error=access_denied` to the client | [SRC: LD L1179, L1374; PLAN L134, S1 checks 4, 4b]; screen name [TL Decision 2026-11-02, OQ-29] |
| 5.5 | Attestation missing / bad signature / expired / replayed / unknown device → `auth.app_access.denied reason=attestation_invalid` → normal password + MFA sign-in; Cash Advance then opens but cage actions stay blocked until the agent attests again (**screen 8 (Session), variant B "security software not responding"**: "This PC's security software isn't responding. Contact IT.") | [SRC: PLAN §1.2, S1 checks 4a, 4b; LD L1180, L1375]; screen name [TL Decision 2026-11-02, OQ-29] |
| 5.6 | Successful hand-off → `auth.app.signed_in method=hub_handoff` with the original `auth_time` and login session id | [SRC: PLAN S1 check 2; LD L1171] |
| 5.7 | **`hub_launch`** (rule 4). The hub / login service issues an opaque `hub_launch` value: single use, valid ≤60 s, stored server-side, bound to the login session id and the target `client_id`. The client passes it unchanged to `/connect/authorize` as parameter `hub_launch`; the login service checks and consumes it. Missing, expired, already used or bound to another session or client → rule 4 fails: `auth.app_access.denied reason=notfromhub`, then the sign-in page (5.3). Replaces "a `state` the hub issued"; `state` stays the client's own | [TL Decision 2026-11-02, OQ-7; SRC: LD §16.16.1 L1170, §16.18.5 L1388-1391, §8 L423]; link format, other clients, consume-on-failure [SSE Proposal → OQ-36] |
| 5.8 | **Attestation on the hand-off.** The attestation travels as `device_attestation` (6.6). Which page calls the agent on a hand-off, and whether the client must forward `device_attestation`, is not stated. Draft: the login service answers the hand-off authorize with a short page on its own host that gets a nonce, calls the agent and re-submits authorize by POST with `device_attestation`; clients forward nothing but `hub_launch` | transport [TL Decision 2026-11-02, OQ-8]; page flow [SSE Proposal → OQ-34] |
| 5.9 | Attestation format: see 6.5 and `device-attestation.schema.json` | [TL Decision 2026-11-02, OQ-9] |
| 5.10 | A failure of rules 1-3, 5 or 6 (re-prompt) writes no `auth.app_access.denied`; the following sign-in writes its normal events. Rule 4 does write one (5.7) | rule 4 [SRC: LD L1170]; others [SSE Proposal → OQ-10] |

## 6. Plain sign-in check (best effort) and the agent attestation (all uses)

| # | Item | Label |
|---|---|---|
| 6.1 | `/l/{slug}` creates a per-transaction nonce; on a cage PC managed Edge gets an attestation; `remote_session=true` → `auth.login.failed reason=remote_session`, screen 8 (Session) variant A "remote session"; **no attestation → sign-in goes ahead** | [SRC: LD §16.18.5 L1364-1367] |
| 6.2 | Attestation present but invalid at plain sign-in: sign-in goes ahead (best effort, like "none"); no extra event; hand-off and cage actions still fail closed | [SSE Proposal → OQ-11] |
| 6.3 | Lockout: 10 wrong passwords → `auth.account.locked`, refused for 30 min | [SRC: PLAN L34, S1 check 10] |
| 6.4 | **Nonce:** created by the login service for each sign-in (`/l/{slug}`) or hand-off transaction; stored server-side, single use, valid ≤60 s, bound to that transaction | [TL Decision 2026-11-02, OQ-8] |
| 6.5 | **Agent call:** the page calls the local device agent on loopback (allowed by managed Edge policy). The agent returns a compact JWS: `alg=ES256`, TPM key, `kid` = enrolled device id; claims `nonce`, `device_id`, `remote_session`, `iat`, `exp`. Schema: `device-attestation.schema.json` | [TL Decision 2026-11-02, OQ-8/OQ-9; SRC: LD §16.18.5 L1381-1386] |
| 6.6 | **Transport:** the page sends the JWS as `device_attestation`: a form field on the sign-in POST, or an authorize parameter (5.8) | [TL Decision 2026-11-02, OQ-8] |
| 6.7 | **Server check:** signature against the enrolled device key registry (`kid` → key); `device_id` = `kid`; `nonce` matches an unconsumed nonce of this transaction (then consumed); `exp` not passed; `exp - iat` ≤60 s. Any failure = `attestation_invalid`. Fails closed for hand-off and cage actions | [TL Decision 2026-11-02, OQ-8/OQ-9]; `exp - iat`, skew, `typ` [SSE Proposal → OQ-35] |
| 6.8 | **Stand-in:** fakes the agent per PC id with the modes valid, missing, bad signature, expired, replayed, unknown device, remote session; fake device ids and keys start `standin-` | modes [SRC: PLAN §3.2 L199; TL Decision 2026-11-02]; mechanism and prefix [SSE Proposal → OQ-33] |

## 7. Token: `POST /connect/token` (back, 8084)

| # | Item | Label |
|---|---|---|
| 7.1 | Grants `authorization_code` (with `code_verifier`) and `refresh_token`; client auth `client_secret_basic` or `private_key_jwt` | [SRC: PLAN L106, L109; LD L209] |
| 7.2 | Rolling refresh; reuse after ≤30 s leeway → `invalid_grant`, chain revoked, `auth.refresh.reuse_detected` | [SRC: PLAN L109, S1 check 8; LD §5 L226] |
| 7.3 | Refresh re-checks user status and role; no role → `invalid_grant` | re-check [SRC: PLAN L109]; error code [ASSUME A-11 → OQ-12] |
| 7.4 | Absolute session cap: default **12 h**, configurable. Setting `LoginService:Session:AbsoluteLifetime` (TimeSpan, e.g. `12:00:00`; env `LoginService__Session__AbsoluteLifetime`). The login session and its refresh-token family end at sign-in time + cap, never later; refresh lifetime = the cap. Bounds and validation: 1 h ≤ value ≤ 24 h, checked at startup, invalid → the service does not start (no silent clamp); the value in force is logged at startup; a change applies to new sessions only | rule [SRC: LD L226, L229]; 12 h + configurable [Mohanraj Decision 2026-11-02 (t43u), OQ-13]; setting name, bounds, validation [SSE Proposal → OQ-40] |
| 7.5 | Response: `access_token`, `token_type=Bearer`, `expires_in=300`, `id_token` (code grant), `refresh_token` (when granted), `scope` | [ASSUME A-13 → OQ-4] (RFC 6749 §5.1 shape) |

## 8. Tokens and claims (schema: `claims.schema.json`)

| # | Item | Label |
|---|---|---|
| 8.1 | Access token: JWT RS256, 5 min, `aud` = that client's API. Claims `iss`, `sub`, `aud`, `exp`, `iat`, `jti`, `scope`, `client_id`, `login_provider` (`database`\|`entra`), `amr`, `auth_time` | [SRC: PLAN L116-118; LD §5 L225-227] |
| 8.2 | `client_id` / `azp` = the OAuth client; never used for tenant scoping | [SRC: LD §16.5 L894] |
| 8.3 | `perm_version`: Cachd clients only | [SRC: PLAN L119; LD L895] |
| 8.4 | `partner_id`, `cachd_client_id` (absent for partner-level users), `location_id`: Cachd always; other clients only when `org_claims` is on (default off) | [SRC: PLAN L120; LD L895] |
| 8.5 | No roles or permissions in any token | [SRC: PLAN L121; LD L896] |
| 8.6 | ID token: `iss`, `sub`, `aud`, `azp`, `exp`, `iat`, `nonce`, `auth_time`, `amr`. No `sid`, no userinfo | [SRC: PLAN L122-123] |
| 8.7 | Stand-in tokens carry `x_standin: true`; outside Development the shared JwtBearer rejects `x_standin`, a `/standin` issuer or a `standin-` kid | [SRC: PLAN §3.5; S1 check 11] |
| 8.8 | API rejects wrong `aud`, bad signature, expired token → 401 | [SRC: PLAN S1 check 8] |
| 8.9 | Header `typ=at+jwt` (RFC 9068) on access tokens; `JWT` on ID tokens | [ASSUME A-14 → OQ-14] |
| 8.10 | Claim value types (`sub`, org ids as UUID strings; `perm_version` integer), `amr` values, ID-token lifetime, allowed clock skew | [ASSUME A-15 → OQ-15] |

## 9. Revoke: `POST /connect/revoke` (back, 8084)

| # | Item | Label |
|---|---|---|
| 9.1 | RFC 7009: `token`, optional `token_type_hint`; client auth required; 200 for unknown or already revoked tokens | [SRC: PLAN L110 (RFC 7009)] |
| 9.2 | Revoking a refresh token revokes its chain | [ASSUME A-16 → OQ-12] |

## 10. Introspect: `POST /connect/introspect` (back, 8084)

| # | Item | Label |
|---|---|---|
| 10.1 | Internal only; optional for clients; RFC 7662 response (`active`, and when active `sub`, `client_id`, `exp`, `iat`, `scope`, `token_type`) | internal/optional [SRC: PLAN L111]; shape [ASSUME A-17 → OQ-4] |

## 11. End session: `GET|POST /connect/endsession` (front)

| # | Item | Label |
|---|---|---|
| 11.1 | Revokes every authorization and token from **that login session** across all clients (not by `sub`), then redirects to the location URL (Entra end-session first at Entra locations) | [SRC: PLAN L112; LD §16.5 L903] |
| 11.2 | Parameters `id_token_hint`, `post_logout_redirect_uri`, `state` (OIDC RP-Initiated Logout 1.0); when the user has several locations the redirect uses the login session's location | [ASSUME A-18 → OQ-16] |
| 11.3 | Writes `auth.session.revoked` | [SRC: PLAN L155]; per-endsession [ASSUME A-19 → OQ-10] |

## 12. Audit events: `audit-events.schema.json`

Events listed in PLAN L148-161 and LD §8 L414-424, envelope per AUD §7A.4. Details in the schema.

## 13. Versioning

[SRC: PLAN L163] Freeze after SSE2's adversarial pass and TL review; after that only `v1.1` edits with the
reason and a contract-test change in the same change set. Every `ASSUME` above must be resolved first.
