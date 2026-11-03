# Login service contract v1 (DRAFT v1-d4)

Labels and source snapshots: see `README.md`. PLAN = build plan md5 10fa9853 (check-3 edit only, no line shift); LD = login doc md5 c2b7e689 (rev 8:20 PM PT, file time 20:11:45; cites re-mapped from b7385794).
"MUST" items are what contract tests may assert against both the stand-in and the real service.

## 0. Global rules [TL Decision 2026-11-02, G1, G2]

- **G1. Clock skew ±30 s** on every time check: JWT `exp`/`nbf`/`iat`, client assertions (7.6) and the attestation `iat`/`exp` (6.7). .NET defaults to 5 min, so every validator MUST set `TokenValidationParameters.ClockSkew = 00:00:30` explicitly. Contract test: a token 31 s past `exp` gets 401. Server-stored handles get **no skew** (only the server clock is involved): code (≤60 s), nonce (≤60 s), `hub_launch` (≤60 s).
- **G2. Nothing secret or diagnostic in a URL or a client-visible error.** LD L251 item 7 applies to `device_attestation` (POST body only, 6.6). Internal reason codes stay in audit only (errors.md §1; LD §16.17.2 L1314; PLAN L146). Exception recorded: `hub_launch` (5.7).

## 1. Issuer, listeners, configuration

| # | Item | Label |
|---|---|---|
| 1.1 | Real issuer is `https://login.<domain>/`, set explicitly (never derived from the request host), so tokens issued on 8084 still carry the public `iss` | [SRC: LD §4.5 L213] |
| 1.2 | Stand-in issuer path always contains `/standin`, e.g. `http://localhost:5080/standin` | [SRC: PLAN §3.5 table; LD §16.17.5 L1329] |
| 1.3 | Front listener: browser-facing (discovery, JWKS, authorize, endsession, `/l/{slug}`, `/apps`, `/admin`). Stand-in example port 5080 | [SRC: PLAN §3.1 L104-113, §3.3]; discovery/JWKS on front [TL Decision 2026-11-02, OQ-1] |
| 1.4 | Back listener **8084**: maps only `/connect/token`, `/connect/revoke`, `/connect/introspect` (and, per LD, discovery/JWKS: see 2.9); everything else 404. On the front listener token/revoke/introspect return 404 | [SRC: LD §4.5 L209-211; TL Decision 2026-11-02, OQ-1] |
| 1.5 | Stand-in control API `/_standin/*` only on a separate 127.0.0.1 listener; never part of the client contract; tests using it are tagged `standin-only` | [SRC: PLAN §3.2, §3.3] |
| 1.6 | Clients read only `Auth:Authority`, `Auth:ClientId`, secret or key, and the back-channel base URL from config; they never hard-code a path, port, `kid` or stand-in claim. **MUST:** clients do not take `token_endpoint` / `revocation_endpoint` from discovery; both are overridden from config (back-channel base URL). Contract test: the BFF stub never calls the front `/connect/token`. **MUST (JWKS refresh):** .NET `ConfigurationManager` ignores HTTP cache headers (default refresh 12 h), so every client and API sets `AutomaticRefreshInterval = 00:05:00` and `RefreshInterval = 00:01:00`; an unknown `kid` triggers one throttled refetch | [SRC: PLAN §3.4]; config override [TL Decision 2026-11-02, OQ-3] ; JWKS refresh [TL Decision 2026-11-02, OQ-30] |
| 1.7 | Allowlist runs first on every front request; a blocked caller gets the POC's minimal 403 (errors.md §4), not an OAuth error | [SRC: PLAN L108, L146; ALW L30-50] |
| 1.8 | 8084 exposure, two layers, each tested. (1) **Network:** 8084 has no ALB target group and its SG allows only the BFF and API SGs; any other caller gets no connection (infra/security tests). (2) **App:** a caller past the SG but not on the internal source list gets the same minimal 403 as errors.md §4 (contract tests). **Never:** 8084 behind any ALB or ingress, in discovery, or in any client-visible URL | [SRC: LD §4.5 L207-214; ALW L30-50]; [TL Decision 2026-11-02, OQ-20] |

## 2. Discovery: `GET /.well-known/openid-configuration` (front)

