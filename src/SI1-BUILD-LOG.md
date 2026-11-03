# SI1 build log (stand-in login service)

Append-only. Branch `feat/si1-standin`, based on 589fefd (the status commit; contract/ there is byte-identical to tag `contract-v1` e363090 except README/OPEN-QUESTIONS/build log). Commits via `/tmp/sse-d0/commit-si1.sh` (separate index `/tmp/sse-d0/idx-si1`; shared HEAD/index untouched). Toolchain: .NET SDK 10.0.403 at /home/box/.dotnet, runtime 10.0.14. Writes only in src/ (and ui/ later).

## SI1-1 skeleton (8:55 PM PT)
- What: `src/Login.StandIn` (Microsoft.NET.Sdk.Web, net10.0), `src/nuget.config` with all package sources cleared, `.gitignore` for bin/obj.
- Why: PLAN §3.3 (one minimal ASP.NET Core .NET 10 app, `code/src/Login.StandIn/`). Build flag guard PLAN §3.5: an MSBuild target fails the build unless `-p:IncludeStandIn=true`.
- Evidence: build with the flag → 0 errors; without it → "Login.StandIn builds only with -p:IncludeStandIn=true".
- Decision [ASSUMPTION A1]: the sources-cleared nuget.config makes "no package fetching" structural. Restore uses only SDK packs.
- **BLOCKED [B1]:** PLAN §3.3 names `Microsoft.IdentityModel.JsonWebTokens` for signing. It is NOT in ~/.nuget/packages or the shared framework (checked: ls ~/.nuget/packages; /home/box/.dotnet/shared/Microsoft.AspNetCore.App/10.0.14). Token signing (authorize/token/ID token) is stopped until the TL decides. Options for the TL: (a) allow the package into the cache; (b) allow hand-rolled RS256 JWS with System.Security.Cryptography (BCL, no package). JWKS needs no package (RSA parameters from the BCL).
- [ASSUMPTION A2] Ports: front 18080, back 18081 (stands in for 8084; 8084 is outside my allowed ports), control 18082. All configurable. Contract 1.4 fixes the real back port at 8084; the stand-in port is config (PLAN §3.3 "front: configurable").

## SI1-2 dev-only startup guard (8:55 PM PT)
- What: `StartupGuard.Check` runs before the host is built. It refuses with exit code 2 and a single stderr line unless the environment is `Development` and `STANDIN_ALLOW=1`. It also refuses on a cloud marker (ECS_CONTAINER_METADATA_URI[_V4], AWS_EXECUTION_ENV) or a non-loopback bind host.
- Why: PLAN §3.5 row 2; standin-control 1.2; PLAN S1 check 11.
- Evidence (env -i, port 18080, timeout 3 s): Production → rc 2 "must be Development"; no STANDIN_ALLOW → rc 2; AWS_EXECUTION_ENV → rc 2; StandIn__BindHost=0.0.0.0 → rc 2; Development + STANDIN_ALLOW=1 → listening on 127.0.0.1:18080 (rc 124 = killed by timeout, i.e. it ran).
- [ASSUMPTION A3] Loopback only. The PLAN also allows "the box's private network", which isn't needed here.

## SI1-3 front, back and control listeners (8:56 PM PT)
- What: Kestrel is configured explicitly, so ASPNETCORE_URLS is ignored. It runs three 127.0.0.1 listeners: front 18080, back 18081 and control 18082 (config StandIn:FrontPort/BackPort/ControlPort). The listener is chosen by the connection's local port, never by the Host header.
  - Front: 404 for `/_standin*` and for token/revoke/introspect.
  - Back: only token, revoke, introspect, discovery and JWKS; everything else 404.
  - Control: every response is no-store; a non-loopback remote gets 403; only `/_standin/*`.
  - The issuer defaults to http://localhost:{front}/standin. Start is refused if the issuer path lacks /standin.
- Why: contract 1.2-1.5 and 2.9; standin-control 1.1 and 1.3-1.5.
- Evidence (curl):
  - front /_standin/reset → 404;
  - front /standin/connect/token → 404;
  - back /standin/foo → 404;
  - control /other → 404 no-store;
  - the log shows all three listening on 127.0.0.1.
- Not proven: the 403 for a non-loopback caller. Because every socket binds to loopback, no such caller can reach it from the box; it is defence in depth. A unit test belongs to SSE2.
- Scripts (outside the repo): /tmp/sse-d0/run-si1.sh and stop-si1.sh. stop-si1 kills only the PID that run-si1 started.

