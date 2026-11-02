# Open questions on contract v1 (DRAFT v1-d1)

For the Tech Lead to answer or relay. "Who" = who I think owns the answer; the TL decides routing.
Every [ASSUME A-n] in the contract points here. Nothing below is decided.

| OQ | Question | Draft for now (assumption) | Who | Refs |
|---|---|---|---|---|
| OQ-1 | Discovery and JWKS: front listener (PLAN L106-107, LD §16.17.2 L1295) or internal-only on 8084 (LD §4.5 L202/L205, TL decision rev 12:22)? | 8084 only; front 404 | TL | contract §2.2 |
| OQ-2 | Source drift: the brief pinned PLAN `8c42fbdd` and LD rev 11:23 `6c489506`. PLAN changed at 11:27:56 (now `ae18d664`, adds 4b); LD `6c489506` no longer exists on the box. OK to use PLAN `ae18d664` + LD `bd70f29a`? | Used them | TL | README |
| OQ-3 | In discovery, are `token_endpoint` / `revocation_endpoint` / `jwks_uri` the internal 8084 URLs, or public URLs that clients override with the back-channel base URL? | Internal URLs (A-1) | TL | §2.3 |
| OQ-4 | Extra discovery fields: `subject_types_supported=["public"]` (A-2); `introspection_endpoint` not published (A-3); no `scopes_supported` etc. (A-4); JWKS `use=sig` (A-5); token response shape (A-13); introspect response shape (A-17). Must the stand-in publish exactly this set? | As stated | TL | §2-3, §7, §10 |
| OQ-5 | Bad `redirect_uri` or `client_id`: error page on our host, never a redirect? | Yes (A-6) | TL | §4.8 |
| OQ-6 | Scope names, `aud` values and client ids for S1 (Cash Advance API, `backoffice_stub`); is `offline_access` needed for refresh? | `openid` + one API scope per API; `offline_access` (A-7) | TL / SA | §4.9 |
| OQ-7 | Hand-off rule 4 says "a `state` the hub issued", but `state` is created by the client (BFF). How does the hub's proof reach authorize? | Hub link carries `hub_launch` (single use, ≤5 min, bound to login session + client); BFF forwards it as an authorize parameter (A-8). This is a client-visible parameter | TL / SA | §5.7 |
| OQ-8 | How does the agent attestation reach the login service during the hand-off (LD §16.18.5 covers only the sign-in page)? Who creates the "transaction nonce" for it? | Login-service interstitial creates the nonce; Edge fetches from the agent; page posts it back. Not a client parameter (A-9) | TL / SA / Device Agent | §5.8 |
| OQ-9 | Attestation wire format (JWS? claims `nonce`, `device_id`, `iat`, `exp`, `jti`, `remote_session`) and how the stand-in identifies the fake PC id (PLAN L199) | Not drafted (A-10) | Device Agent / TL | §5.9 |
| OQ-10 | Audit for a hand-off re-prompt (rules 1-6 fail)? Is `auth.session.revoked` written once per endsession? | Not drafted / yes (A-19) | SA / TL | §5.10, §11.3 |
| OQ-11 | Plain sign-in with an attestation that is present but invalid: go ahead (best effort) or refuse? | Not drafted | TL | §6.2 |
| OQ-12 | Refresh after the role is removed or the user is disabled: `invalid_grant`? Does revoking a refresh token revoke its chain? | Yes / yes (A-11, A-16) | TL | §7.3, §9.2 |
| OQ-13 | Absolute session cap (LS-OQ-13, open with Mohanraj): value for the stand-in? | 12 h, config (A-12) | Mohanraj via PM | §7.4 |
| OQ-14 | Access-token header `typ=at+jwt` (RFC 9068)? `at_hash` allowed in ID tokens? | Yes / yes (A-14, A-29) | TL | §8.9 |
| OQ-15 | Claim types (`sub` and org ids UUID strings? `perm_version` integer?); `aud` string vs array; ID-token lifetime; allowed clock skew; must `location_id` be present for partner-level users? | UUID, integer, 5 min, skew not set; required (A-15) | TL / SA | §8.10 |
| OQ-16 | Endsession parameters and the redirect target when the user has several locations | RP-Initiated Logout params; login-session location (A-18) | TL | §11.2 |
| OQ-17 | Never send `error_description` / `error_uri`? Send `iss` on authorize errors (RFC 9207)? API 401 with `WWW-Authenticate: Bearer error="invalid_token"`? | Yes to all (A-20, A-24, A-25) | TL | errors §1-3 |
| OQ-18 | Which cases give `interaction_required` vs `login_required` with `prompt=none`? | errors.md table (A-22) | TL | errors §2 |
| OQ-19 | `temporarily_unavailable` at the token endpoint isn't an RFC 6749 §5.2 code: HTTP 503 JSON OK? | Yes (A-23) | TL | errors §2 |
| OQ-20 | 8084 internal-list block: same minimal 403, or connection refused? | Not drafted (A-26) | TL | errors §4 |
| OQ-21 | `private_key_jwt` signing alg; revocation auth methods | RS256; same as token (A-27) | TL | discovery schema |
| OQ-22 | `amr` values for DB sign-in (password + TOTP) and for Entra (pass through?) | `["pwd","otp","mfa"]`; Entra pass-through (A-28) | TL / SA | claims schema |
| OQ-23 | Envelope: AUD §7A.4 uses flat `partner_id` / `client_id` / `location_id`; LD §8 L419 uses `scope.partner_id` / `scope.client_id` / `scope.location_ids`, plus `actor.session_id`, `target`, `action`. Which wins? Also `category` literal, `outcome` values, and AUD `client_id` (Cachd client) clashing with OAuth `client_id` | AUD flat names + LD extras; `event`; success/failure/denied (A-30, A-32) | SA | audit schema |
| OQ-24 | Full `auth.login.failed` reason list; field name for the HMAC'd username; `actor.user_id` null on failed sign-in; MFA `method` values; `auth.session.revoked` reasons | Partial lists (A-31, A-34-A-37) | SA | audit schema |
| OQ-25 | Fields for `auth.refresh.reuse_detected`, `auth.break_glass.used`, `auth.session.location_switched`; should stand-in events carry `x_standin`? | Envelope only; yes (A-33) | SA / TL | audit schema |
| OQ-26 | PLAN §3.1 audit list omits LD §8 events (`auth.password.reset_requested` / `reset_completed`, `auth.provider.changed`, `auth.identity.linked` / `unlinked`, `auth.bootstrap.completed`, `auth.mfa.policy_exclusion_detected`). In v1 or later? | Out of v1 | TL | audit schema |
| OQ-27 | `code/README.md` is in the plan tree (§5.1) but SSE may write only `contract/`, `src/`, `ui/`. Who creates it? | Not created; rules in `contract/README.md` | TL | - |
| OQ-28 | Where the code lives (PQ-201, left blank by Mohanraj on the 11:02 card) | Box only, local git | Mohanraj via PM | - |
| OQ-29 | Screen 8 (both variants) is not built by UX yet; the contract refers to it | - | UX / TL | §5.4-5.5 |
