# Errors toward clients: contract v1 (DRAFT v1-d1)

Labels and source snapshots: see `README.md`.

## 1. Rule

- Toward OAuth clients: **standard OAuth 2.0 / OIDC error codes only**. [SRC: PLAN §3.1 L137; LD §16.17.2 L1296]
- Internal reason codes (`locked`, `mfa_missing`, `tenant_mismatch`, `stale_auth_time`, `remote_session`,
  `no_role`, `attestation_invalid`, …) go **only** to audit and to our own pages, never to clients.
  [SRC: PLAN L146; LD §8 L420]
- `error_description` is never sent, so no internal reason can leak through it. [ASSUME A-20 → OQ-17]
- `error_uri` is never sent. [ASSUME A-20 → OQ-17]

## 2. Codes

| Code | When (v1) | Endpoint and transport | Label |
|---|---|---|---|
| `invalid_request` | Missing / duplicated / malformed parameter; no PKCE; `code_challenge_method` ≠ `S256`; `response_type` ≠ `code` | authorize: redirect; token/revoke/introspect: HTTP 400 JSON | code [SRC: PLAN L138]; PKCE cases [SRC: PLAN L108]; transport [ASSUME A-21 → OQ-17] |
| `invalid_client` | Client authentication failed at token / revoke / introspect | HTTP 401 JSON, with `WWW-Authenticate: Basic` for `client_secret_basic` | code [SRC: PLAN L139]; transport [ASSUME A-21] (RFC 6749 §5.2) |
| `invalid_grant` | Code replay, expired code (>60 s), `code_verifier` mismatch, `redirect_uri` mismatch, refresh reuse after leeway, revoked chain | token: HTTP 400 JSON | [SRC: PLAN L140, S1 check 8]; verifier / redirect cases [ASSUME A-21] |
| `invalid_grant` | Refresh after the user lost the role in the client's `app_code`, or the user is disabled / locked | token: HTTP 400 JSON | [ASSUME A-11 → OQ-12] |
| `invalid_scope` | Scopes for more than one audience in one request; unknown scope | authorize: redirect | [SRC: PLAN L141]; unknown-scope case [ASSUME A-7 → OQ-6] |
| `unauthorized_client` | Client not allowed this grant type (e.g. refresh for a client without it) | authorize: redirect; token: HTTP 400 JSON | [SRC: PLAN L142]; cases [ASSUME A-21] |
| `access_denied` | No role in the client's `app_code` (audit `reason=no_role`); user cancels on the remote-session block (screen 8) | authorize: redirect | [SRC: PLAN L143, L134; S1 checks 1, 4] |
| `login_required` | `prompt=none` and no login session | authorize: redirect | [SRC: PLAN L144] |
| `interaction_required` | `prompt=none` and a session exists but interaction is needed (`max_age` exceeded, hand-off rules not met, `cage_consumed`) | authorize: redirect | code [SRC: PLAN L144]; cases [ASSUME A-22 → OQ-18] |
| `temporarily_unavailable` | Outbox write failed (fail closed: no session, no code); issuer dependency down | authorize: redirect. Token endpoint: HTTP 503 JSON `{"error":"temporarily_unavailable"}` (not an RFC 6749 §5.2 token code) | code and cases [SRC: PLAN L145, S1 check 9; LD §8 L423]; token transport [ASSUME A-23 → OQ-19] |

Redirect errors carry `error` and the client's `state` (and `iss`, RFC 9207). `iss` [ASSUME A-24 → OQ-17].

## 3. Not OAuth errors (our own pages)

| Situation | What happens | Label |
|---|---|---|
| Hand-off rule 1-6 fails | Sign-in page again (re-prompt) | [SRC: PLAN L134; LD L1295] |
| Hand-off refused, `attestation_invalid` | Password + MFA sign-in; cage actions blocked; screen 8 variant B | [SRC: LD L1174; PLAN S1 4a, 4b] |
| Hand-off refused, `remote_session` | Screen 8 (remote-session variant); `access_denied` to the client only if the user cancels | [SRC: LD L1173; PLAN L134] |
| Plain sign-in, agent reports remote session | Screen 8 "Try again after the remote session ends" | [SRC: LD §16.18.5 L1359] |
| Wrong password, locked account, MFA failure | Our sign-in page messages (UX screens), no redirect to the client | [SRC: PLAN L146 ("our own pages")]; wording owned by UX |
| `redirect_uri` or `client_id` invalid | Error page on our host; never redirect | [ASSUME A-6 → OQ-5] |
| API (resource server) token problems: wrong `aud`, bad signature, expired, stand-in token in Production | HTTP 401 with `WWW-Authenticate: Bearer error="invalid_token"` | 401 [SRC: PLAN S1 checks 8, 11]; header [ASSUME A-25 → OQ-17] (RFC 6750 §3.1) |

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
`Server: server`, plus `Date` and `X-Request-ID`. Allowlist 403s are not on the outbox (LD §8 L425).
On 8084 the check uses the internal source list (LD §4.5 L205); what an unlisted caller gets there
(same 403, or no connection) [ASSUME A-26 → OQ-20].