## SI1-4 discovery and JWKS (8:56 PM PT)
- What: `{issuer}/.well-known/openid-configuration` and `{issuer}/.well-known/jwks` on both front and back. The bytes are serialised once at start, so both listeners serve identical bytes.
  - Discovery has exactly the 15 keys from 2.1 + 2.4, with every URL under the issuer (2.3).
  - JWKS holds one RSA-2048 key made in memory at start: kty RSA, use sig, alg RS256, kid `standin-<16 hex>`. No private material leaves memory.
  - Cache-Control: discovery `public, max-age=3600`; JWKS `public, max-age=300`.
  - JWKS needs no NuGet package (BCL RSA + WebEncoders).
- Why: contract 2.1-2.10 and 3.1-3.4.
- Evidence (live, 18080 and 18081):
  - `cmp` shows the front and back bytes are identical for both documents;
  - the headers are as above;
  - discovery validates against contract/openid-configuration.schema.json (jsonschema 2020-12 with format checker) with 0 errors;
  - the key set equals the stand-in exact set;
  - the JWKS has 1 key with the 6 members alg, e, kid, kty, n and use, and the kid starts with `standin-`.
- v1.1 note [V1]: the `disc` sample in check_schemas.py puts token_endpoint and revocation_endpoint at `localhost:8084`, but contract 2.3 says they sit under the public issuer. The stand-in follows 2.3; the sample should be aligned. No test impact, since the schema doesn't constrain the host.
- Not done: the allowlist runs first on the front (1.7, 2.10). The stand-in has no allowlist yet, and wiring data/allowlist.db is out of scope (that file is never touched).

## SI1-5 control routes: reset, clock, faults, audit; fixtures (8:57 PM PT)
- What:
  - `StandInState` holds the in-memory state; `TestClock` is real UTC plus an offset.
  - `POST /_standin/reset` → 204. It reloads fixtures.json and clears faults, audit and the clock offset.
  - `GET /_standin/clock` → `{"now":"…Z"}`.
  - `POST /_standin/clock/advance {"seconds":n}` → 200 `{"now":…}`. A negative or non-integer n gets 400 `{"error":"invalid_request"}`.
  - `PUT /_standin/faults` → 204. Keys outbox (ok|fail) and device_registry (ok|unavailable); anything else gets 400.
  - `GET /_standin/audit?since=` → `{"events":[],"next":n}`.
  - Fixtures (fixtures.json) cover users cashier_db and no_role_user; partners pci and nevada, one location each; apps cash_advance, back_office and reports (all PLACEHOLDER); and a 30-minute lockout.
- Why: standin-control 2.1, 2.2, 2.6, 2.7 and §4.
- Evidence (live on 18082; every response no-store):
  - the clock GET validates against standin-ui.schema.json `clock_get` (0 errors), before and after reset;
  - advancing by 301 moves now by +301 s;
  - -1 and 1.5 each get 400;
  - faults outbox=fail gets 204 and outbox=maybe gets 400;
  - audit returns `{"events":[],"next":0}`;
  - after reset the clock is back to real time;
  - the front's /_standin/clock gets 404.
- Assumptions:
  - [A4] The success shapes aren't in the contract, so I picked them: reset and faults return 204 with no body; advance returns the clock_get shape.
  - [A5] Reset puts the clock back to real time. This is the only "backwards" move, since standin-control 2.1 makes reset restore the clock.
  - [A6] The role name `cashier` and every fixture slug, password and id are unruled (standin-control §4 says OPEN → TL). SSE2 reads them from this file.
  - [A7] faults accepts a partial body.
- Not built (next commits): `PUT pcs/{pc_id}` and the fake agent; `PUT users/{username}` (needs the password-policy validator) and `GET users/{username}/totp` (needs TOTP, HMAC-SHA1 from the BCL); authorize, token, revoke, introspect and endsession, which need signing and are **BLOCKED B1**; the pages (ui/); audit emission. The clock is not yet consumed by anything.

## SI1-6 PUT /_standin/users/{username} and GET .../totp (9:00 PM PT)
- What:
  - `PasswordPolicy.Validate` returns the standin-control §3 rule ids in table order. It is the shared validator, so every future set-password path must use it.
  - Users are held in memory and reloaded from fixtures on reset. Passwords are stored only as PBKDF2-SHA256 hashes. A history of 4 drives `reused`.
  - The PUT body is password, roles [{app_code, role}] (app_code must be in the fixtures), location_slugs (must be in the fixtures) and status. It returns `{user_id}`, keeping an existing user's id. A violation gets 422 `{"violations":[…]}`; a bad body gets 400.
  - The TOTP is RFC 6238 using the BCL's HMACSHA1 (no package). Each user gets a 20-byte secret per reset, and the code is computed at the test clock.
