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