| # | Item | Label |
|---|---|---|
| 2.1 | Fields: `issuer`, `authorization_endpoint`, `token_endpoint`, `revocation_endpoint`, `end_session_endpoint`, `jwks_uri`, `response_types_supported=["code"]`, `grant_types_supported=["authorization_code","refresh_token"]`, `code_challenge_methods_supported=["S256"]`, `token_endpoint_auth_methods_supported=["client_secret_basic","private_key_jwt"]`, `id_token_signing_alg_values_supported=["RS256"]`. Schema: `openid-configuration.schema.json` | [SRC: PLAN §3.1 L106] |
| 2.2 | **Served on the front listener**: public (no secrets), read-only, cacheable; browsers and clients need the front issuer. Resolves the PLAN L106-107 / LD §16.17.2 L1311 ("front") vs LD §4.5 L206/L209 (8084 only) conflict in favour of front. LD §4.5 still to be aligned by the SA | [TL Decision 2026-11-02, OQ-1]; [SRC: PLAN L106-107; LD L1311] |
| 2.3 | URLs in the document: every endpoint URL is under the public issuer (`https://login.<domain>/...`), including `token_endpoint` and `revocation_endpoint`, which answer 404 on the front; server-side clients send token/revoke/introspect to their configured back-channel base URL (8084, see 1.6). No internal host name is published | [TL Decision 2026-11-02, OQ-3] (Accept); back-channel base URL [SRC: PLAN §3.4; LD §4.5 L211, L213] |
| 2.4 | **Stand-in publishes exactly** the 2.1 set plus `subject_types_supported=["public"]`, `token_endpoint_auth_signing_alg_values_supported=["RS256"]`, `revocation_endpoint_auth_methods_supported=["client_secret_basic","private_key_jwt"]` and `authorization_response_iss_parameter_supported=true` (4.10); contract tests assert the exact key set. **Real service** may add standard fields (schema `additionalProperties: true`); tests assert the required set is present and the `not` list absent | [TL Decision 2026-11-02, OQ-4] |
| 2.5 | `introspection_endpoint` is **not** published | [TL Decision 2026-11-02, OQ-4] (A-3 accepted) |
| 2.6 | `scopes_supported`, `claims_supported`, `prompt_values_supported` not published in v1 | [TL Decision 2026-11-02, OQ-4] (A-4 accepted) |
| 2.7 | No `userinfo_endpoint`; no `frontchannel_logout_*` / `backchannel_logout_*` | [SRC: PLAN L123, §1.3 L60] |
| 2.8 | Discovery: `Cache-Control: public, max-age=3600` | cacheable [TL Decision 2026-11-02, OQ-1]; value [TL Decision 2026-11-02, OQ-30] |
| 2.9 | 8084 also serves discovery and JWKS (the BFF and API are not on the casino allowlist), with the **same bytes and the same cache headers** as the front (issuer and URLs public, 2.3); a contract test compares them | [SRC: LD §4.5 L209, L206]; [TL Decision 2026-11-02, OQ-31] (Accept) |
| 2.10 | No allowlist exception: the allowlist (1.7) runs first on every front request, including discovery and JWKS. Server-side clients use the 8084 copy (2.9); browsers don't need JWKS | [SRC: PLAN L108, L146]; [TL Decision 2026-11-02, OQ-32] (Accept) |

## 3. JWKS: `GET /.well-known/jwks` (front; see 2.2, 2.8-2.10)

| # | Item | Label |
|---|---|---|
| 3.1 | Public keys only, each with `kid`; `kty=RSA`, `use=sig`, `alg=RS256` | [SRC: PLAN L107; alg from L106]; `use=sig` [TL Decision 2026-11-02, OQ-4] (A-5 accepted) |
| 3.2 | Rotation: a new key is published ≥24 h before it signs; real rotation every 90 days with overlap. Stand-in fault switch may shorten this | [SRC: LD §5 L234; PLAN L107] |
| 3.3 | Stand-in keys: in memory, new at each start, `kid` starts `standin-`. A real JWKS never has a `standin-` kid | [SRC: PLAN §3.5; LD §16.17.5 L1330] |
| 3.4 | JWKS: `Cache-Control: public, max-age=300`, with no `stale-while-revalidate` / `stale-if-error` and no CDN or shared cache in front. A retired key stays in JWKS until every token it signed has expired (≥ 5-min token life + skew; the 90-day overlap covers this). An emergency-removed key stops being trusted within ≤5 min + 60 s (client rule 1.6) | [TL Decision 2026-11-02, OQ-30] |