- Why: standin-control 2.5 and §3.
- Evidence (live, 18082):
  - PCI with a short password → [min_length]; with no number → [letters_and_numbers]; Nevada with all lowercase → [two_of_four_types]; a password containing the username → [contains_username]; a valid one → 200 {user_id}; resetting the same password → [reused].
  - An unknown app_code → 400. The fixture password for cashier_db → [reused].
  - Each 422 body validates against standin-ui.schema.json `password_violations`.
  - The TOTP code changes after the clock advances 30 s. An unknown user's TOTP → 404.
  - RFC 6238 Appendix B SHA1 vectors (T=59, 1111111109, 1234567890, 2000000000) give 287082, 081804, 005924 and 279037: the last 6 digits of the RFC values. The check harness is in /tmp/sse-d0/totpchk, outside the repo.
- Assumptions:
  - A8: a letter is Unicode L* and a number is Nd after NFKC (an SA Proposal; OQ-SSE2-30 is still open).
  - A9: the 4 types are upper, lower, digit and other.
  - A10: SHA1, a 30 s step and 6 digits.
  - A11: status is one of active, disabled or locked.
  - A12: the presets of every partner the user's locations belong to all apply; a user with no location gets both presets.
  - The username is limited to 1-64 characters.

## SI1-7 B1 resolved per TL ruling 9:00 PM (a): pinned Microsoft-signed restore from nuget.org (9:01 PM PT)
- Version pin:
  - OpenIddict.Server 7.7.2 (net10.0) depends on Microsoft.IdentityModel.JsonWebTokens 8.19.2. Source: api.nuget.org/v3-flatcontainer/openiddict.server/7.7.2/openiddict.server.nuspec.
  - The csproj pins it with `Version="[8.19.2]"`.
- nuget.config:
  - One source, nuget.org.
  - packageSourceMapping allows only `Microsoft.IdentityModel.*` and `Microsoft.Bcl.Cryptography`. The Bcl ID was added after the first restore failed with NU1100 for this transitive dependency of Tokens 8.19.2. No System.* IDs were needed.
  - signatureValidationMode=require, with trustedSigners set to the nuget.org repository certificates and owners=Microsoft.
  - No credentials and no other feeds.
- The restore used packages.lock.json and then `RestoreLockedMode=true`. The locked restore was re-run and succeeded. The build has 0 warnings and 0 errors.
- Packages and sha512 (the .nupkg.sha512 files):
  - Microsoft.IdentityModel.JsonWebTokens 8.19.2: 9kokzW5MVsHjANELTihcvrI48azhzhdFQW+oaEce2rD/sGe5Vf57BRQdXFIyDoWluxYh3vhe8aLQ3mg3BP983w==
  - Microsoft.IdentityModel.Tokens 8.19.2: 4rGCcWWvktrOG1esOPr2DMLJpxl9DjIAD943L8C8K8Rcsq30vnOG0IcFMlckuOMots/FHNSUJDxqsmo/EG6eBw==
  - Microsoft.IdentityModel.Logging 8.19.2: 15BrxnYEFaE+3svU/8GB7EYQkOd+x0UdlqmNFoV3gY6vN/u4dg10NuXLoDSSUhxHPdc01cvKeoztwGvAw6Vhng==
  - Microsoft.IdentityModel.Abstractions 8.19.2: ASRJB/oHIKh2N45fTt/t/+p2WaKxWUfvjTrHUq2N39mON6wJTQTX2gwfZOtwWBM4FZ9I7uNi8rFNaynXbMYNsQ==
  - Microsoft.Bcl.Cryptography 10.0.2: rbP/krRdXXVG10QyWH0rWa5+0qP5Rob8VZDKno910kHnDcmH9XdFbBtdedO5pVVlpA1y2MXWx360zLLNLctJ+Q==
- Signatures: `dotnet nuget verify --all` shows, for all 5 packages, an author signature from CN=Microsoft Corporation and a repository signature from CN=NuGet.org Repository by Microsoft.
- OPEN, for the TL:
  - Every package gave the NU3018 warning "revocation server could not be reached / RevocationStatusUnknown: unable to get certificate CRL". The sandbox can't reach the CRL endpoint, so the revocation check could not complete; the signatures and the chain were otherwise accepted.
  - I did nothing to work around it: revocation mode is left at its default and no other route was used.
  - The TL should decide whether an offline revocation result is acceptable.

## SI1-8 TL 9:00 PM rulings applied (9:01 PM PT)
- A2 (accepted): src/README.md documents the 18081↔8084 mapping. Real deployments keep 8084.
- A4: I re-checked standin-control.md and it is silent on success response shapes. A4 stays (reset and faults → 204, clock → {"now"}, users → {user_id}). Logged for v1.1: "standin-control should specify success shapes".
- A6: fixtures.json now labels every app, partner, location, user and role object with `"_label":"FIXTURE"`, and the _comment says 'cashier' and all slugs and ids are FIXTURE, not product role names. A reset loads the file correctly, and the cashier_db reuse check still returns [reused].
- V1: §2.3 is followed (the issuer and front run on 18080, and the token endpoint is on the back listener). Logged for v1.1: "fix the checker sample's localhost:8084 token_endpoint to match §2.3".
- Post-freeze item 4 (recorded, not yet built): when the cap-change piece is built, the server must enforce the Partner Admin own-partner rule and must have a server-side test for it. It is not in SI1 scope tonight.

