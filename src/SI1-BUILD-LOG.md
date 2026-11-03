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