## 4. Authorize: `GET /connect/authorize` (front)

| # | Item | Label |
|---|---|---|
| 4.1 | `response_type=code` only; PKCE required with `code_challenge_method=S256` only; `state`, `nonce`, `prompt`, `max_age` supported | [SRC: PLAN L108] |
| 4.2 | Check order: allowlist → authenticated? → `prompt` / `max_age` / hand-off policy → role in the client's `app_code` → issue code | [SRC: PLAN L108; LD §16.5 L910] |
| 4.3 | No role in the client's `app_code` → redirect with `error=access_denied`, no code, audit `auth.app_access.denied reason=no_role` | [SRC: PLAN L143, S1 check 1 L238; LD §16.5 L910] |
| 4.4 | Code: lifetime ≤60 s, single use; a second redemption → `invalid_grant` | [SRC: PLAN L108, S1 check 8] |
| 4.5 | `auth.app.signed_in` is written in the same transaction that issues the code; if the write fails, no code: `error=temporarily_unavailable` | [SRC: LD §16.5 L914; PLAN L145, S1 check 9] |
| 4.6 | `prompt=none` with no usable session → `login_required` / `interaction_required` | [SRC: PLAN L144] |
| 4.7 | SSO-eligible clients send `max_age=900`; an older `auth_time` forces re-sign-in. Login cookie: 15-min sliding idle | [SRC: LD §12 row 58 L644; PLAN S1 check 6] |
| 4.8 | `redirect_uri` must **exactly** match (string compare) a registered value: no wildcards, no prefix match, no localhost exceptions outside Development. A bad `redirect_uri` or `client_id` → an error page on our host, **never a redirect**; generic text ("This sign-in link isn't valid"); never echoes `client_id`, `redirect_uri` or any parameter; `Cache-Control: no-store`; `Content-Security-Policy: frame-ancestors 'none'`. No audit event in v1 | [TL Decision 2026-11-02, OQ-5] (RFC 6749 §4.1.2.1) |
| 4.9 | Scopes `openid`, `cash_advance_api`, `backoffice_api`; `aud` values `cash-advance-api`, `backoffice-api`; clients `cash_advance_bff`, `backoffice_stub`, `hub` (Reports has no client in S1). Mixed-audience scopes → `invalid_scope`; an **unknown scope** → `invalid_scope`; `offline_access` is required for a refresh token, and `offline_access` from a client not registered for `refresh_token` → `invalid_scope` | names, unknown scope, offline_access [TL Decision 2026-11-02, OQ-6] (Accept; SA records them in LD §16.17); mixed `invalid_scope` [SRC: PLAN L141] |
| 4.10 | `iss` goes on **every** authorization response, success and error (RFC 9207 §2); discovery publishes `authorization_response_iss_parameter_supported=true`; clients MUST check it | [TL Decision 2026-11-02, OQ-17] |

## 5. Hand-off (Cash Advance client only)

