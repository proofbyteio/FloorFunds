# Errors toward clients: contract v1 (DRAFT v1-d3)

Labels and source snapshots: see `README.md`.

## 1. Rule

- Toward OAuth clients: **standard OAuth 2.0 / OIDC error codes only**. [SRC: PLAN §3.1 L137; LD §16.17.2 L1318]
- Internal reason codes (`locked`, `mfa_missing`, `tenant_mismatch`, `stale_auth_time`, `remote_session`,
  `no_role`, `attestation_invalid`, `notfromhub`, …) go **only** to audit and to our own pages, never to clients.
  [SRC: PLAN L146; LD §8 L426]
- `error_description` is never sent, so no internal reason can leak through it. OpenIddict sends it by default, so the real service MUST strip `error_description` and `error_uri` in a response handler; a contract test asserts no `error_description` on every endpoint. [TL Decision 2026-11-02, OQ-17]
- `error_uri` is never sent. [TL Decision 2026-11-02, OQ-17]
- G2: nothing secret or diagnostic in a URL or a client-visible error; reason codes in audit only. [TL Decision 2026-11-02, G2]
- JSON error responses from token / revoke / introspect carry `Cache-Control: no-store`. [TL Decision 2026-11-02, OQ-4] (RFC 6749 §5.1)

## 2. Codes

| Code | When (v1) | Endpoint and transport | Label |
|---|---|---|---|
| `invalid_request` | Missing / duplicated / malformed parameter; no PKCE; `code_challenge_method` ≠ `S256` (`response_type` ≠ `code` moved to `unsupported_response_type` [TL Decision 2026-11-02, CR-04]) | authorize: redirect; token/revoke/introspect: HTTP 400 JSON | code [SRC: PLAN L138]; PKCE cases [SRC: PLAN L108]; transport [TL Decision 2026-11-02, OQ-17] (Accept: RFC 6749 §4.1.2.1 / §5.2) |
| `unsupported_response_type` | `response_type` ≠ `code` | authorize: redirect when `redirect_uri` is valid; otherwise the §3 error page | [TL Decision 2026-11-02, CR-04] (RFC 6749 §4.1.2.1) |
| `unsupported_grant_type` | Unknown `grant_type` at the token endpoint | token: HTTP 400 JSON | [TL Decision 2026-11-02, CR-04] (RFC 6749 §5.2) |
| `invalid_client` | Client authentication failed at token / revoke / introspect | HTTP 401 JSON, with `WWW-Authenticate: Basic` for `client_secret_basic` | code [SRC: PLAN L139]; transport [TL Decision 2026-11-02, OQ-17] (RFC 6749 §5.2) |
| `invalid_grant` | Code replay, expired code (>60 s), `code_verifier` mismatch, `redirect_uri` mismatch, refresh reuse after leeway, revoked chain | token: HTTP 400 JSON | [SRC: PLAN L140, S1 check 8]; verifier / redirect cases [TL Decision 2026-11-02, OQ-17] |
| `invalid_grant` | Refresh after the user lost the role in the client's `app_code` (that client's family revoked only), or the user is disabled / locked (family revoked, `auth.session.revoked reason=user_disabled`) | token: HTTP 400 JSON | [TL Decision 2026-11-02, OQ-12] |
| `invalid_scope` | Scopes for more than one audience in one request; unknown scope; `offline_access` from a client not registered for `refresh_token` | authorize: redirect | [SRC: PLAN L141]; unknown scope, `offline_access` [TL Decision 2026-11-02, OQ-6] |
| `unauthorized_client` | Client not allowed this grant type (e.g. refresh for a client without it) | authorize: redirect; token: HTTP 400 JSON | [SRC: PLAN L142]; cases [TL Decision 2026-11-02, OQ-17] |
| `access_denied` | No role in the client's `app_code` (audit `reason=no_role`); user cancels on the remote-session block (screen 8) | authorize: redirect | [SRC: PLAN L143, L134; S1 checks 1, 4] |
| `login_required` | `prompt=none` and no login session | authorize: redirect | [SRC: PLAN L144] |
| `interaction_required` | `prompt=none` and a session exists but interaction is needed (`max_age` exceeded, hand-off rules not met, `cage_consumed`) | authorize: redirect | code [SRC: PLAN L144]; cases [TL Decision 2026-11-02, OQ-18] (Accept; also MFA and location steps) |
| `temporarily_unavailable` | Outbox write failed (fail closed: no session, no code); issuer dependency down | authorize: redirect. Token endpoint: HTTP 503 JSON `{"error":"temporarily_unavailable"}` (not an RFC 6749 §5.2 token code), `Retry-After: <integer seconds>`, `Cache-Control: no-store`, no description, no partial grant; the presented refresh token is not consumed | code and cases [SRC: PLAN L145, S1 check 9; LD §8 L429]; token transport [TL Decision 2026-11-02, OQ-19] |

