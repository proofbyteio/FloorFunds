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
