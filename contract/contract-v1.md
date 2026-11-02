# Login service contract v1 (DRAFT v1-d2)

Labels and source snapshots: see `README.md`. PLAN = build plan md5 ae18d664; LD = login doc md5 5c68265c (rev 11:32).
"MUST" items are what contract tests may assert against both the stand-in and the real service.

## 1. Issuer, listeners, configuration

| # | Item | Label |
|---|---|---|
| 1.1 | Real issuer is `https://login.<domain>/`, set explicitly (never derived from the request host), so tokens issued on 8084 still carry the public `iss` | [SRC: LD §4.5 L210] |
| 1.2 | Stand-in issuer path always contains `/standin`, e.g. `http://localhost:5080/standin` | [SRC: PLAN §3.5 table; LD §16.17.5 L1315] |
| 1.3 | Front listener: browser-facing (authorize, endsession, `/l/{slug}`, `/apps`, `/admin`). Stand-in example port 5080 | [SRC: PLAN §3.1 L104-113, §3.3] |
| 1.4 | Back listener **8084**: maps only `/connect/token`, `/connect/revoke`, `/connect/introspect` and discovery/JWKS; everything else 404. On the front listener those back-channel paths return 404 | [SRC: LD §4.5 L206-208] |
| 1.5 | Stand-in control API `/_standin/*` only on a separate 127.0.0.1 listener; never part of the client contract; tests using it are tagged `standin-only` | [SRC: PLAN §3.2, §3.3] |
| 1.6 | Clients read only `Auth:Authority`, `Auth:ClientId`, secret or key, and the back-channel base URL from config; they never hard-code a path, port, `kid` or stand-in claim | [SRC: PLAN §3.4] |
| 1.7 | Allowlist runs first on every front request; a blocked caller gets the POC's minimal 403 (errors.md §4), not an OAuth error | [SRC: PLAN L108, L146; ALW L30-50] |

## 2. Discovery: `GET /.well-known/openid-configuration`

| # | Item | Label |
|---|---|---|
| 2.1 | Fields: `issuer`, `authorization_endpoint`, `token_endpoint`, `revocation_endpoint`, `end_session_endpoint`, `jwks_uri`, `response_types_supported=["code"]`, `grant_types_supported=["authorization_code","refresh_token"]`, `code_challenge_methods_supported=["S256"]`, `token_endpoint_auth_methods_supported=["client_secret_basic","private_key_jwt"]`, `id_token_signing_alg_values_supported=["RS256"]`. Schema: `openid-configuration.schema.json` | [SRC: PLAN §3.1 L106] |
| 2.2 | **Which listener.** PLAN L106-107 and LD §16.17.2 L1297 say "front"; LD §4.5 L203/L206 (TL decision rev 12:22) says discovery/JWKS are internal-only on 8084. **Draft follows LD §4.5: served on 8084; front returns 404.** | [CONFLICT → OQ-1] |
| 2.3 | URLs in the document: `authorization_endpoint` and `end_session_endpoint` are front URLs. `token_endpoint`, `revocation_endpoint`, `jwks_uri` are written as the internal 8084 URLs; clients replace nothing | [ASSUME A-1 → OQ-3] |
| 2.4 | `subject_types_supported=["public"]` (required by OIDC Discovery 1.0, not in PLAN) | [ASSUME A-2 → OQ-4] |
| 2.5 | `introspection_endpoint` is **not** published (PLAN lists the endpoint but not the field; it is "optional for clients") | [ASSUME A-3 → OQ-4] |
| 2.6 | `scopes_supported`, `claims_supported`, `prompt_values_supported` not published in v1 | [ASSUME A-4 → OQ-4] |
| 2.7 | No `userinfo_endpoint`; no `frontchannel_logout_*` / `backchannel_logout_*` | [SRC: PLAN L123, §1.3 L60] |

## 3. JWKS: `GET /.well-known/jwks`

| # | Item | Label |
|---|---|---|
| 3.1 | Public keys only, each with `kid`; `kty=RSA`, `use=sig`, `alg=RS256` | [SRC: PLAN L107; alg from L106] / `use` [ASSUME A-5 → OQ-4] |
| 3.2 | Rotation: a new key is published ≥24 h before it signs; real rotation every 90 days with overlap. Stand-in fault switch may shorten this | [SRC: LD §5 L231; PLAN L107] |
| 3.3 | Stand-in keys: in memory, new at each start, `kid` starts `standin-`. A real JWKS never has a `standin-` kid | [SRC: PLAN §3.5; LD §16.17.5 L1316] |

