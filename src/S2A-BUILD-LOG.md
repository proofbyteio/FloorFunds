# S2a build log (SSE) — location login config

Status: **P2 DONE (local commits, not pushed)** after TL Decision 7:58 (runtime EF approved). The blocker sections below are kept as history. Branch `feat/location-login-config`. Times are America/Los_Angeles (PT), Wed 2026-11-04.

## What was done

| Step | Result |
|---|---|
| P2 part 1 spec update (TLD 7:39, 7:41, 7:44, 7:45, 7:53) | commit `2d999a8` "S2a spec: apply TL 7:39 rulings"; `check_schemas.py` ALL PASS, `check_v1_1_s2a.py` 201/201 |
| `src/Login.Abstractions` (F9): enums, records, reader interfaces, `UserStatusText`, `ProviderKindText`, `LocationSlug` | builds, 0 warnings (TreatWarningsAsErrors), no packages, lock file empty |
| `src/Login.Abstractions.Checks` (package-free console checks; no test packages are approved) | 36 passed, 0 failed |
| `src/Login.StandIn` with `-p:IncludeStandIn=true` | still builds, 0 errors; untouched (keeps in-memory fixtures) |
| `src/Login.Abstractions` N1/N2 (SSE2 F-verify) | `UserStatus { Unknown = 0, Active = 1, Disabled = 2 }`; `SecretReference`; non-positional `[JsonIgnore] SecretRef`; checks now **52 passed, 0 failed** |
| `src/Auth.Data` (model, hand-written migration + snapshot, roles script, Npgsql readers), `src/Auth.Data.Migrator` (runner), `src/Auth.Data.Checks` (DB checks) | done; see "P2 approach" and "P2 results" |
| `db/migrations.sql` (idempotent, generated), `db/seed/s2a-test-app-user.sql` (SSE2-FAKE-TEST-ONLY) | done; md5 `2417afaadb064ed79f4f55b5b10ee4d3` / `b907854204946f085bbc35f730fb1c8d` |
| Spec: N1–N3, default-privileges correction (§8.3) | `check_schemas.py` ALL PASS, `check_v1_1_s2a.py` 201/201 |
| Evidence | `/tmp/sse-llc1/p2-evidence.txt` (md5 in the P2 commit report; not in the repo) |

## Blocker (stopped per brief: "If a restore is blocked… STOP and report"; nothing routed around)

Probe restore of the approved pins (Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, Microsoft.EntityFrameworkCore.Design 10.0.14 PrivateAssets=all) with the current `src/nuget.config` plus the two approved mapping patterns (`Npgsql*`, `Microsoft.EntityFrameworkCore*`), in a scratch project under `/tmp/sse-llc1/work/probe` (no repo change):

1. **NU3034** "signed but not by a trusted signer" for `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3` and `Npgsql 10.0.3`. The trust policy allows the nuget.org repository signer with `owners` = `Microsoft` only; nuget.org owners are `Npgsql`, `roji` (EF provider) and `brar`, `ninofloris`, `Npgsql`, `roji` (Npgsql). Fix needs a TL decision, e.g. add owner `Npgsql` to the nuget.org `<repository>` owners.
2. **NU1100** (source mapping) for Design's transitive packages, outside the two approved patterns: `Microsoft.CodeAnalysis.CSharp.Workspaces 5.0.0`, `Microsoft.CodeAnalysis.Workspaces.MSBuild 5.0.0`, `Microsoft.CodeAnalysis.CSharp 5.0.0`, `Microsoft.Extensions.DependencyModel 10.0.14`, `Mono.TextTemplating 3.0.0`, `Microsoft.Build.Framework 18.0.2`, `Newtonsoft.Json 13.0.4`. Their own transitives (from nuget.org metadata): `Microsoft.CodeAnalysis.Common 5.0.0`, `Microsoft.CodeAnalysis.Workspaces.Common 5.0.0`, `Microsoft.CodeAnalysis.Analyzers 3.11.0`, `Humanizer.Core 2.14.1`, `Microsoft.VisualStudio.SolutionPersistence 1.0.52`, `System.Composition{,.AttributedModel,.Convention,.Hosting,.Runtime,.TypedParts} 9.0.0`. Non-Microsoft owners among them (would also hit NU3034): `Humanizer.Core` (dotnetfoundation, Humanizer), `Mono.TextTemplating` (kzu, mhutch), `Newtonsoft.Json` (dotnetfoundation, jamesnk, newtonsoft). With `<FrameworkReference Include="Microsoft.AspNetCore.App"/>` the other Microsoft.Extensions.* packages were pruned (not reported).
3. NU3018 warnings (revocation server unreachable) were reported but not suppressed and `NUGET_CERT_REVOCATION_MODE` was not changed.