| # | Item | Label |
|---|---|---|
| 5.1 | The Cash Advance authorize request drops `prompt=login`. The existing login session is accepted only if rules 1-6 all hold: (1) interactive and `auth_time` ≤5 min, proven by this sign-in's upstream token; (2) `mfa` in `amr`; (3) one-shot (`cash_advance_handoff_used`); (4) from the hub with a hub-issued value; (5) allowlist and location checks as normal; (6) the session then becomes `cage_consumed` (not SSO-eligible for anything) | [SRC: LD §16.16.1 L1179-1184; PLAN L126-132] |
| 5.2 | **Plus a valid agent attestation** (6.4-6.8): signed with the enrolled device's TPM key, ≤60 s, single use, bound to the transaction nonce and device id, `remote_session=false`. Fails closed | [SRC: PLAN §1.2 L51, L133; LD §16.16.1 L1186]; format [TL Decision 2026-11-02, OQ-8/OQ-9] |
| 5.3 | Any rule 1-6 failure → the sign-in page again (re-prompt, not an OAuth error) | [SRC: PLAN L134; LD L1313] |
| 5.4 | Attestation says `remote_session=true` → `auth.app_access.denied reason=remote_session`, **screen 8 (Session), variant A "remote session"**; fallback sign-in on that PC is refused too until it ends (`auth.login.failed reason=remote_session`); if the user cancels → `error=access_denied` to the client | [SRC: LD L1191, L1386; PLAN L134, S1 checks 4, 4b]; screen name [TL Decision 2026-11-02, OQ-29] |
| 5.5 | Attestation missing / bad signature / expired / replayed / unknown device → `auth.app_access.denied reason=attestation_invalid` → normal password + MFA sign-in; Cash Advance then opens but cage actions stay blocked until the agent attests again (**screen 8 (Session), variant B "security software not responding"**: "This PC's security software isn't responding. Contact IT.") | [SRC: PLAN §1.2, S1 checks 4a, 4b; LD L1192, L1387]; screen name [TL Decision 2026-11-02, OQ-29] |
| 5.6 | Successful hand-off → `auth.app.signed_in method=hub_handoff` with the original `auth_time` and login session id | [SRC: PLAN S1 check 2; LD L1183] |
| 5.7 | **`hub_launch`** (rule 4). The hub / login service issues an opaque `hub_launch` value: single use, valid ≤60 s, stored server-side, bound to the login session id and the target `client_id`. The client passes it unchanged to `/connect/authorize` as parameter `hub_launch`; the login service checks and consumes it. Missing, expired, already used or bound to another session or client → rule 4 fails: `auth.app_access.denied reason=notfromhub`, then the sign-in page (5.3). Link: the tile goes to the client's start URL with `?hub_launch=<value>`; clients without the hand-off ignore it; consumed on the first check even if another rule fails. **Add:** the value is 128 bits from a CSPRNG, base64url, stored hashed; issued only for tiles the user has a role for (PLAN L40); a value bound to another client or session → `notfromhub`, consumed. **URL exception (recorded):** a code-class handle (single use, ≤60 s, bound), so a query parameter is accepted like `code`, but ALB and app logs redact `hub_launch`, `code` and `state` values on both hosts. Replaces "a `state` the hub issued"; `state` stays the client's own | [TL Decision 2026-11-02, OQ-7; SRC: LD §16.16.1 L1182, §16.18.5 L1400-1403, §8 L425]; link format, other clients, consume-on-failure [TL Decision 2026-11-02, OQ-36] |
| 5.8 | **Attestation on the hand-off.** The login service answers the hand-off authorize with a short interstitial page on its own host that gets a nonce, calls the agent and re-submits authorize **by POST** with `device_attestation`; clients forward nothing but `hub_launch`. **MUST:** (a) `device_attestation` is never sent in a URL: POST body only, on sign-in and on authorize (G2); (b) the interstitial is bound to a server-side transaction id (nonce + `hub_launch` + login session + `client_id`); the POST must match it, or it fails as `attestation_invalid`; (c) the interstitial has `Content-Security-Policy: frame-ancestors 'none'` and a strict CSP (`connect-src` only the agent loopback origin and `'self'`), `Cache-Control: no-store`, and antiforgery on the POST | transport [TL Decision 2026-11-02, OQ-8]; page flow and (a)-(c) [TL Decision 2026-11-02, OQ-34] |
| 5.9 | Attestation format: see 6.5 and `device-attestation.schema.json` | [TL Decision 2026-11-02, OQ-9] |
| 5.10 | A failure of rules 1-3, 5 or 6 (re-prompt) writes no `auth.app_access.denied`; the following sign-in writes its normal events. Rule 4 does write one (5.7). **5.11** `notfromhub`: "refuse" means the hand-off is refused (no session reuse); the user sees the sign-in page (5.3) plus `auth.app_access.denied reason=notfromhub` | rule 4 [SRC: LD L1182]; others [TL Decision 2026-11-02, OQ-10] (Accept); 5.11 [SRC: LD L1178, L1182]; [TL Decision 2026-11-02, OQ-41] (Accept; SA aligns L1403) |

## 6. Plain sign-in check (best effort) and the agent attestation (all uses)

