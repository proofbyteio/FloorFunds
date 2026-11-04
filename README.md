# FloorFunds: Cachd Ones login + hub (S1 stand-in, S2b Entra sign-in)

This folder holds the S1 build: the written login contract, the stand-in login service and hub UI, and the tests that check them against plan `plans/login-hub-build-plan.md` §5.1 (checks 1–12, 4a, 4b).
Git: one remote only, `origin` = https://github.com/proofbyteio/FloorFunds.git; only the TL pushes (TLD-S2B-01). Agents never push, change remotes or touch credentials. Ports 8080 and 8081 are never used.

## Who owns what

| Path | Owner | Notes |
|---|---|---|
| `contract/` | SSE | Contract v1 (DRAFT until the TL freezes it), schemas, `standin-control.md`, `ui-driver.md`, `SSE-BUILD-LOG-PENDING.md`. Branch `feat/contract-v1` |
| `src/`, `ui/` | SSE | Stand-in service, BFF stub, hub UI (except `src/Login.Entra`) |
| `src/Login.Entra`, `tests/Login.FakeEntra`, `tests/Login.EntraTests` | SSE2 | S2b Entra sign-in library, fake issuer, ENT-* tests. Branch `feat/entra-signin` |
| SI1 `tests/`, `docs/`, `tools/` | SSE2 | Test suites, build log, contract review, threat model, test matrix, scripts. Branch `sse2/tests-docs` (not on this branch) |
| `README.md` | SSE2 | This file |

Nobody edits another owner's folder. SSE's build-log entries reach `docs/BUILD-LOG.md` only through `tools/import-sse-log.sh <commit>`.

## Running the SI1 checks (branch `sse2/tests-docs`)

Needs the .NET 10 SDK (`DOTNET_ROOT=$HOME/.dotnet`) and Python 3.

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
tools/run-checks.sh standin     # or: tools/run-checks.sh real
```

It builds `tests/`, runs the contract, security and e2e suites and prints one line per check: PASS, PARTIAL (runnable tests passed, some pending), PENDING (all skipped), NOT-RUN, MANUAL or FAIL, plus a TOTAL line. Pending tests are xUnit skips whose reason starts `PENDING` and names the open question or ruling. Results go to `/tmp/sse2-results.*`.
PASS or PARTIAL is not S1 sign-off. Without a running stand-in, every server-behaviour test is pending.

Other tools (branch `sse2/tests-docs`): `tools/check-no-remote.sh` (exactly one remote, `origin` → FloorFunds, plus the pre-push hook), `tools/check-no-standin.sh` (no stand-in code in a real build), `tools/import-sse-log.sh`.

## Environment

| Variable | Values / meaning |
|---|---|
| `LOGIN_CONTRACT_TARGET` | `standin`: run against the stand-in, including `standin-only` tests; `real`: the real login service, where `standin-only` tests skip with a reason. `run-checks.sh` sets it from its argument (default `standin`) |
| `LOGIN_FRONT_URL` | Front (browser-facing) listener base URL, e.g. `http://localhost:5080` (name proposed by SSE2) |
| `LOGIN_BACK_URL` | Back-channel base URL (token/revoke), e.g. `http://127.0.0.1:8084` (name proposed by SSE2) |
| `LOGIN_STANDIN_CONTROL_URL` | 127.0.0.1 control listener for `/_standin/*` (contract `standin-control.md` 1.1) |
| `CACHD_CODE_ROOT`, `CACHD_MOCKUPS_DIR` | Optional overrides for the code root and the read-only mockups folder |

## S2b: Entra sign-in (this branch)

`src/Login.Entra` is self-contained (it never references the stand-in, TLD-S2B-04). Packages: exactly
Microsoft.AspNetCore.Authentication.OpenIdConnect [10.0.14] and Microsoft.IdentityModel.Validators [8.19.2],
from nuget.org only, with a lock file and package source mapping (`src/Login.Entra/nuget.config`).

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
cd tests/Login.EntraTests && dotnet build -nologo -v q && ./bin/Debug/net10.0/Login.EntraTests
```

The tests start a fake Entra issuer on 127.0.0.1:18093 and a test host on 18090 (port guard: 18090–18199 only).
Fake tenant ids are marked SSE2-FAKE-TEST-ONLY; signing keys are generated under `/tmp/sse2-fake-entra` and deleted after.
No real Azure tenant, app registration or credential is used. Lock files are enforced (`RestoreLockedMode`);
to regenerate one deliberately, restore with `-p:EntraUnlock=true` and review the diff.