Redirect errors carry `error` and the client's `state` and `iss` (RFC 9207), on every authorization response, success and error. [TL Decision 2026-11-02, OQ-17]

## 3. Not OAuth errors (our own pages)

| Situation | What happens | Label |
|---|---|---|
| Hand-off rule 1-6 fails (rule 4 also writes `auth.app_access.denied reason=notfromhub`: `hub_launch` missing, expired, used or bound to another session or client, contract 5.7) | Sign-in page again (re-prompt) | [SRC: PLAN L134; LD L1317] |
| Hand-off refused, `attestation_invalid` (`device_attestation` missing or failing any 6.7 check) | Password + MFA sign-in; cage actions blocked; screen 8 (Session), variant B "security software not responding" | [SRC: LD L1196; PLAN S1 4a, 4b] |
| Hand-off refused, `remote_session` | Screen 8 (Session), variant A "remote session"; `access_denied` to the client only if the user cancels | [SRC: LD L1195; PLAN L134] |
| Plain sign-in, **verified** attestation reports remote session | Screen 8 (Session), variant A "remote session": "Try again after the remote session ends" | [SRC: LD §16.18.5 L1382] |
| Plain sign-in, attestation invalid (expired, bad signature, replayed, wrong `aud`/`purpose`, unknown/revoked/other-location device) | Sign-in goes ahead; its `remote_session` is ignored (no screen 8A); audit `auth.login.succeeded attestation=invalid`; the user sees no difference (G2) | [TL Decision 2026-11-02, CR-25, OQ-11] |
| Cage action without a valid fresh `cage_action` attestation (Cash Advance server, contract 6.9) | Action refused, screen 8 (Session) variant B | [TL Decision 2026-11-02, CR-12] |
| `/_standin/*` on the front listener | 404 | [TL Decision 2026-11-02, CR-05] |
| Wrong password, locked account, MFA failure | Our sign-in page messages (UX screens), no redirect to the client | [SRC: PLAN L146 ("our own pages")]; wording owned by UX |
| `redirect_uri` or `client_id` invalid | Error page on our host; never redirect; generic text ("This sign-in link isn't valid"); echoes no parameter; `Cache-Control: no-store`; `frame-ancestors 'none'`; no audit event | [TL Decision 2026-11-02, OQ-5] |
| API (resource server) token problems: wrong `aud`, bad signature, expired, stand-in token in Production | HTTP 401 with only `WWW-Authenticate: Bearer error="invalid_token"`, no description; also an ID token sent as a bearer (`ValidTypes=["at+jwt"]`) and a token 31 s past `exp` (G1) | 401 [SRC: PLAN S1 checks 8, 11]; header [TL Decision 2026-11-02, OQ-17]; ID token [TL Decision 2026-11-02, OQ-14] (RFC 6750 §3.1) |

## 4. Allowlist block (not an OAuth error)

[SRC: ALW L30-50; PLAN L146] A blocked caller on the front listener gets exactly:

```
HTTP/1.1 403 Forbidden
Content-Type: text/plain; charset=utf-8
Content-Length: 29
Cache-Control: no-store
Connection: close

403 Forbidden: access denied
```

Body exactly `403 Forbidden: access denied\n` (empty for HEAD); no HTML, redirect, cookie or app name,
`Server: server`, plus `Date` and `X-Request-ID`. Allowlist 403s are not on the outbox (LD §8 L431).
On 8084 the check uses the internal source list (LD §4.5 L211); what an unlisted caller gets there
two layers [TL Decision 2026-11-02, OQ-20]: (1) network: no ALB target group, SG only from the BFF and API SGs, so any other caller gets no connection; (2) app: a caller past the SG but not on the internal list gets the same minimal 403 as above.