| # | Item | Label |
|---|---|---|
| 6.1 | `/l/{slug}` creates a per-transaction nonce; on a cage PC managed Edge gets an attestation; `remote_session=true` → `auth.login.failed reason=remote_session`, screen 8 (Session) variant A "remote session"; **no attestation → sign-in goes ahead** | [SRC: LD §16.18.5 L1376-1379] |
| 6.2 | Attestation present but invalid at plain sign-in: sign-in **goes ahead** (best effort). (a) An unverified attestation is never trusted: a bad-signature JWS saying `remote_session=true` does **not** trigger screen 8 variant A; only a verified one does. (b) `auth.login.succeeded` carries optional detail `attestation` = `valid` \| `missing` \| `invalid` (TL Rec to the SA for LD §8 L416; optional in the schema meanwhile). (c) The session is never marked "attested"; the hand-off and every cage action need their own fresh attestation (5.8). Hand-off and cage actions still fail closed | [SRC: LD L1376-1379, L1385-1388]; [TL Decision 2026-11-02, OQ-11] |
| 6.3 | Lockout: 10 wrong passwords → `auth.account.locked`, refused for 30 min | [SRC: PLAN L34, S1 check 10] |
| 6.4 | **Nonce:** created by the login service for each sign-in (`/l/{slug}`) or hand-off transaction; stored server-side, single use, valid ≤60 s, bound to that transaction | [TL Decision 2026-11-02, OQ-8] |
| 6.5 | **Agent call:** the page calls the local device agent on loopback (allowed by managed Edge policy). The agent returns a compact JWS: `alg=ES256`, `typ=cachd-attestation+jwt`, TPM key, `kid` = enrolled device id; claims `nonce`, `device_id`, `remote_session`, `iat`, `exp`, **`aud`** (the login service issuer for `signin` / `handoff`; the Cash Advance server for `cage_action`) and **`purpose`** = `signin` \| `handoff` \| `cage_action`. No `jti` (single use comes from the nonce). Schema: `device-attestation.schema.json` | [TL Decision 2026-11-02, OQ-8/OQ-9; SRC: LD §16.18.5 L1393-1398]; `typ`, no `jti` [TL Decision 2026-11-02, OQ-35]; `aud`, `purpose` [TL Decision 2026-11-02, Addendum 12:15 PM PT (overrides OQ-35 "no aud")] |
| 6.6 | **Transport:** the page sends the JWS as `device_attestation` in a **POST body only** (a form field on the sign-in POST, or on the authorize POST from the interstitial, 5.8); never in a URL | [TL Decision 2026-11-02, OQ-8]; POST only [TL Decision 2026-11-02, OQ-34] |
| 6.7 | **Server check**, in this order: `alg` must be exactly `ES256`, pinned **before** parsing/key lookup (`none`, HS*, RS* refused); any `crit` header refused; `jwk`/`jku`/`x5c`/`x5u` refused; signature against the enrolled device key registry (`kid` → key); `device_id` = `kid`; `aud` and `purpose` match this verifier (login service: issuer + `signin` or `handoff`; Cash Advance server: its own id + `cage_action`), else refused; `exp` not passed and `iat` not in the future, each with **±30 s skew** (G1); `exp - iat` ≤60 s; `nonce` matches an unconsumed nonce of this transaction and is consumed **atomically (compare-and-delete) before the result is returned**, so two parallel submits can't both pass (the nonce itself, a server-stored handle, gets no skew). Any failure = `attestation_invalid`. Fails closed for hand-off and cage actions. Device id format: **OPEN (Device Agent / hardware owner) → OQ-35**; TL Rec UUID string; safe default: the schema accepts any 1-128 char string and the registry lookup is exact-match only | [TL Decision 2026-11-02, OQ-8/OQ-9]; order, skew, ES256 pin, crit, atomic consume [TL Decision 2026-11-02, OQ-35]; aud/purpose [TL Decision 2026-11-02, Addendum] |
| 6.8 | **Stand-in:** fakes the agent per PC id with the modes valid, missing, bad signature, expired, replayed, unknown device, remote session; fake device ids and keys start `standin-`; keys are in-memory ES256; it emits `aud` and `purpose` per 6.5. The PC id is chosen **only through the control listener** (`/_standin/*` on 127.0.0.1, 1.5), which sets a cookie named `standin_pc`; the front listener never sets it and the real service never reads it. The stand-in refuses to start outside `Development`. `tools/check-no-standin.sh` (SSE2) also greps for `standin_pc` | modes [SRC: PLAN §3.2 L199; TL Decision 2026-11-02]; mechanism [TL Decision 2026-11-02, OQ-33]; aud/purpose [TL Decision 2026-11-02, Addendum] |

## 7. Token: `POST /connect/token` (back, 8084)

