# Open questions on contract v1 (DRAFT v1-d3)

For the Tech Lead to answer or relay. "Who" = who I think owns the answer; the TL decides routing.
Every [ASSUME A-n], [SSE Proposal → OQ-n] and [DOC SILENT → OQ-n] in the contract points here.
Status (v1-d3, 8:10 PM PT): TL rulings `reviews/contract-v1-tl-rulings.md` md5 `9012597c8e0b145988d95a2e034d20e0` applied. **Resolved** = TL Decision 2026-11-02 (relayed by the parent at 11:37 AM PT); **Open + SSE Proposal** = my proposed answer, not decided.
LD line numbers refer to md5 `88878681` (rev 11:41).

## Resolved

| OQ | Question (short) | Decision | Where applied |
|---|---|---|---|
| OQ-1 | Discovery and JWKS: front or 8084 only? | **Resolved, TL Decision 2026-11-02:** front listener (public, read-only, cacheable); token, revoke, introspect and other back-channel calls stay on 8084. Follow-ups OQ-30..32 | contract §1.3-1.4, §2.2, §2.8-2.10 |
| OQ-2 | Which source revisions? | **Resolved, TL Decision 2026-11-02:** always the newest; snapshot each with its md5 in the file name. Used LD `88878681` (TL expected `5c68265c`; the SA revised again at 11:41), PLAN `ae18d664` | README sources |
| OQ-7 | How does the hub's proof reach authorize (rule 4)? | **Resolved, TL Decision 2026-11-02:** `hub_launch`, issued by the hub / login service; single use, ≤60 s, server-stored, bound to login session id + target `client_id`; client passes it to authorize; checked and consumed; missing / invalid → `notfromhub`. Now also in LD §16.18.5 L1388-1391. Follow-ups OQ-36, OQ-41 | contract §5.7, errors |
| OQ-8 | How does the attestation reach the login service; who creates the nonce? | **Resolved, TL Decision 2026-11-02:** login service creates the nonce (server-stored, single use, ≤60 s, bound to the transaction); page calls the agent on loopback (managed Edge policy); page sends `device_attestation` (form or authorize parameter); server verifies against the enrolled device key registry; fails closed. LD §16.18.5 L1381-1386. Follow-up OQ-34 | contract §5.8, §6.4-6.7 |
| OQ-9 | Attestation wire format; stand-in PC id | **Resolved, TL Decision 2026-11-02:** compact JWS, ES256, TPM key, `kid` = enrolled device id; claims `nonce`, `device_id`, `remote_session`, `iat`, `exp`. Stand-in fakes the modes per PC id. Follow-ups OQ-33, OQ-35 | `device-attestation.schema.json`, §6.8 |
| OQ-23 | Audit envelope naming | **Partly resolved, TL Decision 2026-11-02:** the Cachd client field is `cachd_client_id` (as B-LS5). **Still open with the SA:** flat `partner_id` / `location_id` (AUD §7A.4) vs `scope.*` (LD §8 L421); `category` literal; `outcome` values. SSE Proposal: flat names, since the envelope is shared by every service; `category=event`; `success` / `failure` / `denied`. Follow-up OQ-37 | audit schema |
| OQ-26 | Extra LD §8 events in v1? | **Resolved, TL Decision 2026-11-02:** defined in the v1 schema, marked "Defined, not emitted in S1" (password reset, provider change, identity link/unlink, bootstrap, MFA policy exclusion). Follow-up OQ-38 | audit schema |
| OQ-27 | Who creates `code/README.md`? | **Resolved, TL Decision 2026-11-02:** SSE2 creates `code/README.md` and `docs/BUILD-LOG.md`; my log stays in `contract/SSE-BUILD-LOG-PENDING.md` | README |
| OQ-13 | Absolute session cap value (LS-OQ-13) | **Resolved, Mohanraj Decision 2026-11-02 (t43u, 12:00 PM PT, verbatim: "In GitHub and 12 hr is fine but if we can have it configurable then it will be better"):** default 12 h, configurable. Setting name, bounds and validation → OQ-40 | contract §7.4 |
| OQ-28 | Where the code lives (PQ-201) | **Resolved, Mohanraj Decision 2026-11-02 (t43u, same quote):** in GitHub. Org, repo name, visibility and setup → OQ-39. Until the TL says otherwise: no remote, no push, no GitHub actions; local git only | - |
| OQ-29 | Screen 8 not built yet | **Resolved, TL Decision 2026-11-02:** refer to it by name, "screen 8 (Session)", with variant A "remote session" and variant B "security software not responding"; UX is building it (`login-hub-mockups/08-session.html`, 11:38) | contract §5.4-5.5, §6.1, errors |
| OQ-3 | Discovery endpoint URLs | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept:** public URLs; clients take token/revoke from config, never discovery; test: BFF stub never calls front `/connect/token` | §1.6, §2.3 |
| OQ-4 | Exact discovery/JWKS field set | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** stand-in exact set (2.1 + 4 fields, exact-key test); real service may add standard fields; token response `no-store` + `Pragma: no-cache`; inactive introspection exactly `{"active":false}` | §2.4-2.6, §3.1, §7.5, §10.1, discovery schema |
| OQ-5 | Bad `redirect_uri` / `client_id` | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** error page, never redirect; exact match; generic text, no echo, `no-store`, `frame-ancestors 'none'`; no audit | §4.8, errors §3 |
| OQ-6 | Scopes, `aud`, client ids, `offline_access` | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept:** as proposed, plus unknown scope / `offline_access` from non-refresh client → `invalid_scope` | §4.9, errors §2, claims schema |
| OQ-10 | Re-prompt audit; `auth.session.revoked` | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept** | §5.10, §11.3 |
| OQ-11 | Invalid attestation at plain sign-in | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** goes ahead; unverified never trusted; optional `attestation=valid\|missing\|invalid` on `auth.login.succeeded` (TL Rec to SA); never "attested" | §6.2, audit schema |
| OQ-12 | Refresh after role loss / disable | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** disabled/locked → family revoked + `auth.session.revoked reason=user_disabled`; role loss → `invalid_grant`, that client's family only | §7.3, §9.2, errors §2 |
| OQ-14 | `typ`, `at_hash` | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** `ValidTypes=["at+jwt"]`; ID token as bearer → 401 | §8.9, claims schema |
| OQ-16 | Endsession | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** hint validated ignoring `exp`; exact `post_logout_redirect_uri` else ignored; always `/l/{slug}`; confirm page without valid hint | §11.2 |
| OQ-17 | `error_description`, `iss`, 401 header | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** `iss` on every authz response + discovery flag; strip OpenIddict `error_description`/`error_uri` (test); 401 header only | §4.10, errors §1-3 |
| OQ-18 | `login_required` vs `interaction_required` | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept** | errors §2 |
| OQ-19 | Token `temporarily_unavailable` | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** 503, `Retry-After`, `no-store`, no description, no partial grant, refresh token not consumed | §7.7, errors §2 |
| OQ-20 | 8084 block | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** network layer (no ALB TG, SG BFF+API only) + app layer (same minimal 403) | §1.8, errors §4 |
| OQ-21 | `private_key_jwt`; revocation auth | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** RS256 only; assertion rules; secrets ≥256-bit, hashed | §7.6, discovery schema |
| OQ-25 | Extra event fields; `x_standin` | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept:** plus relay rejects/alerts on `x_standin` outside Development | §8.7, audit schema |
| OQ-30 | Cache headers | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** discovery `max-age=3600`; JWKS `max-age=300`, no stale-*; client refresh 5 min / 1 min, throttled unknown-`kid` refetch | §1.6, §2.8, §3.4 |
| OQ-31 | Discovery/JWKS on 8084 | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept:** same bytes and headers, comparison test | §2.9 |
| OQ-32 | Allowlist on front discovery/JWKS | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept** | §2.10 |
| OQ-33 | Stand-in fake agent | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** PC id only via control listener cookie `standin_pc`; refuses to start outside Development; check-no-standin greps `standin_pc` (SSE2's tool) | §6.8 |
| OQ-34 | Hand-off attestation page | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** POST body only (removed "or an authorize parameter"); server-side transaction binding; `frame-ancestors 'none'`, `no-store`, strict CSP, antiforgery | §5.8, §6.6 |
| OQ-35 | Attestation details | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** skew ±30 s; `alg` exactly ES256 pinned before parsing; `crit` refused; atomic nonce consume. **Overridden in part by the Addendum (12:15 PM PT):** `aud` and `purpose` are now required claims; verifier rejects wrong `aud`/`purpose`. Device id format still OPEN (below) | §6.5-6.7, attestation schema |
| OQ-36 | `hub_launch` details | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** 128-bit CSPRNG base64url, stored hashed; only for tiles with a role; log redaction of `hub_launch`/`code`/`state`; other client/session → `notfromhub`, consumed | §5.7 |
| OQ-41 | `notfromhub` refuse vs sign-in page | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept:** sign-in page + event; SA aligns L1391 | §5.11 |

## Open (each with its safe default in the contract)

| OQ | Question | Safe default / TL Rec | Who | Refs |
|---|---|---|---|---|
| OQ-15 (part) | Do `/admin` sessions carry `location_id`? | `location_id` required for `/l/{slug}` tokens; no rule for `/admin` tokens (not frozen) | SA | §8.10, claims schema |
| OQ-22 (part) | (a) recovery-code sign-in as MFA; (b) `ngcmfa` as MFA | Neither counts; recovery-code sign-in gets no `mfa` and is not hand-off-eligible. Rest of OQ-22 ruled (Change: TOTP `["pwd","otp","mfa"]`; Entra RFC 8176 only, `mfa` only if present; `wiaormfa` never) | SA | §8.10, claims schema |
| OQ-24 | Reason lists and field names | TL Rec used as safe default: `bad_credentials` covers unknown user + wrong password; `username_hmac` lowercase hex over normalized username; `password_expired` only if SA models it as a failure; `rate_limited` not an outbox reason | SA | audit schema |
| OQ-23 (rest) | flat vs `scope.*`; `category`; `outcome` values | flat names; `category=event`; `success`/`failure`/`denied` | SA | audit schema |
| OQ-37 | AUD §7A.4 `client_id` → `cachd_client_id` | TL Rec accept; contract keeps `cachd_client_id` | SA | audit schema |
| OQ-38 | `iam.permission.change_requested` | TL Rec: SA renames to `auth.provider.change_requested`; until then exactly that one `iam.*` name, no pattern | SA | audit schema |
| OQ-35 (part) | Enrolled device id format | TL Rec UUID string; schema accepts any 1-128 char string, exact-match registry lookup | Device Agent / hardware owner | §6.7, attestation schema |
| OQ-39 | GitHub repo details (OQ-28) | No change: local git only, no remote, no push, no GitHub actions | Mohanraj (card t44s0) | - |
| OQ-40 | Session cap: who can change it; allowed range | **No values picked.** 12 h default (Mohanraj t43u); technical check cap > 15-min idle or startup fails; setting in deployment config only [POC assumption → OQ-40] | Mohanraj (question t45s0) | §7.4 |

Note (not an OQ): `08-session.html` shows `local_agent.action_blocked reason=remote_session`. That is a device-agent event (LD L1238, L1408), not a login-service event, so it stays out of this contract.