## 4. Authorize: `GET /connect/authorize` (front)

| # | Item | Label |
|---|---|---|
| 4.1 | `response_type=code` only; PKCE required with `code_challenge_method=S256` only; `state`, `nonce`, `prompt`, `max_age` supported | [SRC: PLAN L108] |
| 4.2 | Check order: allowlist → authenticated? → `prompt` / `max_age` / hand-off policy → role in the client's `app_code` → issue code | [SRC: PLAN L108; LD §16.5 L896] |
| 4.3 | No role in the client's `app_code` → redirect with `error=access_denied`, no code, audit `auth.app_access.denied reason=no_role` | [SRC: PLAN L143, S1 check 1 L238; LD §16.5 L896] |
| 4.4 | Code: lifetime ≤60 s, single use; a second redemption → `invalid_grant` | [SRC: PLAN L108, S1 check 8] |
| 4.5 | `auth.app.signed_in` is written in the same transaction that issues the code; if the write fails, no code: `error=temporarily_unavailable` | [SRC: LD §16.5 L900; PLAN L145, S1 check 9] |
| 4.6 | `prompt=none` with no usable session → `login_required` / `interaction_required` | [SRC: PLAN L144] |
| 4.7 | SSO-eligible clients send `max_age=900`; an older `auth_time` forces re-sign-in. Login cookie: 15-min sliding idle | [SRC: LD §12 row 58 L641; PLAN S1 check 6] |
| 4.8 | `redirect_uri` must exactly match a registered value; otherwise an error page on our host, no redirect | [ASSUME A-6 → OQ-5] (RFC 6749 §4.1.2.1 behaviour) |
| 4.9 | Scope values: `openid` plus one API scope per client API; mixed-audience scopes → `invalid_scope`; `offline_access` needed for a refresh token | names [ASSUME A-7 → OQ-6]; `invalid_scope` [SRC: PLAN L141] |

## 5. Hand-off (Cash Advance client only)

| # | Item | Label |
|---|---|---|
| 5.1 | The Cash Advance authorize request drops `prompt=login`. The existing login session is accepted only if rules 1-6 all hold: (1) interactive and `auth_time` ≤5 min, proven by this sign-in's upstream token; (2) `mfa` in `amr`; (3) one-shot (`cash_advance_handoff_used`); (4) from the hub with a hub-issued value; (5) allowlist and location checks as normal; (6) the session then becomes `cage_consumed` (not SSO-eligible for anything) | [SRC: LD §16.16.1 L1165-1170; PLAN L126-132] |
| 5.2 | **Plus a valid agent attestation:** TPM-signed by the enrolled device, ≤60 s, single use, bound to the transaction nonce and device id, `remote_session=false` | [SRC: PLAN §1.2 L51, L133; LD §16.16.1 L1172] |
| 5.3 | Any rule 1-6 failure → the sign-in page again (re-prompt, not an OAuth error) | [SRC: PLAN L134; LD L1299] |
| 5.4 | Attestation says `remote_session=true` → `auth.app_access.denied reason=remote_session`, screen 8 (remote-session variant); fallback sign-in on that PC is refused too until it ends (`auth.login.failed reason=remote_session`); if the user cancels → `error=access_denied` to the client | [SRC: LD L1177, L1372; PLAN L134, S1 checks 4, 4b] |
| 5.5 | Attestation missing / bad signature / expired / replayed / unknown device → `auth.app_access.denied reason=attestation_invalid` → normal password + MFA sign-in; Cash Advance then opens but cage actions stay blocked until the agent attests again (screen 8 variant "This PC's security software isn't responding. Contact IT.") | [SRC: PLAN §1.2, S1 checks 4a, 4b; LD L1178, L1373] |
| 5.6 | Successful hand-off → `auth.app.signed_in method=hub_handoff` with the original `auth_time` and login session id | [SRC: PLAN S1 check 2; LD L1169] |
| 5.7 | **How the hub-issued value reaches authorize.** OAuth `state` is created by the client (BFF), not the hub, so "a `state` the hub issued" can't work as written. Draft: the hub tile links to the client with `hub_launch=<opaque, single use, ≤5 min, bound to login session + client>`; the BFF passes it unchanged as authorize parameter `hub_launch` | [ASSUME A-8 → OQ-7] |
| 5.8 | **How the attestation reaches the login service on hand-off.** Not stated (LD §16.18.5 covers only the sign-in page). Draft: the login service shows a short interstitial on its own host that creates the nonce; managed Edge fetches the attestation from the agent; the page posts it to the login service. Not a client parameter: clients change nothing | [ASSUME A-9 → OQ-8] |
| 5.9 | Attestation wire format (JWS? fields `nonce`, `device_id`, `iat`, `exp`, `jti`, `remote_session`), and how the stand-in learns the fake PC id | [ASSUME A-10 → OQ-9] |
| 5.10 | Is a rule 1-6 failure (re-prompt) audited, and how? | [OQ-10] |