| # | Item | Label |
|---|---|---|
| 7.1 | Grants `authorization_code` (with `code_verifier`) and `refresh_token`; client auth `client_secret_basic` or `private_key_jwt` | [SRC: PLAN L106, L109; LD L211] |
| 7.2 | Rolling refresh; reuse after ≤30 s leeway → `invalid_grant`, chain revoked, `auth.refresh.reuse_detected` | [SRC: PLAN L109, S1 check 8; LD §5 L228] |
| 7.3 | Refresh re-checks user status and role. User **disabled or locked** → `invalid_grant`, the refresh-token family is **revoked** (not just refused) and `auth.session.revoked reason=user_disabled` is written. **Role loss** in the client's `app_code` → `invalid_grant`, and only that client's family is revoked; other clients' grants are not | re-check [SRC: PLAN L109; LD L232]; [TL Decision 2026-11-02, OQ-12] |
| 7.4 | Absolute session cap: default **12 h**. The login session and its refresh-token family end at sign-in time + cap, never later; refresh lifetime = the cap. Setting `LoginService:Session:AbsoluteLifetime` (TimeSpan, `12:00:00`), in deployment config only, no admin screen. **Technical check, not policy:** the cap must be greater than the 15-minute idle timeout, or the service does not start. **OPEN (Mohanraj):** (a) who can change the cap; (b) the allowed range (shortest and longest). No values proposed; asked Nov 2, 12:01 PM PT (OQ-40) | rule [SRC: LD L228, L231]; 12 h default, configurable [Mohanraj Decision 2026-11-02 (t43u), OQ-13]; 15-min idle [SRC: LD §5 L231 (BFF), §16 L946 (login-service cookie), §6 L323 (PCI 8.2.8)]; deployment config only [POC assumption → OQ-40]; who / range [OPEN (Mohanraj) → OQ-40] |
| 7.5 | Response: `access_token`, `token_type=Bearer`, `expires_in=300`, `id_token` (code grant), `refresh_token` (when granted), `scope`; headers `Cache-Control: no-store` and `Pragma: no-cache` (RFC 6749 §5.1). **7.6** `private_key_jwt`: RS256 only; assertion `iss` = `sub` = `client_id`; `aud` = the issuer identifier exactly; `jti` required and rejected on reuse until `exp`; `exp - iat` ≤60 s; skew per G1; `alg=none`/HS* refused. `client_secret_basic`: secret ≥256 bits random, stored hashed. Revoke and introspect accept the same client-auth methods as token. **7.7** Token-endpoint `temporarily_unavailable`: HTTP 503, `{"error":"temporarily_unavailable"}`, `Retry-After: <integer seconds>`, `Cache-Control: no-store`, no description, no partial grant (nothing rotated or issued); the presented refresh token is **not** consumed | 7.5 [TL Decision 2026-11-02, OQ-4]; 7.6 [TL Decision 2026-11-02, OQ-21]; 7.7 [TL Decision 2026-11-02, OQ-19] |

## 8. Tokens and claims (schema: `claims.schema.json`)