Options for TL: (a) trust owner `Npgsql` and map/trust the Design closure above; (b) trust `Npgsql` only and drop `Microsoft.EntityFrameworkCore.Design` (no `dotnet ef migrations add`; the migration and model snapshot would be hand-written C# EF migration classes, still applied by EF `database update` via a small runner — but `dotnet ef` itself needs Design in the project); (c) another approach TL prefers.

## TL Decision 2026-11-04 7:56 AM PT — option (b) [TLD; package rulings are TL's, TL is informing Mohanraj]

- `trustedSigners`: nuget.org repository owner `Npgsql` added (only; not roji, brar, ninofloris).
- Source mapping: exact ids `Npgsql`, `Npgsql.EntityFrameworkCore.PostgreSQL`, pinned `[10.0.3]`.
- `Microsoft.EntityFrameworkCore.Design` and `dotnet-ef` dropped entirely (there was never a `.config/dotnet-tools.json` or lock entry for them in the repo; nothing to remove).
- Migrations: hand-written `[Migration]` classes + hand-kept ModelSnapshot; a console runner (MigrateAsync, to 0 and back; writes `db/migrations.sql` via `IMigrator.GenerateScript` idempotent).
- NU3018 stays a warning; revocation mode unchanged.
- Any other package outside mapping/trust → STOP and report.

## 7:56 follow-up: restore result (STOPPED as instructed)

Applied the 7:56 config (committed in `src/nuget.config`). Probe `/tmp/sse-llc1/work/probe-b` (net10.0, FrameworkReference Microsoft.AspNetCore.App, the two pins), empty `--packages /tmp/sse-llc1/pkgs-probe-empty`, network fetch:

- NU3034 is gone: Npgsql 10.0.3 and Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 pass the signature check (NU3018 warnings only).
- **NU1100**: `Microsoft.EntityFrameworkCore (>= 10.0.4 && < 11.0.0)` and `Microsoft.EntityFrameworkCore.Relational (>= 10.0.4 && < 11.0.0)` — required by Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 and outside the mapping. Restore failed.

Diagnostic only, in `/tmp/sse-llc1/work/probe-c` (not the repo): with four additional exact patterns the restore from an empty `--packages` dir succeeds (network fetch, all signatures accepted; owners `Microsoft` already trusted). Full closure with the AspNetCore FrameworkReference (Microsoft.Extensions.* pruned):

| Package | Version | nuget.org owners | nupkg md5 |
|---|---|---|---|
| Npgsql | 10.0.3 | brar, ninofloris, Npgsql, roji | 1a1a0bc2517c93a15b285b1983bb4932 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | Npgsql, roji | fe918ee28cb0c0f01991e01d8ab45b37 |
| Microsoft.EntityFrameworkCore | 10.0.4 | aspnet, dotnetframework, EntityFramework, Microsoft | 76c7ef3e4908b91594389dd2b392a708 |
| Microsoft.EntityFrameworkCore.Relational | 10.0.4 | same | c9e2cde1b65745b3845665901511699b |
| Microsoft.EntityFrameworkCore.Abstractions | 10.0.4 | same | 223f31fdb105835ec77f248d6fa6513e |
| Microsoft.EntityFrameworkCore.Analyzers | 10.0.4 | same | 389fbbc7fd337730a848453f61c41084 |

Probe lock file md5 `9c295348f903ecfc518894dd9b3db6ca`. Without the AspNetCore FrameworkReference, also needed: Microsoft.Extensions.Caching.Memory (>= 10.0.4), Microsoft.Extensions.Configuration.Abstractions (>= 10.0.4), Microsoft.Extensions.Logging (>= 10.0.4), Microsoft.Extensions.Logging.Abstractions (>= 10.0.0) (and their closure) — so Auth.Data and the runner would take the FrameworkReference.

Needed from TL: approve exact mapping patterns `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Relational`, `Microsoft.EntityFrameworkCore.Abstractions`, `Microsoft.EntityFrameworkCore.Analyzers` (resolved 10.0.4 as transitives; or pin them, e.g. 10.0.14 to match the runtime — TL's call), plus `<FrameworkReference Include="Microsoft.AspNetCore.App"/>` in Auth.Data and the runner.

## TL Decision 2026-11-04 7:58 AM PT — runtime EF approved [TLD; TL is informing Mohanraj]

- The 7:56 ruling replaced only the Design/tool part; runtime EF stays approved.
- Exact mapping patterns `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Relational`, `Microsoft.EntityFrameworkCore.Abstractions`, `Microsoft.EntityFrameworkCore.Analyzers`, all four pinned `[10.0.14]` with explicit PackageReferences in `src/Auth.Data`.
- `<FrameworkReference Include="Microsoft.AspNetCore.App"/>` OK. No `Microsoft.Extensions.*` mapping.
- Redo the empty-cache restore at 10.0.14 and record nupkg md5s and owners (below).
- Anything else outside mapping/trust, or blocked network/egress → STOP and report; never route around.

### Packages (empty-cache restore, 10.0.14)

`dotnet restore` of Auth.Data + Migrator + Checks with `--packages` an empty dir and an empty HTTP cache (`/tmp/sse-llc1/pkgs-empty`, `/tmp/sse-llc1/http-cache-empty`), network fetch (real `GET`s), `signatureValidationMode=require`: **exit 0**, no NU1100/NU3034; NU3018 warnings only. Log `/tmp/sse-llc1/restore-empty-cache.log`.

| Package | Version | nuget.org owners | nupkg md5 |
|---|---|---|---|
| Npgsql | 10.0.3 | brar, ninofloris, Npgsql, roji | 1a1a0bc2517c93a15b285b1983bb4932 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | Npgsql, roji | fe918ee28cb0c0f01991e01d8ab45b37 |
| Microsoft.EntityFrameworkCore | 10.0.14 | aspnet, dotnetframework, EntityFramework, Microsoft | ce78192568e7cb309fc98e6c608b9f9e |
| Microsoft.EntityFrameworkCore.Relational | 10.0.14 | same | 48021783a028cfca47e48853e5720093 |
| Microsoft.EntityFrameworkCore.Abstractions | 10.0.14 | same | d1a1fa91b44466545b3660577e6d1261 |
| Microsoft.EntityFrameworkCore.Analyzers | 10.0.14 | same | 1d6d91d3abab874b08c14a7cbbb31da2 |

NuGet also downloads (graph walk only, not resolved; not in any lock file) `Microsoft.EntityFrameworkCore 10.0.4` (76c7ef3e4908b91594389dd2b392a708) and `.Relational 10.0.4` (c9e2cde1b65745b3845665901511699b), the provider's minimum versions; both are under the same exact ids and pass signature validation.

Lock files (locked mode): `Auth.Data` `6d686c7ecd199bae7b9a16a4c416315f`; `Auth.Data.Migrator` and `Auth.Data.Checks` `86bc4e096214097b5a17db0100f3670c` each. `src/nuget.config` `4b5effba49e326b93bbdb15be720b2d6`.

**NU3018** (revocation server unreachable) stays a warning: the projects keep `TreatWarningsAsErrors` and add `<WarningsNotAsErrors>NU3018</WarningsNotAsErrors>` so it is not promoted to an error. It is **not** in `NoWarn` (still printed: 8 warnings in the build) and `NUGET_CERT_REVOCATION_MODE` is unchanged.

## P2 approach

- **No Design package:** `src/Auth.Data/Migrations/20261104160000_S2aInitial.cs` is a hand-written `[Migration]` (DDL via `migrationBuilder.Sql`, spec §8) and `AuthDbContextModelSnapshot.cs` is hand-kept (ProductVersion 10.0.14). The checks finalise the snapshot as EF's migrator does and assert `IMigrationsModelDiffer.HasDifferences(snapshot, designTimeModel) == false`; `MigrateAsync` would also raise `PendingModelChangesWarning`, which did not fire.
- **Runner** `src/Auth.Data.Migrator`: `update [target|0]` (MigrateAsync), `list`, `script <path>` (`GenerateScript(..., Idempotent)` + header). Reads `CONNECTION_STRING` from env, never prints it.
- **Roles** `src/Auth.Data/tools/create-app-roles.sh <db>` (superuser via `sudo -u postgres`; name must match `^ff_[a-z0-9_]+$`): create-if-missing, then `ALTER ... LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION`; random 48-hex passwords, reused from `/tmp/ff-db/<db>_roles.env` (0600, test-only, never committed). The migration's first statement RAISEs if a role is missing or privileged; the script run with a missing role aborts with nothing applied.
- **Default privileges (spec bug, fixed):** `ALTER DEFAULT PRIVILEGES IN SCHEMA auth REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC` is a silent no-op (per-schema entries can only add to the global defaults). A probe check (owner creates a function, PUBLIC tries EXECUTE) caught it. The migration uses the global per-database form `ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC` (Down restores it); spec §8.3 corrected.
- **ff-db env quoting:** `/workspace/cachd-ones/ff-db/ff-db.sh` (outside the repo) writes `CONNECTION_STRING=Host=...;...` unquoted, so `source`ing it truncates at the first `;`. (An earlier migrator run only worked because Npgsql fell back to the PG* variables.) `src/Auth.Data/tools/env.sh` reads the values with `sed` instead; `ff-db.sh` is not changed (not mine) — flagged for its owner. The roles env file quotes its values.
- **Seed** `db/seed/s2a-test-app-user.sql` (SSE2-FAKE-TEST-ONLY, `ON CONFLICT DO NOTHING`): the 7 `fixtures.json` users; `partner_id` is taken from the user's location's partner, NULL for super_admin. Fake tenants/subjects only (`...00fa-...`, `...00fb-...`), fake ARNs (account 000000000000, FAKE).
- **Readers** (`src/Auth.Data/Readers.cs`, raw Npgsql as `<db>_login`): config reader (location ⋈ config; malformed slug → null without a query; `SecretRef` wrapped in `SecretReference`); `FindUserAsync` with **LEFT JOIN** `app_user` (N3: no row → null; orphan link → `InvalidOperationException`; status via `UserStatusText.Parse`); `IsAssignedAsync` (EXISTS). All throw on DB errors and cancellation.
- **N3 test method:** an orphan link is impossible with the FK, so the check opens an **owner** transaction, drops `external_login_user_fk` (variant: also deletes the `app_user` row), inserts the orphan, runs the reader's own SQL and mapping on that connection/transaction via `internal NpgsqlExternalLoginReader.FindOnConnectionAsync`, asserts the throw, and **rolls back**; a follow-up check asserts the FK and CHECK still exist. Same method for unknown status (CHECK dropped).

## P2 results (`src/Auth.Data/tools/p2-verify.sh`, exit 0; output `/tmp/sse-llc1/run/verify.out`)

- Build with locked restore: 0 errors, 8 warnings (NU3018).
- Runner on `ff_sse`: update → list (applied) → **update 0** (schema, policies, owner default ACL, app-role CONNECT, history all 0) → update.
- Seed twice → 7 rows. `Auth.Data.Checks`: **155 passed, 0 failed** (model/snapshot, columns vs pg_catalog, indexes, roles, grant matrix incl. column grants, functions, default privileges probe, RLS flags/policies/behaviour, 47 constraint cases, EF + raw concurrency, readers, N1/N3, F8 errors/42501/cancellation).
- `db/migrations.sql` generated twice → identical md5 `2417afaadb064ed79f4f55b5b10ee4d3`.
- Fresh `ff_sse`, login role renamed: script aborts ("S2a: app roles … must exist"), schema absent. Roles restored; script applied **twice** (exit 0 both, history 1 row, 9 tables); seed; checks **155 passed, 0 failed** again.
- `ff_sse2` never touched (checks refuse any DB other than `ff_sse`).

## Decisions (this slice)

- Project names: `Login.Abstractions` (mandated by F9). The data project will be `src/Auth.Data` (matches the `auth` schema; shared by the admin API and the login service, so not `Login.*`).
- Roles: per-DB `<db>_admin_api`, `<db>_login`, created by a superuser script (to be `src/Auth.Data/tools/create-app-roles.sh`); the migration grants and RAISEs if they are missing (spec §8.2).
- RLS off on the login tables (F7); delete-to-unassign for `user_location` (F10); `user_location` FK ON DELETE RESTRICT; status/provider mapping throws on unknown values (TLD 7:53).
- OQ-S2a-8 log: no `partners` table and no FK on `partner_id` in S2a (TLD 7:39).

## Assumptions / open

See spec §13 (A-21 server-filled OIDC keep/startup validation; A-22 Entra partner match). Open for P3: validating outbox envelopes against `audit-events.schema.json` in .NET needs a JSON Schema package (none approved).

## Sources (md5)

MK9 `939e41bd0d087a35e1f00688dca53a6c`; SSE2 F-verify `/tmp/sse2-fverify/F-STATUS-VERIFY.md` `70f77c7402918580e5980874f2d2adfd`; SSE2 review @c35863f `536109ccc2115b9c7185ecf08809aa85`; `src/Login.StandIn/fixtures.json` `612918cc3bd6c9453a3031d0a3668b15`.

## Toolchain

.NET SDK 10.0.403 / runtime 10.0.14 at `/home/box/.dotnet`. PostgreSQL 17 (box). PowerShell 7.6.9 installed from packages.microsoft.com debian/13 trixie prod, per TL's OK at 7:25, used for testing db/setup.ps1 only.
