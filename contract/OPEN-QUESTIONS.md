# Open questions on contract v1 (DRAFT v1-d4, NOT frozen)

For the Tech Lead to answer or relay. "Who" = who I think owns the answer; the TL decides routing.
Every [OPEN → OQ-n], [CONFLICT → OQ-n] and [DOC SILENT → OQ-n] in the contract points here (the v1-d1/d2 ASSUME and SSE Proposal labels are retired at v1-d3).
Status (v1-d4): TL rulings `reviews/contract-v1-tl-rulings.md` md5 `5bcaedc9fc1c748743243a7dfd1a3a38` applied (incl. Addendum 8:12 PM, Correction 8:14 PM, Addendum 2 ~8:15 PM PT); earlier md5 `9012597c8e0b145988d95a2e034d20e0` at v1-d3. **Resolved** = TL Decision 2026-11-02 (relayed by the parent at 11:37 AM PT); **Open** = not decided; the contract uses the safe default shown.
LD line numbers refer to md5 `560cb6ab` (rev 8:25 PM PT, file time 20:17:36); re-mapped `b7385794` → `c2b7e689` → `560cb6ab` at v1-d4 (from `88878681` at v1-d3).

## Resolved

| OQ | Question (short) | Decision | Where applied |
|---|---|---|---|
| OQ-1 | Discovery and JWKS: front or 8084 only? | **Resolved, TL Decision 2026-11-02:** front listener (public, read-only, cacheable); token, revoke, introspect and other back-channel calls stay on 8084. Follow-ups OQ-30..32 | contract §1.3-1.4, §2.2, §2.8-2.10 |
| OQ-2 | Which source revisions? | **Resolved, TL Decision 2026-11-02:** always the newest; snapshot each with its md5 in the file name. Used LD `88878681` (TL expected `5c68265c`; the SA revised again at 11:41), PLAN `ae18d664` | README sources |
| OQ-3 | Discovery endpoint URLs | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept:** public URLs; clients take token/revoke from config, never discovery; test: BFF stub never calls front `/connect/token` | §1.6, §2.3 |
| OQ-4 | Exact discovery/JWKS field set | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** stand-in exact set (2.1 + 4 fields, exact-key test); real service may add standard fields; token response `no-store` + `Pragma: no-cache`; inactive introspection exactly `{"active":false}` | §2.4-2.6, §3.1, §7.5, §10.1, discovery schema |
| OQ-5 | Bad `redirect_uri` / `client_id` | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** error page, never redirect; exact match; generic text, no echo, `no-store`, `frame-ancestors 'none'`; no audit | §4.8, errors §3 |
| OQ-6 | Scopes, `aud`, client ids, `offline_access` | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept:** as proposed, plus unknown scope / `offline_access` from non-refresh client → `invalid_scope` | §4.9, errors §2, claims schema |
| OQ-7 | How does the hub's proof reach authorize (rule 4)? | **Resolved, TL Decision 2026-11-02:** `hub_launch`, issued by the hub / login service; single use, ≤60 s, server-stored, bound to login session id + target `client_id`; client passes it to authorize; checked and consumed; missing / invalid → `notfromhub`. Now also in LD §16.18.5 L1404-1407. Follow-ups OQ-36, OQ-41 | contract §5.7, errors |
| OQ-8 | How does the attestation reach the login service; who creates the nonce? | **Resolved, TL Decision 2026-11-02:** login service creates the nonce (server-stored, single use, ≤60 s, bound to the transaction); page calls the agent on loopback (managed Edge policy); page sends `device_attestation` (form or authorize parameter); server verifies against the enrolled device key registry; fails closed. LD §16.18.5 L1397-1402. Follow-up OQ-34 | contract §5.8, §6.4-6.7 |
| OQ-9 | Attestation wire format; stand-in PC id | **Resolved, TL Decision 2026-11-02:** compact JWS, ES256, TPM key, `kid` = enrolled device id; claims `nonce`, `device_id`, `remote_session`, `iat`, `exp`. Stand-in fakes the modes per PC id. Follow-ups OQ-33, OQ-35 | `device-attestation.schema.json`, §6.8 |
| OQ-10 | Re-prompt audit; `auth.session.revoked` | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept** | §5.10, §11.3 |
| OQ-11 | Invalid attestation at plain sign-in | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** goes ahead; unverified never trusted; optional `attestation=valid\|missing\|invalid` on `auth.login.succeeded` (TL Rec to SA); never "attested" | §6.2, audit schema |
| OQ-12 | Refresh after role loss / disable | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** disabled/locked → family revoked + `auth.session.revoked reason=user_disabled`; role loss → `invalid_grant`, that client's family only | §7.3, §9.2, errors §2 |
| OQ-13 | Absolute session cap value (LS-OQ-13) | **Resolved, Mohanraj Decision 2026-11-02 (t43u, 12:00 PM PT, verbatim: "In GitHub and 12 hr is fine but if we can have it configurable then it will be better"):** default 12 h, configurable. Setting name, bounds and validation → OQ-40 | contract §7.4 |
| OQ-14 | `typ`, `at_hash` | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** `ValidTypes=["at+jwt"]`; ID token as bearer → 401 | §8.9, claims schema |
| OQ-15 | Value types; `/admin` `location_id` | **Resolved, TL Decision 2026-11-02 (TL 8:11 PM PT on LD b7385794; 8:12 PM addendum; Addendum 2):** UUID strings with `format: uuid` + hex `pattern` (CR-01); `/admin` tokens carry **no** `location_id` (absent, not null), `/l/{slug}` tokens require it (LD §16.19.2 L1430, row 117). Access-token `aud` only `cash-advance-api` or `backoffice-api`. [TL Decision 2026-11-02, OQ-15, CR-01] | §8.10, claims schema (`access_token_admin`) |
| OQ-16 | Endsession | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** hint validated ignoring `exp`; exact `post_logout_redirect_uri` else ignored; always `/l/{slug}`; confirm page without valid hint | §11.2 |
| OQ-17 | `error_description`, `iss`, 401 header | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** `iss` on every authz response + discovery flag; strip OpenIddict `error_description`/`error_uri` (test); 401 header only | §4.10, errors §1-3 |
| OQ-18 | `login_required` vs `interaction_required` | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept** | errors §2 |
| OQ-19 | Token `temporarily_unavailable` | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** 503, `Retry-After`, `no-store`, no description, no partial grant, refresh token not consumed | §7.7, errors §2 |
| OQ-20 | 8084 block | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** network layer (no ALB TG, SG BFF+API only) + app layer (same minimal 403) | §1.8, errors §4 |
| OQ-21 | `private_key_jwt`; revocation auth | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** RS256 only; assertion rules; secrets ≥256-bit, hashed | §7.6, discovery schema |
| OQ-22 | `amr` values; what counts as MFA | **Resolved, TL Decision 2026-11-02 (TL 8:11 PM PT on LD b7385794; 8:12 PM addendum; Addendum 2):** tokens carry only RFC 8176 values, nothing passes through, enum closed. DB password+TOTP = `["pwd","otp","mfa"]`. Entra `amr` containing `mfa` or `ngcmfa` → `mfa`; `wiaormfa` never → `mfa`; a recovery-code sign-in gets `["pwd"]` and never counts as MFA (not hand-off-eligible). The old "exactly `mfa`" wording is withdrawn (LD row 119 L706, §16.19.3). PQ-221 (cage work after recovery-code sign-in) is with Mohanraj and doesn't change the contract. [TL Decision 2026-11-02, CR-02, OQ-22] | §5.1 rule 2, §8.10, claims schema |
| OQ-23 | Audit envelope naming | **Resolved, TL Decision 2026-11-02 (TL 8:11 PM PT on LD b7385794; 8:12 PM addendum; Addendum 2):** tokens use **flat** `partner_id` / `cachd_client_id` / `location_id`; the audit envelope uses the **nested** `scope.*` of AUD §7A.4 (`scope.partner_id`, `scope.cachd_client_id`, `scope.location_ids`); `category=event`; `outcome` = `success` / `failure` / `denied` (LD §16.19.4 L1443). [TL Decision 2026-11-02, OQ-23] | audit schema |
| OQ-24 | Reason lists and field names | **Resolved, TL Decision 2026-11-02 (TL 8:11 PM PT on LD b7385794; 8:12 PM addendum; Addendum 2):** `bad_credentials` covers unknown user and wrong password; `username_hmac` lowercase hex over the normalized username; **no** `password_expired` reason (expiry is a forced change); `rate_limited` is not an outbox reason (LD §16.19.5). [TL Decision 2026-11-02, OQ-24] | audit schema |
| OQ-25 | Extra event fields; `x_standin` | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept:** plus relay rejects/alerts on `x_standin` outside Development | §8.7, audit schema |
| OQ-26 | Extra LD §8 events in v1? | **Resolved, TL Decision 2026-11-02:** defined in the v1 schema, marked "Defined, not emitted in S1" (password reset, provider change, identity link/unlink, bootstrap, MFA policy exclusion). Follow-up OQ-38 | audit schema |
| OQ-27 | Who creates `code/README.md`? | **Resolved, TL Decision 2026-11-02:** SSE2 creates `code/README.md` and `docs/BUILD-LOG.md`; my log stays in `contract/SSE-BUILD-LOG-PENDING.md` | README |
| OQ-28 | Where the code lives (PQ-201) | **Resolved, Mohanraj Decision 2026-11-02 (t43u, same quote):** in GitHub. Org, repo name, visibility and setup → OQ-39. Until the TL says otherwise: no remote, no push, no GitHub actions; local git only | - |
| OQ-29 | Screen 8 not built yet | **Resolved, TL Decision 2026-11-02:** refer to it by name, "screen 8 (Session)", with variant A "remote session" and variant B "security software not responding"; UX is building it (`login-hub-mockups/08-session.html`, 11:38) | contract §5.4-5.5, §6.1, errors |
| OQ-30 | Cache headers | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** discovery `max-age=3600`; JWKS `max-age=300`, no stale-*; client refresh 5 min / 1 min, throttled unknown-`kid` refetch | §1.6, §2.8, §3.4 |
| OQ-31 | Discovery/JWKS on 8084 | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept:** same bytes and headers, comparison test | §2.9 |
| OQ-32 | Allowlist on front discovery/JWKS | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept** | §2.10 |
| OQ-33 | Stand-in fake agent | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** PC id only via control listener cookie `standin_pc`; refuses to start outside Development; check-no-standin greps `standin_pc` (SSE2's tool) | §6.8 |
| OQ-34 | Hand-off attestation page | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** POST body only (removed "or an authorize parameter"); server-side transaction binding; `frame-ancestors 'none'`, `no-store`, strict CSP, antiforgery | §5.8, §6.6 |
| OQ-35 | Attestation details | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** skew ±30 s; `alg` exactly ES256 pinned before parsing; `crit` refused; atomic nonce consume. **Overridden in part by the Addendum (12:15 PM PT):** `aud` and `purpose` are now required claims; verifier rejects wrong `aud`/`purpose`. Device id format still OPEN (below) | §6.5-6.7, attestation schema |
| OQ-36 | `hub_launch` details | **TL Decision 2026-11-02 (rulings md5 9012597c), Change:** 128-bit CSPRNG base64url, stored hashed; only for tiles with a role; log redaction of `hub_launch`/`code`/`state`; other client/session → `notfromhub`, consumed | §5.7 |
| OQ-37 | AUD `scope.client_id` → `scope.cachd_client_id` | **Resolved, TL Decision 2026-11-02 (TL 8:11 PM PT on LD b7385794; 8:12 PM addendum; Addendum 2):** the contract uses `scope.cachd_client_id` now; the schema refuses `scope.client_id`. AUD §7A.4 itself is renamed at its v0.8 unfreeze (LD row 114 L701). [TL Decision 2026-11-02, OQ-37] | audit schema |
| OQ-38 | `iam.permission.change_requested` | **Resolved, TL Decision 2026-11-02 (TL 8:11 PM PT on LD b7385794; 8:12 PM addendum; Addendum 2):** renamed `auth.provider.change_requested` (LD L166, §8 L427, row 115 L702); the schema allows `auth.*` names only; AUD catalogue rename at v0.8. [TL Decision 2026-11-02, OQ-38] | audit schema |
| OQ-41 | `notfromhub` refuse vs sign-in page | **TL Decision 2026-11-02 (rulings md5 9012597c), Accept:** sign-in page + event; SA aligns L1407 | §5.11 |

## Open (each with its safe default in the contract)

| OQ | Question | Safe default / TL Rec | Who | Refs |
|---|---|---|---|---|
| OQ-35 (part) | Enrolled device id format | 1-128 char string, exact-match registry lookup; UUID recommended, not required [TL Decision 2026-11-02, OQ-35, 8:12 PM reply] | Device Agent / hardware owner | §6.7, attestation schema |
| CR-22 (part) | Does a device's enrolment record its location or partner; may one device serve several locations? (LD §16.20.3 / row 122: Proposal (SA) one location or a set under one partner+client; OPEN, Device Agent owner) | Revoked / unenrolled / disabled device → `attestation_invalid` (decided). Location binding: §6.7 check runs only if the registry records one [safe default] | Device Agent owner + SA | §6.7 |
| CR-12 (part) | Production cage-action challenge design (LD §16.20.2 / row 121: Proposal (SA), Device Agent parts OPEN) | S1: stand-in BFF stub as specified in §6.9; paths and shapes accepted, built as part of SI1 [TL Decision 2026-11-02 8:26 PM]; production design still OPEN | SA + Device Agent owner | §6.9 |
| OQ-SSE2-30 | PCI "letter" / "number" for `Lt`, `Lm`, `Lo`, non-ASCII `Nd` (LD §16.20.1 / row 120: Proposal (SA) letter = `L*`, number = `Nd` after NFKC; OPEN, compliance owner) | ASCII cases testable now; Unicode PCI cases PENDING. TL Rec for the SA: same method as Nevada (after NFKC; letter = any `L*`, number = `Nd`) | SA → compliance owner (LS-OQ-52) | standin-control §3 |
| LS-OQ-52 | PCI compliance owner sign-off on password classes | follows OQ-SSE2-30 | Compliance owner | standin-control §3 |
| OQ-39 | GitHub account, repo, visibility, setup (OQ-28) | Local git only, no remote, no push, no credentials. The earlier "company org" proposal is withdrawn [TL Decision 2026-11-02, Addendum 2 §D] | Mohanraj (card t44s0) | - |
| OQ-40 | Session cap: who can change it; allowed range | **No values picked.** 12 h default, configurable (Mohanraj t43u; LD row 110); technical check cap > 15-min idle or start-up fails; deployment config only | Mohanraj (question t45s0) | §7.4 |

Note (not an OQ): `08-session.html` shows `local_agent.action_blocked reason=remote_session`. That is a device-agent event (LD L1254, L1500), not a login-service event, so it stays out of this contract.

## Addendum 2 items (TL rulings md5 5bcaedc9, ~8:15 PM PT)

Change requests (CR-n) and SSE2 questions (OQ-SSE2-n) are tracked in the rulings file; this is where each landed. All [TL Decision 2026-11-02, CR-n / OQ-SSE2-n] unless marked.

| ID | State | Where applied |
|---|---|---|
| CR-01 | Applied | claims/audit `$defs.id` pattern; §8.10 |
| CR-02 | Applied | §5.1 rule 2; claims `amr` |
| CR-03 | Applied | `id_token_code` / `id_token_refresh`; attestation header closed |
| CR-04 | Applied | errors.md §2 (`unsupported_response_type`, `unsupported_grant_type`) |
| CR-05 | Applied | standin-control.md, ui-driver.md; §1.5 |
| CR-12 | Applied (spec only, stub not built; flagged for TL); production design Open | §6.9 |
| CR-13 | Applied | §6.3; standin-control §4 (`pci`, `nevada`) |
| CR-14, CR-15 | Accepted as ruled (OQ-3 option c, OQ-4) | no change |
| CR-20, CR-21 | Applied (CR-21: header `additionalProperties:false`; README rule on format assertion) | attestation schema; README |
| CR-22, CR-23, CR-23b | Applied; CR-22 location source Open | §6.7 |
| CR-25 | Applied | §6.1, 6.2, 6.4, 6.5; errors.md §3 |
| CR-26 | Applied (note) | §7.4 |
| OQ-SSE2-04, 05, 07, 13, 29, 34, 35, 38 | Answered by CR-05 / CR-25 / CR-13 / CR-12 / CR-03 / CR-23b | as above |
| OQ-SSE2-30 | Open | table above |
| OQ-SSE2-31, 32, 33 | TL Decision / Answered (LD) | standin-control §3 |
| OQ-SSE2-36, 37 | Answered | §7.4 |
| OQ-SSE2-39 | Answered / partly Open (= CR-22) | §6.7 |
| OQ-SSE2-40 | TL Decision | §4.11 |

## TL Decision 2026-11-02 8:26 PM

All rows [TL Decision 2026-11-02 8:26 PM]. Schema cases: `check_schemas.py` (one positive and one negative each).

| Item | Decision | Where applied |
|---|---|---|
| SSE proposals (CR-05) | Accepted: `data-testid` values, `totp_code`, fake-agent timeout 3 s, password rule ids, clock `GET` `{"now":…}`, S1 BFF stub paths and shapes | ui-driver §1.2, §2; standin-control §2.2, §3; contract §6.5, §6.9; `standin-ui.schema.json` |
| §6.9 stub build | Specified for the v1 freeze; built as part of SI1 | contract §6.9 |
| Admin token `partner_id` | Superseded [TL Decision 2026-11-02 8:29 PM]: required claim `admin_type` super/partner discriminates; partner → `partner_id` required, super → absent; `_super`/`_partner` defs retired | contract §8.12; `claims.schema.json` |
| Location mapping | Token/session: single `location_id`; audit: array `scope.location_ids` (AUD §7A.4 L266) | contract §8.11; claims and audit schemas |
| Cite precedence | Where the contract and the rulings file differ, the contract's LD `560cb6ab` cites govern | README |

## TL Decision 2026-11-02 8:29 PM

| Item | Decision | Where applied |
|---|---|---|
| `admin_type` claim | Required on `/admin` tokens, `super` \| `partner`; schema discriminator (if/then) | contract §8.12; claims schema |
| Empty `location_ids` | `[]` only when the envelope has `admin_type` (an `/admin` event); otherwise ≥1. Keyed on `admin_type`, because no event class is `/admin`-only. Kept in the envelope; AUD naming is open item AUD v0.8 below. `location_ids` now required (≥1) without `admin_type` [TL Decision 2026-11-02 8:31 PM] | contract §8.13; audit schema |
| `admin_type` vs no-roles rule | Treated as sign-in kind, not a role. Resolved [TL Decision 2026-11-02 8:31 PM]: kind of sign-in, not a role; no-roles rule holds (contract 8.5) | contract §8.12 |

## Confirmed DEC-084 (Mohanraj 8:27 PM PT (t110s414): "No, cage work is only for location users")

| Item | Decision | Where applied |
|---|---|---|
| Cage work for admins | `/admin` sessions (super and partner) never get the Cash Advance hand-off or a cage action; refused with `not_location_user` (audit only) | contract 5.12, 6.9; errors.md; audit schema; standin-control 2.9 |
| (a)/(b) resolved [TL Decision 2026-11-02 8:31 PM] | `access_denied` confirmed as the client code for both refusals (hand-off and cage 403 body); the CA server audits a refused cage action with `not_location_user`, outside this contract | errors.md; `standin-ui.schema.json` |

## TL Decision 2026-11-02 8:31 PM

| Item | State | Who | Where |
|---|---|---|---|
| AUD v0.8 | **Open:** AUD owner to add `admin_type` to §7A.4 (TL asking the SA) | AUD owner via SA | audit schema envelope `admin_type`; contract 8.13 |