| # | Item | Label |
|---|---|---|
| 8.1 | Access token: JWT RS256, 5 min, `aud` = that client's API. Claims `iss`, `sub`, `aud`, `exp`, `iat`, `jti`, `scope`, `client_id`, `login_provider` (`database`\|`entra`), `amr`, `auth_time` | [SRC: PLAN L116-118; LD §5 L227-229] |
| 8.2 | `client_id` / `azp` = the OAuth client; never used for tenant scoping | [SRC: LD §16.5 L906] |
| 8.3 | `perm_version`: Cachd clients only | [SRC: PLAN L119; LD L907] |
| 8.4 | `partner_id`, `cachd_client_id` (absent for partner-level users), `location_id`: Cachd always; other clients only when `org_claims` is on (default off) | [SRC: PLAN L120; LD L907] |
| 8.5 | No roles or permissions in any token | [SRC: PLAN L121; LD L908] |
| 8.6 | ID token: `iss`, `sub`, `aud`, `azp`, `exp`, `iat`, `nonce`, `auth_time`, `amr`. No `sid`, no userinfo | [SRC: PLAN L122-123] |
| 8.7 | Stand-in tokens carry `x_standin: true`; outside Development the shared JwtBearer rejects `x_standin`, a `/standin` issuer or a `standin-` kid. The audit relay or ingest outside Development rejects and alerts on any event with `x_standin` | [SRC: PLAN §3.5; S1 check 11]; relay guard [TL Decision 2026-11-02, OQ-25] |
| 8.8 | API rejects wrong `aud`, bad signature, expired token → 401 | [SRC: PLAN S1 check 8] |
| 8.9 | Header `typ=at+jwt` (RFC 9068) on access tokens; `JWT` on ID tokens; `at_hash` allowed, not required. **MUST:** the shared JwtBearer sets `ValidTypes = ["at+jwt"]`, so an ID token sent as a bearer gets 401 (contract test) | [TL Decision 2026-11-02, OQ-14] |
| 8.10 | `sub` and org ids are UUID strings; `perm_version` integer; ID-token lifetime 5 min; access-token `aud` is a **single string** (no array). Skew ±30 s per G1. `location_id` is required for location sign-ins (`/l/{slug}`); **OPEN (SA) → OQ-15:** whether `/admin` sessions carry a location; safe default: v1 makes no rule for `/admin` tokens (the `/admin` shape is not frozen). `amr`: DB sign-in with TOTP = `["pwd","otp","mfa"]`; Entra: copy only RFC 8176 values, set `mfa` only if Entra's `amr` contains exactly `mfa`; `wiaormfa` never counts. **OPEN (SA) → OQ-22:** (a) recovery-code sign-in as MFA, (b) `ngcmfa` as MFA; safe default: neither counts, a recovery-code sign-in is not issued `mfa` and is not hand-off-eligible | [TL Decision 2026-11-02, OQ-15]; [TL Decision 2026-11-02, OQ-22] |

## 9. Revoke: `POST /connect/revoke` (back, 8084)

| # | Item | Label |
|---|---|---|
| 9.1 | RFC 7009: `token`, optional `token_type_hint`; client auth required; 200 for unknown or already revoked tokens | [SRC: PLAN L110 (RFC 7009)] |
| 9.2 | Revoking a refresh token revokes its whole family | [TL Decision 2026-11-02, OQ-12] (Accept) |

## 10. Introspect: `POST /connect/introspect` (back, 8084)

| # | Item | Label |
|---|---|---|
| 10.1 | Internal only; optional for clients; RFC 7662 response (`active`, and when active `sub`, `client_id`, `exp`, `iat`, `scope`, `token_type`). An inactive, unknown or other-client token gets exactly `{"active":false}` with no other member | internal/optional [SRC: PLAN L111]; shape [TL Decision 2026-11-02, OQ-4] |

## 11. End session: `GET|POST /connect/endsession` (front)

| # | Item | Label |
|---|---|---|
| 11.1 | Revokes every authorization and token from **that login session** across all clients (not by `sub`), then redirects to the location URL (Entra end-session first at Entra locations) | [SRC: PLAN L112; LD §16.5 L915] |
| 11.2 | OIDC RP-Initiated Logout 1.0: `id_token_hint`, `post_logout_redirect_uri`, `state`, `client_id`. (a) `id_token_hint` is validated for signature, `iss` and `aud`, **ignoring `exp`**. (b) `post_logout_redirect_uri` must exactly match one registered for that client, else it is ignored. (c) The end state is always the login session's `/l/{slug}`; no external redirects. (d) Without a valid `id_token_hint`, a confirm page ("Sign out of all apps?") comes before revoking (logout CSRF). (e) One location per session in Phase 1 (SA-ANSWERS 11) | [SRC: LD §7.2 item 0 L395, L917]; [TL Decision 2026-11-02, OQ-16] |
| 11.3 | Writes one `auth.session.revoked` (`reason=sign_out`) per login session ended, not one per client or token | [SRC: PLAN L155]; per-session [TL Decision 2026-11-02, OQ-10] (Accept) |

## 12. Audit events: `audit-events.schema.json`

Events listed in PLAN L148-161 and LD §8 L416-426, envelope per AUD §7A.4. Details in the schema.

## 13. Versioning

[SRC: PLAN L163] Freeze after SSE2's adversarial pass and TL review; after that only `v1.1` edits with the
reason and a contract-test change in the same change set. Every former `ASSUME` is now `[SRC]`, `[TL Decision]` or an explicit `OPEN → OQ-n` with its safe default.
