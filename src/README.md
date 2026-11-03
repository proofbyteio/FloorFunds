# src/ — SI1 stand-in (Login.StandIn)

Dev/test-only stand-in. It builds only with `-p:IncludeStandIn=true` and will refuse to start unless both `Development` and `STANDIN_ALLOW=1` are set.

## Port mapping (TL ruling A2, accepted 9:00 PM)

| Listener | This sandbox | Real deployments |
|---|---|---|
| front (issuer, allowlist) | 127.0.0.1:18080 | per contract |
| back (token endpoint) | 127.0.0.1:18081 | **8084** (unchanged) |
| control (`/_standin/*`) | 127.0.0.1:18082 | per standin-control |

- 18081 is a sandbox-only stand-in for 8084, because the sandbox may use only ports 18080-18082. Real deployments keep 8084.
- The contract checker sample's localhost:8084 token_endpoint is V1, logged for v1.1.

## Fixtures
`Login.StandIn/fixtures.json` holds FIXTURE data only (ruling A6).

## Packages
The only package is Microsoft.IdentityModel.JsonWebTokens, pinned at exactly 8.19.2. It restores from nuget.org in locked mode (see SI1-7 in SI1-BUILD-LOG.md).