## 6. Plain sign-in check (best effort)

| # | Item | Label |
|---|---|---|
| 6.1 | `/l/{slug}` creates a per-transaction nonce; on a cage PC managed Edge gets an attestation; `remote_session=true` → `auth.login.failed reason=remote_session`, screen 8; **no attestation → sign-in goes ahead** | [SRC: LD §16.18.5 L1362-1365] |
| 6.2 | Attestation present but invalid at plain sign-in: go ahead or refuse? | [OQ-11] |
| 6.3 | Lockout: 10 wrong passwords → `auth.account.locked`, refused for 30 min | [SRC: PLAN L34, S1 check 10] |

## 7. Token: `POST /connect/token` (back, 8084)

| # | Item | Label |
|---|---|---|
| 7.1 | Grants `authorization_code` (with `code_verifier`) and `refresh_token`; client auth `client_secret_basic` or `private_key_jwt` | [SRC: PLAN L106, L109; LD L208] |
| 7.2 | Rolling refresh; reuse after ≤30 s leeway → `invalid_grant`, chain revoked, `auth.refresh.reuse_detected` | [SRC: PLAN L109, S1 check 8; LD §5 L225] |
| 7.3 | Refresh re-checks user status and role; no role → `invalid_grant` | re-check [SRC: PLAN L109]; error code [ASSUME A-11 → OQ-12] |
| 7.4 | Refresh lifetime = absolute session cap (LS-OQ-13, open). Stand-in default 12 h | [SRC: LD L225]; value [ASSUME A-12 → OQ-13] |
| 7.5 | Response: `access_token`, `token_type=Bearer`, `expires_in=300`, `id_token` (code grant), `refresh_token` (when granted), `scope` | [ASSUME A-13 → OQ-4] (RFC 6749 §5.1 shape) |

## 8. Tokens and claims (schema: `claims.schema.json`)

| # | Item | Label |
|---|---|---|
| 8.1 | Access token: JWT RS256, 5 min, `aud` = that client's API. Claims `iss`, `sub`, `aud`, `exp`, `iat`, `jti`, `scope`, `client_id`, `login_provider` (`database`\|`entra`), `amr`, `auth_time` | [SRC: PLAN L116-118; LD §5 L224-226] |
| 8.2 | `client_id` / `azp` = the OAuth client; never used for tenant scoping | [SRC: LD §16.5 L892] |
| 8.3 | `perm_version`: Cachd clients only | [SRC: PLAN L119; LD L893] |
| 8.4 | `partner_id`, `cachd_client_id` (absent for partner-level users), `location_id`: Cachd always; other clients only when `org_claims` is on (default off) | [SRC: PLAN L120; LD L893] |
| 8.5 | No roles or permissions in any token | [SRC: PLAN L121; LD L894] |
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
| 11.1 | Revokes every authorization and token from **that login session** across all clients (not by `sub`), then redirects to the location URL (Entra end-session first at Entra locations) | [SRC: PLAN L112; LD §16.5 L901] |
| 11.2 | Parameters `id_token_hint`, `post_logout_redirect_uri`, `state` (OIDC RP-Initiated Logout 1.0); when the user has several locations the redirect uses the login session's location | [ASSUME A-18 → OQ-16] |
| 11.3 | Writes `auth.session.revoked` | [SRC: PLAN L155]; per-endsession [ASSUME A-19 → OQ-10] |

## 12. Audit events: `audit-events.schema.json`

Events listed in PLAN L148-161 and LD §8 L413-423, envelope per AUD §7A.4. Details in the schema.

## 13. Versioning

[SRC: PLAN L163] Freeze after SSE2's adversarial pass and TL review; after that only `v1.1` edits with the
reason and a contract-test change in the same change set. Every `ASSUME` above must be resolved first.