## SI1-9 PUT /_standin/pcs/{pc_id} (9:03 PM PT)
- What: fake PCs are held in memory and cleared on reset. The body is {mode, location_slug}. It returns {"cookie":{"name":"standin_pc","value":pc_id}} and sends no Set-Cookie (standin-control 2.3).
- Evidence (live): all 9 modes return 200 with the cookie body. These each return 400: pc_id without the `standin-` prefix, an unknown mode, an unknown location_slug, and a missing location_slug.
- Assumption A16: pc_id is `standin-` followed by 1-64 characters from [A-Za-z0-9._-] (cookie-safe).
- Not yet built: the front reading `standin_pc` and the fake agent behaviour per mode (2.4). Those need the sign-in and nonce flow.

## SI1-10 Front network allowlist, the first middleware on the front listener (9:03 PM PT)
- What:
  - The allowlist middleware is registered before the listener rules and routing, and applies to front requests only.
  - It matches the TCP peer address against active IPv4 and IPv6 CIDRs.
  - A blocked caller gets the errors.md 403: text/plain; charset=utf-8, Content-Length 29, body `403 Forbidden: access denied\n`, Cache-Control no-store, Connection close, Server: server, Date and X-Request-ID. HEAD gets no body.
  - A blocked caller emits no audit event (errors.md: allowlist 403s are not on the outbox).
- Data:
  - /tmp/sse-d0/export-allowlist.py opens /tmp/sse-d0/allowlist-copy.db with `mode=ro` and writes the active rows to /tmp/sse-d0/allowlist-active.json (2 rows: 127.0.0.2/32 and 203.0.113.10/32).
  - The path is set by config `StandIn:AllowlistJson`.
  - The real data/allowlist.db is never opened.
- Evidence (live):
  - From 127.0.0.1 and 127.0.0.4 (inactive): 403 with the exact headers, and a body of 29 bytes ending in \n (od).
  - HEAD: 403 with no body. A POST to an unknown path from .4: 403 (the gate runs before the 404).
  - From 127.0.0.2: 200 discovery.
  - X-Forwarded-For: 127.0.0.2 sent from 127.0.0.1: 403.
  - The control (18082) and back (18081) listeners are unaffected (200).
  - With the JSON missing: loaded=False, and .2 gets 403 (fail closed).
- Assumptions:
  - A13: .NET has no SQLite package, so the active rows are exported to JSON with the Python stdlib. The stand-in reads that snapshot at startup; a DB change needs a re-export and a restart.
  - A14: there are no trusted proxies, and XFF is ignored.
  - A15: a missing or unreadable list means fail closed.
- Note: the 127.0.0.1 tester is now blocked on the front. Earlier front tests (SI1-4) must use `--interface 127.0.0.2`.
- Not built: the back-listener internal source list (errors.md, LD §4.5).

## SI1-11 Audit emission into the control feed (9:04 PM PT)
- What:
  - `Audit.Build` creates the audit-events.schema.json envelope: a UUIDv7 event_id (Guid.CreateVersion7 at the test clock), schema cachd.log, category event, is_audit true, plus actor, source, details and an optional scope.
  - `Audit.UsernameHmac` is HMAC-SHA256 hex over the trimmed, NFKC-normalized, lowercased username.
  - `StandInState.Emit` appends to the feed that GET /_standin/audit serves; reset clears it. Emit itself was added to Control.cs in SI1-9.
  - No endpoint emits yet: the sign-in, app and admin flows are not built, and the allowlist 403s are not on the outbox per errors.md.
- Evidence (harness /tmp/sse-d0/auditchk, outside the repo; it compiles the repo sources and drives StandInState.Emit and AuditSince):
  - auth.login.failed and auth.login.succeeded are each VALID under Draft 2020-12 with FormatChecker, and their event_ids are UUID v7.
  - The negative case (no scope, and carrying `username`) is INVALID, as expected.
  - The feed gives next=3; after a reset, next=0.
- Also added: src/Login.StandIn/scripts/export-allowlist.py (the read-only export used by SI1-10).
- Assumptions:
  - A17: schema_version is 1.0.0.
  - A18: source.service is login-standin.
  - A19: the test HMAC key is fixed.
  - A20: the server does not validate events against the schema at runtime (there is no JSON Schema package). Validity comes from how Build constructs them and from harness checks.

