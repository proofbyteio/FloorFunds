# SI1 build log (stand-in login service)

Append-only. Branch `feat/si1-standin`, based on 589fefd (the status commit; contract/ there is byte-identical to tag `contract-v1` e363090 except README/OPEN-QUESTIONS/build log). Commits via `/tmp/sse-d0/commit-si1.sh` (separate index `/tmp/sse-d0/idx-si1`; shared HEAD/index untouched). Toolchain: .NET SDK 10.0.403 at /home/box/.dotnet, runtime 10.0.14. Writes only in src/ (and ui/ later).

## SI1-1 skeleton (8:55 PM PT)
- What: `src/Login.StandIn` (Microsoft.NET.Sdk.Web, net10.0), `src/nuget.config` with all package sources cleared, `.gitignore` for bin/obj.
- Why: PLAN §3.3 (one minimal ASP.NET Core .NET 10 app, `code/src/Login.StandIn/`). Build flag guard PLAN §3.5: an MSBuild target fails the build unless `-p:IncludeStandIn=true`.
- Evidence: build with the flag → 0 errors; without it → "Login.StandIn builds only with -p:IncludeStandIn=true".
- Decision [ASSUMPTION A1]: the sources-cleared nuget.config makes "no package fetching" structural. Restore uses only SDK packs.
- **BLOCKED [B1]:** PLAN §3.3 names `Microsoft.IdentityModel.JsonWebTokens` for signing. It is NOT in ~/.nuget/packages or the shared framework (checked: ls ~/.nuget/packages; /home/box/.dotnet/shared/Microsoft.AspNetCore.App/10.0.14). Token signing (authorize/token/ID token) is stopped until the TL decides. Options for the TL: (a) allow the package into the cache; (b) allow hand-rolled RS256 JWS with System.Security.Cryptography (BCL, no package). JWKS needs no package (RSA parameters from the BCL).
- [ASSUMPTION A2] Ports: front 18080, back 18081 (stands in for 8084; 8084 is outside my allowed ports), control 18082. All configurable. Contract 1.4 fixes the real back port at 8084; the stand-in port is config (PLAN §3.3 "front: configurable").
