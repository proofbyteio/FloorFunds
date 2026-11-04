# Login.EntraTests (ENT-*), SSE2, S2b

Ports (env-configurable, both pass the PortGuard: 18090–18199, never 8080/8081/18080–18082, must be free; otherwise fail fast):
- `ENT_STANDIN_PORT` = test host, default 18090
- `ENT_FAKEENTRA_PORT` = fake Entra issuer (SSE2-FAKE-TEST-ONLY), default 18093

Run (.NET 10 SDK). `dotnet test` works from this directory because the local `global.json` selects
the Microsoft.Testing.Platform runner (xunit.v3.mtp-v2 can't use the VSTest path on .NET 10). No package changes.

    export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
    cd tests/Login.EntraTests
    ENT_STANDIN_PORT=18094 ENT_FAKEENTRA_PORT=18095 dotnet test -c Release
    # or: dotnet run -c Release --project tests/Login.EntraTests   (from the repo root)

Signing keys are generated per run under /tmp only, never committed.

## Location membership check (TLD 2026-11-04 8:07, decision (a) refuse)

Order in `EntraEvents.OnTokenValidated`: tenant/issuer → guest → token checks → auth_time → link lookup
(`FindUserAsync`; no link = `bad_credentials`/`unknown_subject`) → **membership** → lock / disabled / status
(only `UserStatus.Active`) → amr/MFA → success.

Membership = `ExternalLoginMatch.PartnerId` non-null AND equal to `LocationLoginConfig.PartnerId` AND an
`auth.user_location` row. Both halves are always evaluated (assignment query runs even when the partner fails),
then one refusal: audit reason `not_location_user`, log detail `not_location_user`, no partner ids in audit or log.
Fail-closed: null partner, mismatch, or unassigned all refuse identically. Super Admin via Entra: out of scope.
Membership runs before lock/status so a locked user at a foreign location is not told it is locked.

Tests: ENT_04 (now `not_location_user`), ENT_04e x4 (null partner, mismatch+assigned, match+unassigned,
match+assigned signs in; no partner ids leaked), ENT_04f (mismatch vs unassigned give identical audit/log shape).

## Abstractions 7e0350b (S2a P2, SSE2 N1-N3), merged by 6402993

- `SecretRef` is a non-positional, `[JsonIgnore]` `SecretReference` (ToString `[redacted]`). `EntraLocation.SecretRef` is the
  same type, non-positional. `ISecretResolver.ResolveAsync(SecretReference)` is the only caller of `Reveal()`.
- `UserStatus { Unknown=0, Active=1, Disabled=2 }`: only `UserStatus.Active` passes (no numeric casts); default/Unknown →
  `bad_credentials`/`user_status_unknown`. `FixtureUser.RawStatus` (test-only) injects raw values.
- Tests: ENT_09u x2 (default, Unknown), ENT_19 (SecretReference never in logs/audit/ToString/JSON; the deployment ref
  contains `9q7z` so a leak is greppable).

## Deployment-level Entra config (TLD 9:50, TLD 10:12, D-51; S2b C1)

- Authority, client id and secret ref are per deployment, not per location. Login.Entra binds no config key (no
  `EntraOptions` binding; `EntraOptions` is handler tuning only) and keeps reading `SecretRef`, `OidcAuthority` and
  `OidcClientId` from `ILocationLoginConfigReader`. SSE's `NpgsqlLocationLoginConfigReader` fills them from
  `EntraDeployment` for entra locations and returns null for database locations (SSE 06dc8ac, `Auth.Data/Readers.cs`).
- **Config changes apply after a restart.** SSE's reader snapshots `EntraDeployment` at startup; there is no hot-reload.
- Login.Entra caches nothing per location; every request re-reads the config. `client_missing` (blank client id or null
  ref) is the request-time backstop. The ref is opaque: nothing parses or path-handles its suffix (C1-2, info only).
- Fixture: `EntraFixture.Deployment` (`FixtureDeployment`: Authority, ClientId, SecretRef) applies the same rule on every
  `GetAsync`/`GetBySlugAsync` call and is mutable in tests via `Mutate`. `FixtureConfig.Build` no longer takes
  authority/client/secret and `WithoutSecret` is gone. The harness has one deployment client id, one ref
  `arn:sse2-fake-test-only:secret:deployment-9q7z` and one generated secret (SSE2-FAKE-TEST-ONLY), replacing
  `ref-{a,b,c}`. A stand-in fixture JSON needs a `"deployment"` object; row-level oidc/secret fields are ignored.
- Tests: ENT_CFG_01 (null ref → `client_missing`; database rows get nulls), ENT_CFG_01b (deployment ref unresolvable →
  `secret_unresolved`), ENT_CFG_03/03b/03c (a changed ref+secret, client id or authority reaches the next request; no
  cache), ENT_CFG_04 (no deployment ref → `client_missing` at request time, including between challenge and callback and
  after an earlier success; resolver not called; blank client id too). Mutation proofs are in the commit message.

## ENT_04g (TLD 8:31 Q2)

Audit reasons stay distinct (internal, admin-only), but the user-facing response is byte-identical for unknown subject,
not_location_user (partner mismatch, unassigned), locked and disabled: status, headers and body bytes. Normalised only:
Date, request ids, Set-Cookie values, and the per-request id in the OIDC Correlation/Nonce cookie *names*.