## SI1-12 TL Decision 9:06 PM: NU3018 accepted for the local stand-in only (9:05 PM PT)
- Conditions:
  1. The warning stays visible. It is not suppressed (no NoWarn or WarningsNotAsErrors change), and NUGET_CERT_REVOCATION_MODE=offline is not set. I checked: neither nuget.config nor the csproj changes revocation mode or NoWarn.
  2. **Release gate v1.1/CI-01:** before any build leaves this box, the pipeline re-verifies with online revocation checking (`dotnet nuget verify --all`), and any NU3018 there fails the build.
  3. Adding any package or new source mapping needs a TL ruling; stop and report if one is needed. Microsoft.Bcl.Cryptography 10.0.2 is accepted.

## SI1-13 Token signing, OAuth clients, fixture additions (9:20 PM PT)
- Evidence note for SI1-13..16: every result below comes from ONE run against the final build (all four slices), after a reset: `/tmp/sse-d0/e2e/drive.py`, output `/tmp/sse-d0/e2e/si1-13-16-evidence.txt`, **84/84 PASS**. Only temp DB copies were used; ports 18080-18082.
- **D1 (divergence, v1.1 list):** the task text says "ES256/JWKS per the contract", but the contract (2.1, 3.1, 8.1, claims `jose_header_*`, discovery `id_token_signing_alg_values_supported`) says **RS256**. I followed the contract: tokens are RS256 with a `standin-` kid. ES256 is used only for the fake-agent device attestations (device-attestation.schema.json).
- Tokens.cs: access token typ at+jwt, 300 s; ID token typ JWT, aud = azp = client_id, nonce only on the code grant; ±30 s skew on validation.
- Clients.cs: client_secret_basic (SHA-256 hash, fixed-time compare). fixtures.json: clients cash_advance_bff and backoffice_stub, cachd_client_id per partner, user nevada_db, back_office/clerk for cashier_db, and perm_version.
- Evidence: discovery validates openid-configuration.schema.json; JWKS has RS256 `standin-569d0f257300dccb`; token kid == JWKS kid; the RS256 signature verifies independently (pure-Python PKCS#1 v1.5; no package installed); headers validate jose_header_access_token / jose_header_id_token; payloads validate access_token_other, id_token_code, id_token_refresh; x_standin true, amr [pwd,otp,mfa], aud backoffice-api.
- Assumptions: **A21** perm_version is 1 for every fixture user. **A24 / blocker B2**: private_key_jwt is refused with invalid_client, because the fixtures have no client JWKs (a TL ruling is needed to add them). **H1** (v1.1): the hub client is not registered, because the hub token's aud is undefined.

## SI1-14 Sign-in, MFA, attestation nonce flow (9:20 PM PT)
- Auth.cs, SignIn.cs, Program.cs (antiforgery). The order is: attestation, then lockout, then password, then verified remote_session, then TOTP (±1 step). The pre-session id is reissued after the password step, and the new session cookie is set after MFA (4.11).
- Evidence: sign-in → TOTP → hub; the 4.11 cookie checks pass; all 8 PC modes (valid, missing, and the six that should be invalid) give the expected `attestation` detail; remote_session → screen-8a + login.failed remote_session; registry unavailable → invalid (fails closed); a re-minted nonce voids the old one; no antiforgery → 400; the fake-agent JWS validates the schema; a wrong password and an unknown user give the same bad_credentials; a wrong TOTP gives mfa.failed + login.failed; lockout is Nevada 3 and PCI 10 tries; the lock lifts after 30 min. Every emitted event validates audit-events.schema.json.
- Assumptions:
  - **A22 / AG1** (v1.1): the fake agent runs on the control port, `/_standin/agent/attest`, with CORS.
  - **A23**: the slug comes from the standin_loc cookie, else the first fixture location.
  - **A25**: if the outbox fails at MFA, no session is created.
  - **A26**: a wrong TOTP emits both auth.mfa.failed and auth.login.failed reason=mfa_failed.
- Note: antiforgery makes Cache-Control `no-cache, no-store`; no-store is still present.

## SI1-15 /connect/authorize (9:20 PM PT)
- Evidence:
  - A bad client or redirect gives 400 with no echo and no event. invalid_scope, invalid_request, unsupported_response_type and login_required all carry iss and state.
  - With no session, the user goes to the sign-in page and then back to authorize. The code goes to the exact redirect, with method interactive and then sso.
  - prompt=login and max_age force a fresh sign-in.
  - Cash Advance with no hub_launch gives notfromhub plus the sign-in page.
  - With no role, the client gets access_denied and auth.app_access.denied no_role (target app/back_office).
  - An outbox failure gives temporarily_unavailable, with no code and no event.
- Fix during this run: auth.app_access.denied used outcome `failure`. The schema requires `denied`, so I corrected it.
- Gap **G-H** (open): hub_launch issuance and the Cash Advance hand-off (5.7) are not built, so Cash Advance always gets notfromhub.

## SI1-16 token / revoke / introspect / endsession (9:20 PM PT)
- Evidence:
  - The token response has the right fields, no-store and Pragma.
  - Codes are single-use, and reuse revokes the family. A wrong PKCE verifier, a code older than 60 s, or a bad secret are rejected (401 with WWW-Authenticate Basic). The token endpoint isn't served on the front port (404).
  - Refresh rotation: a reuse inside the 30 s leeway is accepted. After it, the result is invalid_grant plus auth.refresh.reuse_detected, and the whole chain is revoked.
  - If the user loses the role, refresh gives invalid_grant.
  - Introspect gives the active shape. For another client or an unknown token it returns exactly `{"active":false}`.
  - Revoking a refresh token revokes its family. Revoke always returns 200, and returns 401 without client auth.
  - Endsession:
    - With no hint, a confirm page shows.
    - An expired hint is accepted, gives a 302 to /standin/l/pci-main, and writes one session.revoked sign_out. After that, refresh fails, prompt=none gives login_required, and a second sign-out writes no second event.
    - The confirm POST works. Without antiforgery it returns 400.
- Assumptions:
  - **A27/A28**: an outbox fault returns 503 temporarily_unavailable with Retry-After: 5.
  - **A29**: introspect covers access tokens only.
  - **A30**: post_logout_redirect_uri is never followed (no open redirect); the user always goes to the location sign-in page.
- Not built here: the recovery_reenroll paths (7.8), the admin paths (5.12), and the session-cap UI. The NU3018 warning was not shown in this build because restore was cached (no-op). I suppressed nothing.
- md5 (driver, evidence):
    948af73451e0493360ced9da84705f95  /tmp/sse-d0/e2e/drive.py
    6564ee37ff911d766e6414b314f60935  /tmp/sse-d0/e2e/si1-13-16-evidence.txt

## TL rulings 9:16 PM and item (1) hand-off (2026-11-02, 9:16 PM-)

### Rulings logged
- **B2 [TL Decision 9:16 PM]**: FIXTURE client keys. RSA-2048 per client (7.6: private_key_jwt is RS256 only), generated fresh at start and every reset (`Clients.Load`). Private PKCS#8 PEM written only to `$STANDIN_CLIENT_KEY_DIR` (default `/tmp/sse-d0/fixture-client-keys`, mode 0600); `Clients.Load` throws for any path outside /tmp. Public key in memory only. private_key_jwt per 7.6: alg pinned RS256 (none/HS*/ES* refused), iss = sub = client_id, aud = issuer exactly, jti required and refused on reuse until exp (+30 s skew), exp − iat ≤ 60, iat skew +30 s. Token, revoke and introspect all accept it. Basic and an assertion together → invalid_client.
- **B2 secrets check**: `src/Login.StandIn/scripts/check-fixture-secrets.sh [index]` fails if a tracked file is key-like (*.pem/*.key/*.p8/*.pfx/*.p12), has a PEM PRIVATE KEY block, or has a JWK with private members. It ran on the si1 index (OK, 36 files) and against a negative case in a throwaway repo (FAIL, rc=1). It's not wired into tests/ or tools/ (both off limits), so it's a manual step until the owner of tests/tools wires it into CI.
- **H1 [TL Decision 9:16 PM, interim; v1.1 for SA ratification]**: hub client `hub` registered (FIXTURE secret). Access-token aud = "hub" (its client_id, PLACEHOLDER). No app_code, no roles, no role check, no org claims. **v1.1 conflict:** `claims.schema.json` access_token.aud enum is ["cash-advance-api","backoffice-api"] and rejects "hub". The schema (frozen) needs an update.
- **D1 accepted [TL 9:16 PM]**: RS256 for tokens, ES256 only for attestations. v1.1: the contract text states it.
- **A29, A30 accepted [TL 9:16 PM]**: v1.1 items for the contract to state them (introspect answers for access tokens only; post_logout_redirect_uri never followed).
- **DEC-093 [TL 9:22 PM; Mohanraj 9:21 PM via PM]**: v1.1 item: clarify the §7.8 text. After re-enrolment finishes, the session ends and redirects to the sign-in entry of the same kind: an admin_type session goes to /admin, a location session to its own /l/{slug}. Each needs a test. **To be built in step (3) recovery_reenroll, not built now.**

### Item (1) built: hub_launch + 5.7 hand-off
- Handoff.cs: hub_launch is 16 bytes of CSPRNG, base64url, stored only as SHA-256 (`AuthStore.HubLaunches`), bound to sid + client_id. Issued on each hub view for role tiles of hand-off clients only. Tile = start_url + ?hub_launch=. GET /standin/apps re-renders the hub (2.8: tests read hub_launch from the tile; no control route).
- authorize order for cash_advance_bff: consume hub_launch (even with no session, or when a later rule fails) → 5.13 recovery_session → 5.12 not_location_user → ViaReturn-fresh session = interactive → rule 4 (notfromhub event, re-prompt) → rules 1,2,3,5,6 (no event, 5.10, re-prompt) → interstitial. prompt=none with unmet rules or a cage_consumed session → interaction_required.
- Interstitial (5.8): no-store, CSP frame-ancestors 'none', connect-src 'self' + agent origin, antiforgery. Server-side tx (cookie standin_hx) binds nonce + hub_launch hash + sid + client_id. POST /standin/connect/authorize carries device_attestation in the body. Attestation purpose "handoff". Rules are re-checked at completion.
  - invalid, missing, or tx/session mismatch → attestation_invalid, then re-prompt (password + MFA, then Cash Advance opens interactive).
  - remote → remote_session, then screen-8a.
  - valid → auth.app.signed_in method=hub_handoff with the original auth_time and sid; the session is marked HandoffUsed + CageConsumed. A cage_consumed session gets no SSO to anything.
- Re-prompt = /l/{slug}?ret=<authorize without hub_launch/prompt>&resign=1, rendered with the UX 07 page-resign-in ids.
- Minimal /standin/admin (UX 05 ids): password, then TOTP or a FIXTURE recovery code (stored hashed, single use). A recovery code gives a recovery_reenroll session with amr [pwd]. The fixture admin `partner_admin` (admin_type partner).

### Assumptions
- A31: hub_launch is minted on every hub view (hub page after sign-in and GET /standin/apps); earlier unused values stay valid until 60 s.
- A32: for the hand-off client, only a session created by a sign-in that returned to this authorize (ViaReturn), within 60 s and with no code yet, counts as interactive.
- A33: /admin is minimal: no lockout, no attestation, no re-enrolment flow.
- A34: refusal order is recovery_session (5.13) first, then not_location_user (5.12).
- A35: 5.12/5.13 refusals apply to every client's authorize, not only the hand-off (fail closed). Admin sessions are never SSO'd into location apps.
- A36: a recovery hub shows only "set up your authenticator again" and sign-out; it writes no event at the hub (the recovery_session event is written at authorize).
- A37: the allowlist half of rule 5 is the existing global front middleware; there is no per-location allowlist.
- A38: the interstitial has no ui-driver id (v1.1 UI item).
- A39: the hub's auth.app.signed_in target.id is its client_id "hub" (no app_code).
- A40: a hub_launch presented with no session is still consumed.

### Gaps
- Rule 2: there's no live way to show the Entra mfa/ngcmfa → mfa mapping or a non-MFA location session (no Entra provider and no location recovery path). The code checks amr contains "mfa".
- A location-user recovery sign-in isn't built (step 3).

### Evidence status: BLOCKED (live run not done)
- The build succeeds (0 errors; NU3018 didn't re-print because restore was cached).
- At 9:24 PM, ports 18080-18082 were held by another stand-in: pid 628112, started 9:18 PM, `/tmp/sse2-si1/bin/Login.StandIn.dll` (SSE2). It isn't mine, so I didn't kill it.
- My first driver run went to that instance by mistake before I noticed. It sent control calls (reset, clock advance, PUT users, pcs) to SSE2's in-memory state. That needs reporting.
- The drive.py handoff/pkjwt scenarios are written (`/tmp/sse-d0/e2e/handoff.py`) but haven't run against this build.

## TL rulings 9:26 PM applied (2026-11-02, logged 9:31 PM PT)
1. **PORTS (TL Decision).** `STANDIN_PORT_BASE` (default 18080): front = base, back = base+1, control = base+2, loopback only, StartupGuard unchanged (Listeners.Read; the old per-port config keys are gone). We keep 18080-18082; SSE2 uses 18090-18092. Per-instance state dir `/tmp/sse-d0/standin-{base}/` holds fixture-client-keys/, si1.log, si1.pid (from run-si1.sh) and server.pid (written by the process). STANDIN_CLIENT_KEY_DIR still overrides the key dir.
   - run-si1.sh refuses to start (exit 3) if any of the 3 ports is listening, and after start kills its child and exits 4 unless the `ss -ltnp` owner of all 3 ports is the pid it started.
   - drive.py takes `--base` / STANDIN_PORT_BASE and aborts before sending any request unless the ss owner of all 3 ports equals the pid in the base-keyed pid file. I chose the ss-owner comparison over a new control route, so standin-control (frozen contract) is unchanged.
   - Proven: base 18090 (SSE2, pid 639792): run-si1.sh refused with "STOP: port 18090 already held"; drive.py aborted with "no pid file". With a fake pid file it aborted with "owner 639792 is not our pid 999999; no traffic sent". No traffic went to SSE2.
2. **H1 revised.** The hub client is openid-only: fixture `aud` null, so the token endpoint returns `{id_token, scope}` and no access_token, token_type or refresh_token. The id_token aud = "hub" (client_id). An API scope request returns invalid_scope. aud=hub is removed from the access-token path (TokenIssuer.Access throws for a null-aud client). v1.1 (a), the claims-schema aud conflict, is **closed with no schema change**. [ASSUMPTION A43] Omitting access_token departs from RFC 6749 §5.1 (REQUIRED); it is done by the TL ruling and noted for v1.1.
3. **(b) Rule 2 negative paths.** New FIXTURE users cashier_pwd and cashier_wia have a per-user `fixture_amr` override ([pwd] or [wiaormfa]) that is applied when the location session is created and carried into auth.login.succeeded amr. Live, both get the hub tile, and the hand-off is refused with a re-prompt, no code and no event (5.10). The positive ngcmfa path is pending until there is an Entra provider.
4. **(c)** v1.1 item: the fixture secrets check; SSE2 will wire it (scripts/check-fixture-secrets.sh stays local).
5. **A32 TL ruling.** Interactive = now − auth_time ≤ 300 s AND the session's last authentication was a real credential entry (password + MFA) made for this authorize. auth_time is set only by credential entry. Cash Advance counts as interactive only when ViaReturn, no code has been issued from the session yet (credential entry not spent), and prompt≠none. The old 60 s / ViaReturn heuristic is replaced for the hand-off client; the 60 s method label for other clients is unchanged.
   - [ASSUMPTION A42] Ages are compared in whole elapsed seconds (truncated, like the integer auth_time claim), because the test clock runs on from real time. This applies to rule 1, A32 and the 60 s hub_launch.
   - Tested live: 300 s gives interactive and 301 s is refused; SSO reuse gives notfromhub; prompt=none gives interaction_required; after a refresh grant the session is not interactive; hand-off rule 1 passes at exactly 300 s.
6. **A35 and A40 accepted.**
7. **DEC-094** (build log + v1.1): the 403 shows only the message, with no help text; the body is unchanged. **DEC-095** (v1.1): device-enrolment work, where a Super or Partner Admin issues cage-PC codes and a Partner Admin only for its own partner.
- [ASSUMPTION A41] Only the hand-off target client reads hub_launch; another client ignores it, so the launch stays usable for Cash Advance. This replaces the placeholder "wrong client" check.
- Fixes found by the first live run of handoff.py:
  - The interstitial was missing its CSP. It now has frame-ancestors 'none' and connect-src for the agent port.
  - The rule-5 test used a slug that does not exist and no password (the PUT returned 400).
  - The id_token schema def is id_token_code.
  - The "recovery code single use" placeholder is now real: a second use gives no hub and no new login.succeeded.

### Evidence status: DONE (supersedes BLOCKED above)
- Live run 2026-11-02 on base 18080, own pid 643647 (guard ok): **TOTAL 143 / 143**, 85 earlier checks + 58 hand-off/pkjwt/H1/(b)/A32. Then the guard negatives.
- Evidence: /tmp/sse-d0/e2e/si1-item1-evidence.txt, md5 **276d88d001535ac6eac2723dbe0bb5d1**.
- Server stopped; 18080-18082 are free afterwards. allowlist.db md5 is unchanged (e5384097dd60ee5df568577ec9126071).

### v1.1 list additions
- (a) claims-schema aud conflict: CLOSED, no schema change (H1 revised).
- (c) fixture secrets check: SSE2 wires it.
- RFC 6749 §5.1 access_token omission for the openid-only hub (A43).
- DEC-093 (§7.8 re-enrolment redirect by session kind; build in step 3).
- DEC-094 (403 message only, no help text, body unchanged).
- DEC-095 (device-enrolment codes: Super/Partner Admin, Partner Admin only for its own partner).
- Positive ngcmfa rule-2 path (needs an Entra provider).

## SI1 FROZEN @ 3feb66c (2026-11-02, PT)
- TL accepted SI1 at 3feb66c at 10:12 PM PT. The local tag si1-freeze stays on 3feb66c; this log-only commit sits on top of it.
- Live suite 251/251. Evidence /tmp/sse-d0/evidence-final.txt, md5 aa782b7bbda57c6198a00498e44ac770. The step (4) and final-commit details are in src/Login.StandIn/SI1-BUILD-LOG.md.
- **A68 is a TL Decision (10:12 PM, stand-in only):** the cage_action audience is "cash-advance-server" (value taken from contract/check_schemas.py).
- **v1.1 item V11-CAGEAUD (SA):** name the exact cage_action aud value for the Cash Advance server. Contract 6.7(5) says only "its own id".
- No new work after this.
