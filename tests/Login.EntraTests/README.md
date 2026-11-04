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

- `SecretRef` is a non-positional, `[JsonIgnore]` `SecretReference` (ToString `[redacted]`). `FixtureConfig.Build` sets it with
  `{ SecretRef = ... }`; `WithoutSecret` is `c with { SecretRef = null }`. `EntraLocation.SecretRef` is the same type,
  non-positional. `ISecretResolver.ResolveAsync(SecretReference)` is the only caller of `Reveal()`.
- `UserStatus { Unknown=0, Active=1, Disabled=2 }`: only `UserStatus.Active` passes (no numeric casts); default/Unknown →
  `bad_credentials`/`user_status_unknown`. `FixtureUser.RawStatus` (test-only) injects raw values.
- Tests: ENT_09u x2 (default, Unknown), ENT_19 (SecretReference never in logs/audit/ToString/JSON). Fixture refs are now
  `arn:sse2-fake-test-only:secret:ref-{a,b,c}-9q7z` so leaks are greppable.
