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
- `ff_sse2` never touched (at 7e0350b the checks refused any DB other than `ff_sse`; see P2-2 below for the current guard).

## P2 fixes — SSE2 DB review of 7e0350b (`/tmp/sse2-fverify/F-STATUS-VERIFY.md`, md5 9b3e4f12) + TL Decision 8:20 AM PT

- **P2-1 (Medium, TLD must-fix)**: `<db>_admin_api` had table-level UPDATE on `auth.location_login_config`, so it could change `location_id` (PK), `created_at`, `created_by`. Fixed in a NEW hand-written migration `20261104170000_S2aLlcColumnUpdate` (Up: REVOKE table UPDATE, GRANT UPDATE on the 11 mutable columns only; Down: back to table-level UPDATE). `S2aInitial` is unchanged (SSE2 is merging 7e0350b). Grants are not in the EF model, so the snapshot's model is unchanged (header note only; model-diff check still passes). Spec §8.4 row updated.
  - Other tables, same audit (every column, both roles, now a DB check): `location` admin UPDATE = `name` only (already); `location_login_config_change` and `audit_outbox` admin = no UPDATE (append-only; checked behaviourally); `app_user`, `external_login`, `user_location`, `oidc_replay`, `session` = no admin grant at all; login role column UPDATE = the 7 `session` spec columns only (PK, `user_id`, `created_at` etc. not updatable). **No other immutable-column problem found**; only `location_login_config` needed fixing.
  - New checks (+6): admin UPDATE of `location_id` / `created_at` / `created_by` each denied 42501; all 11 mutable columns allowed (§8.5 statement, rolled back); UPDATE on change table / outbox denied; has_column_privilege none on the 3 columns. The column-grant check now covers every `auth` column x both roles.
- **P2-2 (TLD 8:20)**: checks take the target from `AUTH_CHECKS_DB` (all three connection strings must point at it); safety guard refuses anything except `ff_sse`, `ff_sse2` or `ff_kit_*`. SSE runs only `ff_sse` and `ff_kit_*`; never `ff_sse2`. Guard probes: unset / `postgres` → refuse (exit 2); name ≠ connection strings → refuse (exit 2).
- **P2-3**: spec §8.3 now states the global default-privileges revoke covers every schema in the DB, including functions from extensions the owner installs later.
- **ff-db.sh (TLD 8:20 (3), ours, outside the repo)**: `CONNECTION_STRING` was written unquoted, so `source` cut it at the first `;`. Now every value is single-quoted by a `write_env` function. md5 **823296e08f6cd7cebf7fedf9dd7db8c1 → 7f90790f007bff2767903b523dff4d8e** (backup `/tmp/sse-llc1/p2fix/ff-db.sh.823296e0.bak`). Verified without `rebuild`: `write_env` to a scratch path with a fake password containing `; ' $ backtick` → sourced value equals the full string; then `/tmp/ff-db/ff_sse.env` only regenerated with its existing password (role untouched) → sourced `CONNECTION_STRING` equals the full 114-char string (was `Host=127.0.0.1`). `ff_sse2.env` not rewritten (mtime 07:22). `src/Auth.Data/tools/env.sh` sed workaround **kept** for unquoted (old) files; it can go once every env file is regenerated.
- **EF 10.0.4 downloads**: the stray `10.0.4` EF packages seen in the NuGet cache are cache-only and fine: nothing resolves them (every lock file resolves EF 10.0.14; `10.0.4` appears only as the lower bound of Npgsql.EntityFrameworkCore.PostgreSQL's `[10.0.4, 11.0.0)` dependency range) (TLD 8:20 (4)).
- Verify (`p2-verify.sh`, now resets `ff_sse` with the runner's `update 0` instead of an ff-db.sh rebuild; output `/tmp/sse-llc1/run/verify-p2fix.out`, exit 0): build 0 errors / 8 NU3018 warnings; runner update → both applied → **update 0** (schema, policies, owner default ACL, app-role CONNECT, history all 0) → update; seed twice → 7; checks **161 passed, 0 failed** (was 155); `db/migrations.sql` generated twice → identical **6d2904571ab1b40ecdddc652fb13fdca** (was 2417afaa…); missing role → script aborts, schema absent; script applied twice (exit 0, history 2, 9 tables); checks **161 passed, 0 failed**.
- md5s (P2-4): spec `contract/v1.1-S2a-location-login-config.md` **33b64453709ee581c55d1c0338de145d** at 7e0350b (as SSE2 measured) → **455a2f6bcb44a358d5a1cdf394591693** (§8.3, §8.4 edits); `src/Login.Abstractions/Readers.cs` fd4b6c39b7ba1a928ea348efaf0aa7bb; `src/Auth.Data/Readers.cs` b9c5f243c0fd5493dd9cbc0a99979c4e; `db/seed/s2a-test-app-user.sql` b907854204946f085bbc35f730fb1c8d; `20261104160000_S2aInitial.cs` f3328f2299f2c709182a8c164cd9c20a (unchanged); `20261104170000_S2aLlcColumnUpdate.cs` 7f202d29e8ffcb5de9d8f7ba7f5001cb; snapshot 269f2689… → 06e2f7898051ec84706fccd321b51af7 (comment only); `db/migrations.sql` 6d2904571ab1b40ecdddc652fb13fdca.

## Part A — portable db/ kit (Mohanraj's 7:23 ask; finished 2026-11-04 AM PT, after the P2 fixes so it ships with them)

- `db/setup.sh` and `db/setup.ps1` do the same steps: owner role + DB, app roles `<db>_admin_api` / `<db>_login` (NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION), `db/migrations.sql` (both migrations, incl. P2-1) applied as the owner, optional TEST-ONLY seed (`--seed` / `-Seed`). Passwords via env, SecureString or hidden prompt; never argv, never printed.
- `db/README.md`: dotnet-tools.json / `dotnet ef` references removed (dotnet-ef dropped, 7:56 ruling); added "Run and test" (prerequisites, setup, migrations, seed, checks with `AUTH_CHECKS_DB=ff_kit_*`); `--host` documented as the owner connection host; stale "added when the migration lands" / TODO wording removed.
- **pwsh note: `db/setup.ps1` was tested only with pwsh 7.6.9 on Linux** (packages.microsoft.com debian/13, per TL's OK), as the postgres OS user with `-AdminHost ''` (local socket). **The Windows admin login over TCP (postgres password) is untested**, as are Windows psql discovery and paths.
- End-to-end verify, each from a clean, newly created scratch DB (pre-state 0 DBs / 0 roles), each script run twice (idempotent):
  - `setup.sh --db ff_kit_verify --role ff_kit_verify --seed`: history 2 (S2aInitial, S2aLlcColumnUpdate), auth tables 9, app_user 7, 3 roles non-privileged; checks (`AUTH_CHECKS_DB=ff_kit_verify`) **161 passed, 0 failed**; guard: no `AUTH_CHECKS_DB` → refuse (exit 2); `ff_kit_other` ≠ connection DB → refuse (exit 2).
  - `setup.ps1 -Db ff_kit_verify_ps -Role ff_kit_verify_ps -AdminHost '' -Seed` (pwsh 7.6.9): same counts; checks **161 passed, 0 failed**.
  - Both scratch DBs and their 6 roles dropped afterwards (0 `ff_kit%` DBs / roles left); `ff_sse2` untouched.
  - Scripts/outputs outside the repo: `/tmp/sse-llc1/kit/kit-verify-{sh,ps}.{sh,out}`, `checks-{sh,ps}.out`, `kit-drop.sh`.

## K fixes (SSE2 review of d787f76, `/tmp/sse2-fverify/F-STATUS-VERIFY.md` md5 3956cef917e389e840d5bab4469a3b47; TL Decisions 8:32)

- **K2+K3 (commit 8d1497e)**: the checks read the DB owner from `pg_database` (so `--role` ≠ `--db` passes); they now delete the rows they add (try/finally) and first refuse (rc 3) a DB that is not in the post-seed state. 162 checks.
- **K1+K5 (next commit)**: every DB/role the kit creates carries `COMMENT ... IS 'floorfunds-kit'`. Read-only preflight `db/kit/preflight.sql`; hard refusals (exit 5, before any changing SQL; `--force` never overrides): `postgres` / template DBs, role `postgres` / `pg_*`, any superuser role, any existing unmarked role or DB, a `--role` owning another DB, an existing `--db` not owned by `--role`. Existing kit objects need `--force`; `--drop` needs `--force`. `ALTER DATABASE OWNER` removed; `ALTER ROLE` only on marked non-superusers, re-checked inside `db/kit/role.sql` / `db/kit/database.sql`. K5: newline/CR in a password → exit 2.
- Tests `db/tests/k1-kit-tests.sh` (stub psql for the system DBs and `ff_sse`/`ff_sse2`, live scratch `ff_kit_k1*` objects otherwise, both scripts): 76 passed, 0 failed (`/tmp/sse-llc1/run/k1-tests.out`).

## KF fixes (SSE2 GO review of 8d1497e/3e12b8d, `/tmp/sse2-fverify/F-STATUS-VERIFY.md` md5 1cb425b760c9eb7d27c32082d32cc36e, snapshot `/tmp/F-STATUS-VERIFY.1cb425b7.md`; TL Decision 8:50 AM PT) [TLD]

Branch `fix/db-kit-kf` off 3e12b8d (kept apart from P3). No migration, spec or app code changed.

- **What / why (KF1, Low):** `--force` kept privileged memberships on a marked kit role. `db/kit/preflight.sql` now REFUSEs (exit 5; `--force` never overrides) when a kit role (`--role`, `<db>_admin_api`, `<db>_login`) has `BYPASSRLS`, `CREATEROLE`, `CREATEDB` or `REPLICATION`, or when it is a member, directly or transitively, of any role that is not one of this run's kit-marked roles. That covers `pg_*`, superuser/BYPASSRLS/CREATEROLE/CREATEDB roles, unmarked roles, and another install's marked roles. The same rules are re-checked in `role.sql`.
  - **Decision (attribute list):** SSE2 named BYPASSRLS; TL named BYPASSRLS, CREATEROLE and REPLICATION. I added CREATEDB because the kit creates its roles NOCREATEDB, and the migration's own guard (`db/migrations.sql` line 22) rejects CREATEDB app roles. SUPERUSER was already refused. LOGIN, INHERIT and CONNECTION LIMIT are left alone, since the kit sets LOGIN itself.
  - **Decision (memberships):** I refuse instead of revoking, so the kit never silently undoes another admin's change (K1: never act on what the kit did not create). The kit grants no memberships and `db/migrations.sql` needs none. Membership in this run's own marked kit roles is allowed, because it gives no rights outside the kit.
- **What / why (KF2, Low):** the check and the change were separate statements. Now:
  - `role.sql` runs check+CREATE+COMMENT or check+ALTER as one `DO` block. Values arrive through transaction-local `set_config`, and `\gset` keeps them off stdout.
  - `database.sql` does COMMENT/REVOKE/GRANT after its re-check in one `DO` block. A `DO` pre-check runs before DROP, and the DROP itself is still generated only under the same conditions. A superuser owner of the DB is now refused too.
  - `CREATE DATABASE` and `DROP DATABASE` cannot run in a transaction or a function (PostgreSQL rule), so they cannot be made atomic with their check. That window (a concurrent superuser) is documented in db/README.md. The README sentence "re-check the marker in the same statement they act in" was inaccurate and has been rewritten to match the code.
- **What / why (KF3, Info):** the preflight's last line is always `END floorfunds-kit-preflight`. setup.sh and setup.ps1 refuse with exit 5 when the output doesn't end with it, so empty or truncated output now fails closed.
- **KF5 suggestion:** `db/tests/k1-kit-tests.sh` builds every scratch name from `P` (`KIT_TEST_PREFIX`, default `ff_kit_kf`, validated as `^ff_kit_[a-z0-9_]{1,20}$`). Cleanup uses `PL`, the LIKE-escaped form of `P`. A run with `KIT_TEST_PREFIX=ff_kit_kfm` cleaned up to 0.
- **KF4 (Info), not fixed here:** NU3018 revocation is environmental. README "Known limits" now says to re-check it on a normally networked machine before release, and that setup.ps1 has never been run on Windows.
- **Tests:** `db/tests/k1-kit-tests.sh` passes 134/0 (was 76). The new cases run on both scripts:
  - KF3: empty and truncated preflight output, with --force.
  - KF1 live: 10 membership cases and 4 attribute cases. The memberships are pg_read_all_data, pg_write_server_files, pg_monitor, a BYPASSRLS role, a CREATEROLE role, a CREATEDB role, a marked SUPERUSER role, an unmarked role, another install's marked role, and a transitive case via a kit role. The attributes are BYPASSRLS, CREATEROLE, REPLICATION and CREATEDB. Each is refused with rc 5 and nothing changes (the snapshot now covers memberships and attributes), and after each revert --force passes again.
  - DO-block guards, run directly: a membership, BYPASSRLS, an unmarked DB, a marked DB with a superuser owner (drop=0/1), and atomic create+mark.
  - The DB checks still pass 162/162 on fresh, --force --drop and ps1 scratch DBs.
  - Mutation check: the new tests against the 3e12b8d kit give 79 passed and 51 failed, so they do detect the old behaviour.
- **Other checks:** the spec checker gives v1.1-S2a 201/201 and v1-d4 ALL PASS. Login.Abstractions.Checks passes 52/52, and the Release build has 0 warnings and 0 errors (warm cache). `db/migrations.sql` (6d2904571ab1b40ecdddc652fb13fdca) and the spec (455a2f6bcb44a358d5a1cdf394591693) are unchanged.
- **Open:**
  - The CREATE/DROP DATABASE window can't be closed in Postgres.
  - KF1 doesn't look at roles that were granted membership *in* a kit role (closed later by KF-C, below).
  - setup.ps1 has never run on Windows.
  - NU3018 still needs re-checking on a normally networked machine.

## KF-A/B/C/E (commit after 81affe1): SSE2 NO-GO fixes

- **Source:** SSE2 review `/tmp/sse2-fverify/F-STATUS-VERIFY.md` md5 `382cfd6c34649d9f2f643926b9e6baf5`, snapshotted to `/tmp/F-STATUS-VERIFY.382cfd6c.md`. TL steering at 9:03 (KF-B lock; SCRAM verifier; KF-C in this commit).
- **KF-A, what:** a password leaked through the QUERY/CONTEXT lines when an error happened inside role.sql's `EXECUTE`. Reproduced before the fix: `--role none` (reserved name) printed the password on stderr and in the server log.
  - **Fix:** setup.sh and setup.ps1 now hash the password client-side into a SCRAM-SHA-256 verifier (RFC 5802/7677, 4096 iterations, random 16-byte salt). setup.sh uses perl with Digest::SHA; setup.ps1 uses .NET Rfc2898DeriveBytes.Pbkdf2, HMACSHA256 and SHA256. role.sql refuses any value that is not a verifier.
  - Each `EXECUTE` is wrapped as `EXCEPTION WHEN OTHERS THEN RAISE 'floorfunds-kit: % failed (%)', step, SQLSTATE`, and psql runs with `SHOW_CONTEXT never` and `VERBOSITY terse`.
  - Passwords must be printable ASCII, so SASLprep is the identity.
  - **Server log:** `log_min_error_statement=error`, so the failing statement is logged. After the fix it is the `DO` text, which holds no password and no verifier (checked from a byte offset, read only, through `sudo -u postgres`).
- **KF-B:** role.sql takes `LOCK pg_authid, pg_auth_members IN SHARE MODE`. This works because the kit's admin is a superuser.
  - The race test runs an uncommitted GRANT pg_read_all_data, sleeps 4 s, then commits. The kit waits about 3 s and refuses with rc 5 (sh and ps1).
  - Mutation check: with the LOCK removed, the same test gets rc 0 with the membership present (2 FAIL), so the test detects the race.
  - The README's "only CREATE/DROP remains" text now states that the role race is closed and the DB race is still open.
- **KF-C:** refuses a non-kit role that is a member OF a kit role, in both preflight.sql (line kind 11) and role.sql. Tested in sh, ps1 and the DO block directly.
  - **Conflict:** the Task 2 brief said not to implement KF-C; the 9:03 steering said "KF-C in this commit". I followed the steering.
- **KF-E:** setup.ps1 splits the preflight output on LF only, so `END …\r` is not taken as the end marker, the same as setup.sh. Stub tests cover CRLF and a bare CR.
- **Results:** k1-kit-tests 169/0 (was 134; +35 new). DB checks 162/162 on every scratch run. Spec v1.1-S2a 201/201 and v1-d4 ALL PASS. Abstractions 52/52. Release build 0 warnings and 0 errors. Scratch objects were cleaned up to 0.
- **Open:** the CREATE/DROP DATABASE window. The ALTER-path EXECUTE error is not forced by a test (hard to trigger once the lock is held), but it is wrapped the same way. setup.ps1 has never run on Windows. NU3018.

## TLD 9:19: superuser-only admin connection (commit after 7dff464)

- **Decision:** TL chose option (a): keep the pg_authid/pg_auth_members SHARE lock and require a superuser admin connection.
- **What:** `db/kit/preflight.sql` now emits a first line (kind 0), `REFUSE the admin connection must be a PostgreSQL superuser (e.g. postgres); <current_user> is not.`, whenever `current_user` lacks rolsuper.
  - Both scripts print it as `Refused: …` and exit 5. `--force`/`-Force` doesn't override it.
  - It fires before any change step and before any password is hashed (hashing happens only in the role step).
- **Why:** only a superuser may lock system catalogs. The role checks and changes also assume one, so a CREATEROLE/CREATEDB admin would fail part-way or be weaker.
- **Tests:**
  - Stub case: sh and ps1 each get rc 5 with the message, and the only psql call is the read-only preflight.
  - Live case: a scratch admin `ff_kit_kfadm LOGIN CREATEROLE CREATEDB` runs over TCP, with and without --force --drop, on sh and ps1.
    - Each run gets rc 5 with the message.
    - DBs, roles, memberships and comments are unchanged (snap plus an md5 of pg_shdescription).
    - No `ff_kit_kfna*` object was created, no change step message appeared, and no password or verifier is in the output.
    - The admin role is dropped afterwards.
- **README:** step 0 says the admin must be a superuser and why. The Known limits text now matches.
- **Results:** see the commit message and `/tmp/sse-kf/evidence-kf3.txt`.

## P3 — S2a admin API (Mohanraj 8:44; TL 8:43; on top of 3e12b8d / f986aaf)

- **Projects**: `src/Admin.Api` (Web SDK, `CreateSlimBuilder`; only package `Microsoft.IdentityModel.JsonWebTokens` 8.19.2, already in use and locked; ProjectReference `Auth.Data`) and `src/Admin.Api.Checks` (exe; only framework/project references). Lock files: `Admin.Api/packages.lock.json` and `Admin.Api.Checks/packages.lock.json`, the latter generated once with `dotnet restore Admin.Api.Checks -p:RestoreLockedMode=false` (no new packages; NU3018 not suppressed, revocation mode not changed); builds then run in locked mode.
- **Endpoints** (interim `/admin` host, `aud` = `backoffice-api`): `GET`/`PUT /api/orgs/locations/{location_id}/login-config`, `GET …/login-config/changes`, `POST /api/orgs/locations` (create needs a provider choice). Check order is exactly spec §4: 401 → 403 forbidden (+`not_admin` event on writes) → 404 (+`forbidden_scope` on out-of-scope writes; identical body to a missing location) → 428/400 If-Match → 409 with `current_row_version` → 400 malformed_body → 422 validation_failed `fields[]` (+`tenant_bound_elsewhere`) → 403 not_super_admin (+event) → 400 reason_required → commit. Denied events are written in their own outbox-only transaction after the request transaction is rolled back.
- **K4**: `app.*` GUCs only via `set_config(name, value, true)` (= `SET LOCAL`) inside the transaction, from the validated token identity (never from headers, query or body); parameterised SQL only; audit columns (`updated_by`, `updated_at`, `row_version`, `created_by`, `created_at`) and `partner_id` in a PUT body → 422 `unknown_field`; secret_ref account id and region allowlisted from `SecretRef:AllowedAccountIds` / `SecretRef:AllowedRegions` → 422 `secret_ref_format`, value never echoed; crafted cross-partner tests (spoofed `X-Partner-Id` / `X-Admin-Type` / `X-Forwarded-User` headers, `app.*` query params, SQL-like `display_name`, body `partner_id` / `admin_type`, pooled-connection GUC leak, RLS backstop).
- **Startup refusal** (exit 2, message names the key, never the value): `Entra:Authority` not an https base URL, `Entra:ClientId` not a GUID, `Entra:SecretRef` missing / not an allowlisted Secrets Manager ARN (TLD 9:49, D-51), empty/invalid allowlists, no RSA key at `BackofficeAuth:JwksPath`, schema missing or containing an unknown keyword, and the DB role guard (superuser, BYPASSRLS, or member of an auth object owner → refuse). `oidc_authority` / `oidc_client_id` are server-filled from `Entra:Authority` / `Entra:ClientId`; `secret_ref` is write-only (never selected back, never logged; only exception type and SqlState are logged).
- **Outbox**: every envelope is validated with `JsonSchemaLite` against `contract/audit-events.schema.json` before insert; invalid → 500, nothing committed (fail closed). Cross-checked by `Admin.Api.Checks/crosscheck.py` (Python jsonschema Draft 2020-12 + FormatChecker, date-time by an RFC 3339 regex because the venv has no rfc3339-validator and no package was added) on every emitted event plus 9 mutations each; API responses are validated against `contract/location-login-config.schema.json`.
- **Tests** (`AUTH_CHECKS_DB=ff_sse dotnet run --project Admin.Api.Checks -- <outdir>`, then `crosscheck.py <outdir>`): ports 18130 (API) and 18131 (fail-closed instance) from the 18130–18139 range only; fake ids only (account 000000000000, `FAKE-test-only/…` ARNs, tenants …fa11–fa13). The checks refuse a DB that is not ff_sse / ff_kit_* or whose S2a tables are not empty, and delete their rows in a finally block (tables re-checked empty). Result: **218 passed, 0 failed**; crosscheck 260 docs / 0 disagreements, 82 responses valid. Mutation spot-check (scope check and downgrade gate disabled at runtime) → 7 checks fail as expected; source restored (md5 unchanged).
- **Open questions (not guessed; behaviour chosen is the conservative one)**:
  - OQ-P3-1: org:V reader reachability under the interim host (S2a has no role store; interim directory maps super → Super Admin, partner → Partner Admin). A reader's RLS GUC is set to the location's real partner, decided server-side. Custom roles are ignored (built-in only).
  - OQ-P3-2: no `not_admin` event on create: the envelope needs `scope.location_ids` ≥ 1 without `admin_type`, which a create cannot have. Likewise an update `not_admin` on a missing location writes no event.
  - OQ-P3-3: 500 responses have no body.
  - OQ-P3-4: a failure writing a denied event → 500 (instead of the 4xx).
  - OQ-P3-5: attempted_fields in denied events = the editable key names present in the body (names only).
  - OQ-P3-6: a no-op PUT → 200 with the current view, nothing written, no event, row_version unchanged.
  - OQ-P3-7: changed_fields on create are relative to the column defaults.
  - OQ-P3-8: POST parses the body before the scope check (the partner comes from the body).
  - OQ-P3-9: unknown keys are echoed in `fields[].field` only if they match `^[A-Za-z0-9_.-]{1,64}$`, otherwise `*`; body cap 64 KiB (Kestrel 1 MiB); duplicate JSON keys → 400 malformed_body.

## P3 rulings applied (TL 9:04 AM PT, "TLD 9:04"; on top of 5a2d032)

- **OQ-P3-2 CHANGED**: a refused create is audited. `auth.config.change_denied` (location_login_config branch) now carries a required `details.location_id`: the location on update, JSON `null` on create. `not_admin` on create → event with target `{type: partner, id: <body partner_id>}`, scope `{partner_id, location_ids: []}`, no `admin_type` / `actor_partner_id`. Schema (narrowest): one exemption from the envelope rule "no `admin_type` → `scope.location_ids` ≥ 1", only for change_denied + location_login_config + create + `location_id: null`, and that branch then requires `location_ids: []`; null is refused for every other event (update denials, `auth.provider.changed`, v1 `config` branch; tests in `check_v1_1_s2a.py` §9 and `Admin.Api.Checks` §7c, both validators agree in the crosscheck). A PUT to a missing location: still 404 (admin) / 403 (non-admin), no event.
- **OQ-P3-10 (NEW; approved as implemented, TLD 9:20, below)**: a `not_admin` create whose body yields no valid `partner_id` (malformed / too large / duplicate keys / non-UUID) is still audited (never dropped), with `target.id` and `scope.partner_id` both absent (schema allows this only for create + not_admin, and only both together). Alternative if TL prefers: drop to an error log line instead, or require a readable partner.
- **OQ-P3-8**: POST and PUT decide 401 and 403 before reading the body; the body is then read (64 KiB cap, any read error = no body) only for `attempted_fields` and the create target partner. `Content-Length` > 64 KiB is refused before reading, so Kestrel's 1 MiB limit never surfaces (no 413, no 500); authorised oversize → 400 `malformed_body`.
- **OQ-P3-3**: every response carries a server-generated UUIDv7 request id header (a client value is ignored); 500 has no body; the error log line carries the same id. Header name renamed to `X-Request-ID` at TLD 9:20 (below).
- **OQ-P3-4**: unchanged (fail closed); new test: a refused-create event that cannot be written → 500, nothing saved.
- **OQ-P3-1**: interim OK; **V11-ROLESTORE** logged in spec §12. OQ-P3-5/6/7/9: OK as implemented.
- Tests: `Admin.Api.Checks` **266 passed, 0 failed** (was 218); crosscheck 458 docs / 0 disagreements, 100 responses; `check_v1_1_s2a.py` 238 passed. Mutation spot-check (Content-Length guard off; not_admin create event skipped; correlation header removed on 500; body parsed before the 403) → each caught (1, ≥6, 1 and ≥6 failing checks); source restored, md5 unchanged.

## P3 rulings applied (TL 9:20 AM PT, "TLD 9:20"; on top of 0c640f3)

- **OQ-P3-10 approved as implemented**: a `not_admin` create with no readable body partner is still audited, `target.id` and `scope.partner_id` both left out, create + `not_admin` only. Spec §13: moved from "Still OPEN" to the ruled list; §7.2 label updated. No code or schema change.
- **Header renamed to `X-Request-ID`** everywhere (`LoginConfigApi.RequestIdHeader`), matching frozen v1 `contract/errors.md` L67 (md5 1e97dae4d3da00c429d56c0f2f78464e) and `src/Login.StandIn/Allowlist.cs` L64 (md5 239a78993d177301f6528c68fd9afb62); both verified, neither edited. Value: server-generated UUIDv7; a client `X-Request-ID` (or `X-Correlation-Id`) is ignored. (Note: the frozen stand-in allowlist 403 uses `Guid.NewGuid()`, a v4 value; only the name is shared.)
- **Same value in the audit event**: the request id is carried on `Caller` and written as the envelope `correlation_id` of every event the request writes (`auth.provider.changed` and every `auth.config.change_denied`). Schema unchanged (`correlation_id` stays an optional string). Error log line: `admin-api request failed: {Type} {SqlState} request_id={RequestId}`.
- **Test hook**: `AdminApiHost.BuildAsync` gained an optional `ILoggerProvider? extraLogger` (null in production) so the checks can read the 500 error line.
- Tests (`Admin.Api.Checks` 266 → **275**): client `X-Request-ID` "client-chosen" and a well-formed client UUIDv7 not echoed (header nor event); header = stored event `correlation_id` for a success (create → `auth.provider.changed`), a denial (`forbidden_scope` 404) and a refused create (`not_admin`, `location_id` null); every stored event's `correlation_id` is a UUIDv7 equal to some response's `X-Request-ID`; the 4 fail-closed 500s each have exactly one error log line ending `request_id=<that 500's X-Request-ID>`; no response carried `X-Correlation-Id`. Crosscheck 488 docs / 0 disagreements, 102 responses. `check_v1_1_s2a.py` 238 → **246** (9:20 spec pins, no `X-Correlation` left, OQ-P3-10 not under Still OPEN, errors.md names `X-Request-ID`, `correlation_id` schema unchanged).
- Mutation spot-check (event `correlation_id` ≠ request id; client header echoed; error log with another id) → 4, 3 and 1 failing checks; sources restored (md5 unchanged).
- `rg -i "X-Correlation"` over `src/` and `contract/` (excluding bin/obj): 0 hits in `src/Admin.Api` and in the spec/schema; the only hits are negative tests (`Admin.Api.Checks` sends it and asserts it is never returned; `check_v1_1_s2a.py` asserts the spec has none) and this log.

## P3 rulings applied (TL 9:28 AM PT, "TLD 9:28"; on top of 2e3869d)

- **Input:** SSE2 gave GO on P3 (f986aaf..2e3869d). Review snapshot: `/tmp/sse-llc1/S2A-P3-REVIEW.snapshot.md`, md5 bfaa2efbe4ee6f41cca4447ac36d21ba. It is identical to `/tmp/sse2-fverify/S2A-P3-REVIEW.md` (TL cited bfaa2efb).
- **P3-2 (413):** `ReadBodyAsync` reads at most 64 KiB + 1 and reports `TooLarge` when `Content-Length` or the bytes read exceed the cap. It never reads to the end of an oversize body.
  - Order: 401/403 still come first. POST answers 413 right after the permission check (before scope). PUT answers 413 at §4 step 7, after 404/428/`if_match` 400/409 (P3-3: no change).
  - The 413 response is `{"error":"payload_too_large"}`. The schema `error_code` enum gained this code. Nothing is written: no row and no event.
- **Unread bodies:** handled by `RequestEnvelope` middleware.
  - The per-request Kestrel limit is set to null because every API read is bounded; the global 1 MiB limit is kept.
  - When a body may exist and was not read to EOF, the response carries `Connection: close`. After `next()`, the middleware completes the response, then drains at most 1 MiB within 2 s and swallows read errors. The client therefore receives the status and headers before the close, with no reset.
  - This applies to any unread body, so 401/403/404/405/409 responses with an unread body also close.
- **P3-4 (X-Request-ID):** the same middleware runs before routing and sets the header. Every response carries it, including 405 and an unrouted 404.
  - The value is still a server UUIDv7; client values are ignored. `LoginConfigApi` reads it with `RequestEnvelope.Id(ctx)`, so it stays the audit `correlation_id` and the error-log `request_id`.
- **Backlog (spec §12):** P3-5 is **V11-AUDITRATE** (rate-limit refused-create events). P3-6 is **V11-TIMING** (refused PUT timing difference). Neither is implemented.
- **Tests:** `Admin.Api.Checks` 275 → **298**. The new tests use a raw-socket helper.
  - 413 + `Connection: close` + `X-Request-ID` for: PUT declared 400 KB and 8 MiB; PUT chunked 8 MiB; POST declared 8 MiB; POST chunked 8 MiB and 200 KB; HttpClient chunked 300 KB. Each response is received with no read error.
  - A trickle bound: the client keeps sending a declared 8 MiB; the server closes in under 4 s and reads less than 1 MiB (observed close at about 2.0 s).
  - Nothing written: no events, location count, change rows and row_version are unchanged.
  - Authentication and permission still come first: an unauthenticated chunked 8 MiB request gets 401. An unpermitted PUT declared 8 MiB and an unpermitted POST chunked 8 MiB get 403, each with its `not_admin` event.
  - Other cases: 409 + close (P3-3); a follow-up GET 200.
  - 405 (DELETE/PATCH/POST on mapped paths) and unrouted 404 (`/nope`, a nested unknown path, `/`, and an unrouted POST chunked 8 MiB that also closes). All carry distinct UUIDv7 `X-Request-ID` values and write no events.
- **Other counts:** crosscheck 508 docs (107 valid / 401 invalid), 0 disagreements, 113 responses. `check_v1_1_s2a.py` 246 → **262** (9:28 §11: 413 body valid; spec pins for the 413 row, step 7, middleware, V11-AUDITRATE/V11-TIMING in §12, the review md5). `check_schemas.py` ALL PASS.
- **Mutations** (one change each, sources restored, md5 verified):

  | Mutation | Failing checks |
  |---|---|
  | no 413 on PUT | 3 |
  | no 413 on POST | 7 |
  | no close/drain | 10 |
  | close but no drain | 1 (trickle test only; on loopback the close alone already avoids a reset) |
  | unbounded drain | 1 (trickle) |
  | middleware header removed | 26 |

- **Open (interpretations, not guessed further):**
  - On PUT, steps 3–6 still answer before 413. So an out-of-scope Partner Admin with an oversize body gets 404 and a `forbidden_scope` event with an empty attempted-fields list.
  - `Connection: close` also applies to non-413 responses that leave a body unread.
  - A client writing past 1 MiB or 2 s after the response sees its writes fail once it has the response.
  - No 401/403-vs-413 conflict arose.
  - **TLD 9:49: TL confirmed readings 1–4 above as correct** (no change).

## P3 C1 — TLD 9:49 (D-51: one app registration per deployment; TL 7:40 ruling)

- **secret_ref from deployment config**: new required key `Entra:SecretRef` (DRAFT name, like `Entra:ClientId`) → `AdminApiOptions.EntraSecretRef`. Checked at startup in `Validated()` with the existing K4 rule (Secrets Manager ARN, ≤ 2048, `\z`-anchored, account in `SecretRef:AllowedAccountIds`, region in `SecretRef:AllowedRegions`); missing or bad → `admin-api refused to start: config Entra:SecretRef …`, exit 2, key named, value/account/region never printed. `AdminApiOptions.ToString()` = `[redacted]`.
- **Bodies**: `secret_ref` moved to the server-filled list with `oidc_authority` / `oidc_client_id`: present in a PUT body or create `login_config` with any value (ARN, other account/region, plain string, `null`, number, object, empty) → 422 `field_not_writable`, field name only, nothing written, no event. An Entra create or switch needs only `entra_allowed_tenant_ids`.
- **Server set/keep/clear (A-21 analogue)**: create entra and database→entra write `Entra:SecretRef` (`changed_fields` += `secret_ref`); entra→entra keeps the stored value (no `secret_ref` in `changed_fields`); entra→database clears it (`changed_fields` += `secret_ref`). `secret.rotated_at` stays `max(at)` of change rows naming `secret_ref`.
- **Errors**: `secret_ref_required` removed from the client-facing codes (schema enum, spec §5.2, API); `secret_ref_format` removed too (cannot occur any more; open q3). `field_not_allowed` now only for `database` + tenant.
- **GET**: unchanged shape; Super Admin (and, per current spec, the restricted view) shows only `secret {set, rotated_at}` = MK9 "Microsoft app connection: set / rotated <date>", never the value (open q2).
- **SSE2**: `ISecretResolver` (wt-sse2-entra `src/Login.Entra/Contracts/HostHooks.cs:34`, read only) unchanged; it gets the `SecretReference` from the row via `ILocationLoginConfigReader`, and the row value now comes only from config. No SSE2 code edited.
- **Contract**: spec (TLD 9:49 labels, D-51 cited; §3.0, §3.1, §3.2, §3.3, §5.2, §6, §6.1 access_rules, §9 resolver note, §11 P4 UI rulings recorded, §12 **V11-ACTORNAME**, §13), `location-login-config.schema.json` (secret_ref out of `login_config_write`; `secret_ref_input` → `secret_ref_config` doc-only def; enum; access_rules), `audit-events.schema.json` (changed_field description), `contract/README.md`, `check_v1_1_s2a.py` (262 → 301), `Admin.Api.Checks` (298 → 323).
- **Mutation spot-checks** (restored after each; final run 323/0): `secret_ref` dropped from `ServerFilled` → FAILs (≥ 6 shown, create field_not_writable; output head-limited); startup `SecretRefAllowed(EntraSecretRef)` disabled → FAILs (≥ 6 shown, each startup refusal case); schema copy with `secret_ref` re-added to the write body and `secret_ref_required` in the enum → 18 FAILs in `check_v1_1_s2a.py`.
- **Open (for TL)**: (q1) a changed `Entra:SecretRef` does not rewrite existing entra rows (as A-21) — OK? (q2) hide `secret {set, rotated_at}` from Partner Admin / org:V ("Super Admin GET may show")? (q3) confirm `secret_ref_format` removal. Evidence: `/tmp/sse-llc1/p3f-evidence.txt`.

## P4 — screen 9 UI (`ui/orgs/`) [TLD 2026-11-04 9:49, D-51] (new commits on top of 6a71fbf, rebased onto its TLD 11:57 / 12:04 amend; unpushed)

- **Inputs.** Mockup 09 is the live file, now md5 90d2a8fa (D-103, see P4b; built first from snapshot md5 9dae6040). Theme snapshot `/tmp/sse-llc1/theme.aaae47e3.snapshot.css` (md5 aaae47e3) is copied verbatim to `ui/orgs/theme.css`. Fonts are copied from `ui/login-hub/fonts` (byte-identical). TLD 9:49 C2 and C3 apply. Part A evidence: `/tmp/sse-llc1/p3n-evidence.txt`, md5 75f925843de4cadc1b3b41585f8652b9.
- **Page** `ui/orgs/location-sign-in.html` plus `js/llc-core.mjs` (pure) and `js/llc-page.mjs` (DOM and fetch). There is no inline script or style.
  - States: view; Super Admin edit, including the downgrade sub-state (warning banner, reason field, title "Lower sign-in security: {name}"); partner-edit; create; partner-create at `/orgs/{partner}/locations/new`; view only; saved; errors.
  - The app-shell grid needs a rail, so a plain wrapper is used instead (the chrome stays app-agnostic).
- **Viewer kind comes from the API**, not the host. A `secret` key means Super Admin. A restricted view then asks `/changes`: 200 means Partner Admin; 403 means an org:V reader, who gets the "View only (org:V)" chip and info banner, no history (C2), no form and no card. `data-admin-type` only picks the create layout. OQ-S2a-1 is open.
- **C3.** The partner comes from the URL. A location whose `partner_id` is not the URL org is shown as not found. Partner-create under another partner gets the API's 404.
- **Writes.** A PUT sends exactly the six keys with `If-Match`. A create drops a blank reason. Writes never send `oidc_*`, `secret_ref` or `secret`; e2e asserts this on every captured body.
- **Super Admin card** "Microsoft app connection": authority, client id and "Set" / "Not set". It is read-only, has no date, and there is no secret input. It is not shown on create (open item).
- **History** columns: When (PT), Event (`auth.provider.changed`), Who (`actor_user_id`, V11-ACTORNAME) and Details, with the reason appended.
- **Partner Admin** locks only the downgrade direction, against the saved value (on create, against MFA on and backup off). Locked controls use `aria-disabled` and stay focusable, with MK9 hints. A click on a locked control is prevented.
- **Errors.** These use MK9 copy:
  - 409: the conflict banner with a reload link;
  - 422 `tenant_bound_elsewhere`: the tenant banner and field error;
  - the saved banner.

  Field errors set `aria-invalid` and `aria-describedby`. Everything else uses `js/PROPOSAL-error-copy.mjs`, labelled "SSE PROPOSAL FOR UX, NOT APPROVED" [TLD 9:49, D-51], plus a "Request ID: <X-Request-ID>" line on every error banner.
- **Not decided (TO CONFIRM, Mohanraj).** These are left as labelled open items in `ui/orgs/README.md`:
  - The reason length copy. There is no client-side 10–500 check; the server code is shown.
  - Whether a Partner Admin may undo a downgrade. The page follows the API, which allows it.
  - The test sign-in deferral. There is no button and no gate.
- **Test host (TEST ONLY)** `src/Admin.Api.Checks/UiHost.cs` with `Program.cs --ui-host <dir>`.
  - It runs the harness's real API with the same startup and fake ids.
  - It seeds L1–L4 through the API as the Super Admin and adds L1 and L2 to the org:V reader set.
  - It serves the page under `/as/{persona}/orgs/…` and only `theme.css`, `js/*.mjs` and `fonts/*.ttf` under `/ui/orgs/`, with a traversal guard.
  - It proxies `/as/{persona}/api/…` with that persona's test token.
  - Response headers: CSP `default-src 'none'` with `'self'` for script, style, font, connect and img; `base-uri`, `form-action` and `frame-ancestors` `'none'`; plus XFO DENY, nosniff and no-store.
  - On stdin EOF it prints the owner-read DB summary. The `finally` (via `goto uiHostDone`) then stops the API and empties the tables.
  - The normal checks mode is unchanged: **393/0**.
- **Tests.**
  - `node --test ui/orgs/tests/core.test.mjs`: **7/7**.
  - `node ui/orgs/tests/e2e.mjs ff_sse`: **54/0**. It runs headless Chrome 156 over CDP using node built-ins only. Every host except 127.0.0.1 is unresolvable, the profile is under /tmp, and the CDP port is in 18130–18139.
  - e2e covers: SA view (entra and db); org:V (F-A3, real PG: page, `/changes` 403, restricted view, reader of an unassigned location 404); another partner 404; wrong URL org; anon 401; partner-edit (locks, real mouse and Space, tenant-taken, save); SA downgrade (reason_required, then reason_length, then saved); conflict and reload; SA create; partner-create (own partner, partner B 404, org:V 403); no CSP, console or exception errors; no outside request.
  - Real-PG summary after the run: no change row by the org:V reader; SA 6 and PA 3; 6 locations.
- **e2e mutations** (restored by `cp`, md5 re-checked):
  - History shown for viewonly: caught (1).
  - Partner Admin MFA lock removed: caught (3). The first try caught only 2, because the click-then-Space check toggled the box twice. The check was split so each input is asserted separately.
  - Inline style in the page: caught by the CSP check (1).
- **Spec.**
  - §11 heading and the P4 bullet now say "built in P4 from MK9 90d2a8fa" (D-103; first built from 9dae6040), with a "Built in P4" summary.
  - The §13 Sources review snapshot moved from bfaa2efb to `/tmp/sse-llc1/S2A-P3-REVIEW.c6f7258b.snapshot.md` (md5 c6f7258b). That file equals the original plus SSE2's prepended 9:44/9:48 delta review (final GO); the diff is a pure 41-line prepend.
  - Checker pins: L328 is now c6f7258b. L391 now requires c6f7258b and no full bfaa2efb, and a new §11 pin was added.
  - Checker results: **398/0**; v1-d4 ALL PASS (VENV=/tmp/sse-d0/venv, nothing installed).
- **IVT finding (Part A, recorded here).** The `InternalsVisibleTo` evidence count of 2 is check-name strings only. There are 0 real attributes.
- **Not touched / noted.**
  - frozen files (errors.md 1e97dae4, Allowlist.cs 239a7899, root README 16b8b071, db/, Login.Abstractions);
  - allowlist.db e5384097;
  - SSE2's root docs/, tests/ and tools/.
  - Noted only: `wt-sse2-entra` has an untracked shellcheck_py-0.11.0.1 wheel. It is SSE2's and was left alone.
- **Evidence:** `/tmp/sse-llc1/p4a-evidence.txt` (new file).

### P4b — TLD 11:57 (P4 items), D-102 (UX error copy), D-103 (MK9 90d2a8fa) [TLD 2026-11-04 11:57] (fixups folded into the P4 commits; unpushed)

- **D-103, MK9 90d2a8fa.** Source `/workspace/cachd-ones/login-hub-mockups/09-location-method.html` (the file 9dae6040 was taken from), md5 90d2a8fa0052d5496f8fc4c274bc779c, snapshot `/tmp/sse-llc1/09-location-method.90d2a8fa.snapshot.html`.
  - `diff` against the 9dae6040 snapshot: 2 changed lines, nothing else. (1) the "Microsoft app connection" card's `data-show` loses `create`; (2) its design note adds "Not shown in create: the card appears in edit once the location exists (TLD 11:57 AM PT, V11-CREATE-SECRETCARD)" and cites TLD 11:57 / D-103. theme.css in that folder is still aaae47e3.
  - Every pin moved to 90d2a8fa: page header comment, `llc-core.mjs` header, `ui/orgs/README.md`, the spec (Sources row, header, §3, §4, §11 heading and bullets; the 939e41bd and 9dae6040 mentions left are "replaces / was" history only), this log, and the checker pin.
- **TLD 11:57 (P4).**
  - No Microsoft card on create. It appears in the view and edit states once the location exists. e2e: the SA create state has no card; after create the saved view shows it ("Not set" for a database location), and so does the edit view. Backlog **V11-CREATE-SECRETCARD** (spec §12).
  - Partner Admin chip: "Partner Admin: <partner id>" in mono (MK9 draws "Partner Admin: Example Partner"; no API gives the name), like the actor ids. The id is the viewed location's `partner_id`, or the URL partner on create. e2e checks it on view and partner-create. Backlog **V11-PARTNERNAME** (spec §12).
  - OQ-S2a-13 stays open and waits on OQ-S2a-1. No location list is built. The TO CONFIRM items are left undecided.
- **D-102 (UX error-copy review** `/workspace/cachd-ones/reviews/sse-error-copy-review.md`, md5 8c7d5274, snapshot `/tmp/sse-llc1/sse-error-copy-review.8c7d5274.snapshot.md`): proposal 9c71950f approved with exactly three changes.
  - `git mv ui/orgs/js/PROPOSAL-error-copy.mjs ui/orgs/js/error-copy.mjs` (history kept), header "Approved D-102 (UX)". Imports updated in `llc-page.mjs` and `core.test.mjs`.
  - `not_found.text` = "It may have been removed, or you may not have access to it."; `not_super_admin` = "Only a Super Admin can make this change." / "Nothing was saved. Turning off the second step or allowing backup sign-in needs a Super Admin."; `FIELD.reason_length` = "This reason is too short or too long. Change it, then try again." with the TO CONFIRM comment (becomes "Enter a reason of 10 to 500 characters." once Mohanraj confirms). All other copy unchanged. README notes reason_length is still TO CONFIRM.
  - Request ID line: new `ui/orgs/orgs.css` (theme.css stays verbatim), class `.request-id`: own last line (block) of the banner, small, #6b6b7b, `user-select: text`, `white-space: normal`, `overflow-wrap: anywhere`, `text-overflow: clip`. The element carries `hidden` and is shown only when there is an id. It is in the error, 409 conflict and 422 tenant banners only, never in field errors. UiHost (TEST ONLY) now also serves `orgs.css`.
- **e2e** (`node ui/orgs/tests/e2e.mjs ff_sse`): **67/0** (was 54). New: the three D-102 strings (404 page, a Partner Admin MFA-off PUT made by unlocking the box in the page so the server's 403 shows, the short reason); the Request ID line on the not_found, validation, not_super_admin, 409 and tenant banners (visible, UUIDv7, `display: block`, below the title, last child, colour rgb(107, 107, 123), not `user-select: none` and a real selection returns the whole text, no ellipsis, no nowrap, scrollWidth <= clientWidth); absent (hidden, empty) on a route error with no request; cleared after a success; never under a field; the create card and the chip. Unit tests **7/7** (D-102 strings asserted).
- **e2e mutations** (restored by `cp`, md5 re-checked): history shown for viewonly: caught (1); Partner Admin MFA lock removed: caught (8); inline style (CSP): caught (1); `.request-id` ellipsis + nowrap + overflow hidden: caught (5); `.request-id` `user-select: none`: caught (5). A first, weaker history mutation (adding `viewonly` to the role test) was not caught, because the null-history guard still hides the card; it is kept in the evidence as mut1a.
- **Spec checker:** **413/0** at P4b, **439/0** on the final tree after the P3 TLD 11:57 / 12:04 amend added 9 pins, the TLD 12:33 amend 8 and the TLD 13:15 KF-A amend [KF-A] 9 (+15 at P4b: D-103 source pin and no live 939e41bd / 9dae6040 pin, D-102 §11 pins, §12 V11-PARTNERNAME / V11-CREATE-SECRETCARD, Sources rows); v1-d4 ALL PASS.
- **Evidence:** `/tmp/sse-llc1/p4b-evidence.txt` (new file; p4a left as it was).
- **Evidence after the TLD 13:15 KF-A P3 amend [TLD 2026-11-04 13:15] [KF-A]:** `/tmp/sse-llc1/p4d-evidence.txt` at the rebased P4 HEAD (new file; p4a–p4c left as they were; P3 evidence `/tmp/sse-llc1/p3q-evidence.txt`).
- **Evidence after the TLD 13:54 P3 amend [TLD 2026-11-04 13:54]:** `/tmp/sse-llc1/p4e-evidence.txt` at the rebased P4 HEAD (new file; p4a–p4d left as they were; P3 evidence `/tmp/sse-llc1/p3r-evidence.txt`). From p4e on, nothing on 5432 is inspected with sudo: `ff_scratch_*` leftovers are counted as `ff_scratchadm`, `ff_sse` tables as the `ff_sse` owner role [TLD 13:54 item 3].

### P4c — live XSS e2e (SSE2 GO on P3 9c0967a / P4 98605e5, review `/tmp/sse2-fverify/RANGE-9c0967a-98605e5-P4-REVIEW.md` md5 1ec73d3829399efce066146f369045a1, snapshot `/tmp/sse-llc1/RANGE-9c0967a-98605e5-P4-REVIEW.1ec73d38.snapshot.md`) [SSE2 1ec73d38] [TLD 2026-11-04 14:18] (one new test-only commit on top of 1a42e66; unpushed)
- **Why:** SSE2's XSS PASS was a code read only [SSE2 1ec73d38 Info]; this makes it a live check. SSE2's GO names 9c0967a / 98605e5; the local P3 is now 435b539 (TLD 13:54 amend) and P4 is rebased to 1a42e66, so the GO is on the earlier SHAs (TL to confirm it carries over).
- **Seed (`UiHost.cs`, `--ui-host` only):** L5 is created through the API (Super Admin, Partner A) with the payloads `<img src=x onerror=window.__xss=1>`, `<script>window.__xss=1</script>`, `"><svg onload=window.__xss=1>` and `javascript:window.__xss=1` in the location name, the display_name and the create reason. All three are accepted (they are trimmed text within length). The org:V reader can see L5.
- **Refused payloads (validation unchanged):** each is 422. A javascript: URL as a tenant id gives `entra_allowed_tenant_ids/0:tenant_id_format`. `<img onerror>` as a tenant id gives the same. `<svg onload>` as partner_id gives `partner_id:partner_id_format`. A javascript: URL as oidc_authority (server config, the only link-like field) gives `login_config/oidc_authority:field_not_writable`. `<script>` as provider_kind gives `provider_kind_invalid`. No API field is rendered as a link (the only `<a>` elements are skip-link, conflict-reload and #tenant, all static).
- **e2e step "XSS hostile API text":** covers the Super Admin view and edit, the Partner Admin view, edit and save (the display name set to the javascript: URL), the org:V view-only page, and the change history (the reason cell).
  - It asserts `window.__xss` is undefined, and that #title, document.title, #bc-location, #saved-loc, the history reason and the #dname value are literal (textContent / value).
  - It asserts that img/svg/iframe stay at 0, script stays at 1 and the anchors at the 3 static ones, with no `on*` attribute and no `javascript:` attribute value.
  - It asserts there are no CSP violations, exceptions or console errors/warnings.
- **TLD 14:18 (A), stored byte-for-byte:** the UIHOST-DB summary reads these as UTF-8 hex through the ff_sse owner/test role (no sudo) and the e2e compares them to the seeded strings:
  - `auth.location.name`;
  - `location_login_config.display_name`;
  - `location_login_config_change.reason`;
  - the `auth.audit_outbox` `auth.provider.changed` envelope `details.reason`.
  - It also checks that 0 rows contain an HTML entity, so the page is the only place the text is encoded.
  - The location name and display_name are not copied into the change or outbox rows: the audit state holds only provider_kind, tenants, fallback and MFA. (A) is therefore checked on the reason in both audit stores, and on the name and display_name in their own tables.
- **DB counts updated:** SA 7 / PA 4 change rows, 7 locations.
- **Mutations (`/tmp/sse-llc1/run/p4f-mut.sh`; each one restored by cp and its md5 compared):**
  - mut6 switches the history cells to `innerHTML`. Caught: extra img/svg/script, the reason not literal, and a CSP inline-handler violation.
  - mut7 adds server-side `WebUtility.HtmlEncode` on the location name. Caught: the name is not literal in the title or breadcrumb, the stored hex differs, and an entity row appears. The project is rebuilt around it.
  - mut1a–mut5 are rerun.
- **Evidence:** `/tmp/sse-llc1/p4f-evidence.txt` at the new HEAD (new file; p4a–p4e left as they were). No sudo: `ff_scratch_*` leftovers are counted as `ff_scratchadm`.
- **P3 designed sudo checks re-run [TLD 2026-11-04 14:18 (B)]:** `/tmp/sse-llc1/p3s-evidence.txt`, produced at 435b539 in a detached worktree and labelled CURRENT. It runs k1-kit-tests.sh (new prefix), M(b), KF-A (c) and the server-log read, with `sudo -u postgres` only inside those 4. For those 4 it supersedes the "past" label in p3r; p3r is left untouched.
- **Backlog [SSE2 1ec73d38 Low]:** re-review CSRF once the cookie-auth `/admin` host lands (OQ-S2a-1). This XSS step does not test CSRF.
- **Open:** the reason_length copy is still TO CONFIRM [SSE2 1ec73d38 Info].

### P4d — SSE2 Lows L1–L5 on the XSS e2e (SSE2 GO on ac0697e, review `/tmp/sse2-fverify/P4-ac0697e-XSS-REVIEW.md` md5 5842c444888d884aa51f944c2dc52d38, snapshot `/tmp/sse-llc1/P4-ac0697e-XSS-REVIEW.5842c444.snapshot.md`) [SSE2 5842c444] [TLD 2026-11-04 14:41] (one new test-only commit on top of ac0697e; unpushed; product code unchanged)
- **L1 (display name byte-for-byte):** a new seed L6 (Partner A, org:V can read it) keeps a hostile display_name that nobody saves. The SA and PA edit forms assert `#dname.value` and `#lname.value` equal the seeded text exactly, and UIHOST-DB checks L6's display_name hex. L5's display_name is still overwritten by the PA save, as before.
- **L2 (javascript: in name and reason):** L6's name and reason both start with `javascript:window.__xss=1`. The DOM scan gains `attrX`: any attribute whose name or value carries `__xss` (href, title, aria-label, src …), on top of the `javascript:` attribute scan and the anchor count. mut8 (name in a breadcrumb `<a href title>`) and mut8b (name in `aria-label`, no new link) both fail on the attribute scans, so the anchor count is not the only detector.
- **L3 (entity check):** the global "no HTML entity in any row" SQL check is removed (it would fail on a user who types `&lt;`). L6 carries typed entities (`&lt;b&gt;`, `&amp;amp;`, `&#60;svg&#62;`) in name, display_name and reason. Per field, the stored text must hold exactly as many entities as were sent (no server encoding, no decoding), the hex must match, and the page must show them as typed.
- **L4 (exact refusal):** each XSS probe asserts its exact `field:code` list (as observed in p4f), not "422 with ≥ 1 field". mut11 (partner_id_format renamed) is caught only by this.
- **L5 (hostile length / trim probes, observed, not a spec guess):** 29 SA creates on Partner B; the e2e asserts each status and field:code, and for a 201 that name / display_name / change reason / outbox reason are stored as the sent text trimmed, byte-for-byte. DB counts are now 8 + accepted probes (SA changes and locations).
  - name: 200 runes accepted, 201 refused (`name:name_invalid`); 200 astral runes (400 UTF-16 units) accepted, 201 refused, so the limit is in code points. Padding of space / tab / LF / NBSP / U+3000 is trimmed. U+200B is not trimmed and is stored. Whitespace-only, empty and an interior newline are refused (`name_invalid`).
  - display_name: 100 runes accepted (also 100 astral), 101 refused (`login_config/display_name:display_name_invalid`); padding trimmed; whitespace-only refused; an interior tab is accepted.
  - reason: trimmed 9 runes refused, 10 accepted, 500 accepted, 501 refused (`login_config/reason:reason_length`); raw 2000 (10 + 1990 spaces) accepted and stored trimmed, raw 2001 refused; whitespace-only and "" accepted and stored as null (no reason); interior newlines accepted and kept.
  - **Product bug (flagged, not fixed):** a reason of 251–500 astral characters (code points ≤ 500, UTF-16 units > 500) passes the API check but fails the outbox envelope schema (`auth.provider.changed` details.reason pattern `[\s\S]{8,498}` is matched per UTF-16 unit by .NET Regex, while maxLength counts code points). The request fails closed with an empty 500 and nothing is stored. 250 astral (500 units) is accepted. The e2e pins this observed 500 so a fix will show up as a changed assertion.
- **UIHOST-DB:** `html_entity_rows` removed; adds `xss.l6` (name, display_name, change and outbox reason hex) and `xss.len` (the same per accepted probe).
- **Mutations (`/tmp/sse-llc1/run/p4g-mut.sh`; each one restored by cp and its md5 compared):** mut1a–mut7 rerun; new mut8 / mut8b (L2), mut9 (`#dname` decodes `&lt;`; L1/L3), mut10 (server-side HtmlDecode of the reason; L3), mut11 (refusal code renamed; L4), mut12 (name Trim narrowed to ' '), mut13 (display_name limit 101), mut14 (name length in UTF-16 units) (L5).
- **Evidence:** `/tmp/sse-llc1/p4g-evidence.txt`, produced at this commit in a detached worktree (new file; p4f and older left as they were). No sudo: `ff_scratch_*` leftovers are counted as `ff_scratchadm`.
- **Open:** whether a whitespace-only reason should be refused (`reason_length`) rather than read as "no reason"; whether U+200B / other invisible characters should be refused in name and display_name; the name refuses an interior newline but the display_name accepts an interior tab (TO CONFIRM, product unchanged).

### P4e — F-ASTRAL fix, B1 blank-reason probe, OQ-S2a-14 / OQ-S2a-15 [TLD 2026-11-04 15:03] [TLD 2026-11-04 15:10] (one new commit on top of a6f1b47, which was pushed after SSE2 GO 85138cd5; this commit is unpushed; contract/ untouched)
- **F-ASTRAL [TLD 2026-11-04 15:03] (fixes the P4d product bug above):** `JsonSchemaLite` now evaluates `pattern` over code points, as it already did for `minLength` / `maxLength`. Before the match, each surrogate pair becomes ONE stand-in, U+FFFF. U+FFFF is a noncharacter that is not `\s`, so `\S`, `.`, `[\s\S]` and negated classes count one astral character as one character. Lone surrogates are kept as they are. Strings without a surrogate pair are matched unchanged (fast path). The schemas are not edited: `contract/` stays frozen (contract v1; `audit-events.schema.json` as it is).
  - **Fail closed:** at load, a schema pattern that names a surrogate, U+FFFF, `\uD8xx`–`\uDFxx`, `\uFFFF` or `\p{Cs}` is refused with InvalidOperationException, because the stand-in would make such a pattern wrong. No pattern in `contract/` hits this guard.
  - **Unit [TLD 2026-11-04 15:10 (1)]:** Unicode code points (runes), per spec L131: "A non-blank reason must be **10–500 characters after trimming**" [TLD 2026-11-04 7:39, OQ-S2a-7]. The API already counted runes, and the envelope now does too.
  - **Both envelope paths are covered:** the change path is `auth.provider.changed` details.reason (audit-events.schema.json L374: `maxLength` 500 plus the pattern). The downgrade path is `downgrade_rule` (L393: `minLength` 10, `maxLength` 500, pattern `^\s*\S[\s\S]{8,498}\S\s*$`). Both are read only. The downgrade path is probed on an MFA-off update (true → false) and an MFA-off create (vs "none", A-5), for each case: BMP 10 / 500 → 2xx, BMP 9 / 501 → 422; 249 astral + 3 ASCII (252 code points, 501 UTF-16 units; the case SSE2 found returning 500) → 2xx; 251 / 300 / 500 astral and 499 BMP + 1 astral → 2xx; 501 astral → 422. Every 2xx stores the trimmed text byte for byte in the change row, the history and the outbox `details.reason`.
  - **Result:** reasons of 249 astral + 3 ASCII, 251, 300 and 500 astral characters (and 499 BMP + 1 astral) → 200 on PUT and 201 on create. The history `items[0].reason`, the change row and the outbox `details.reason` equal the sent text trimmed, byte for byte (UTF-8 hex). 501 astral → the API's own 422 `reason:reason_length` (create: `login_config/reason:reason_length`). BMP 9 → 422, 10 → 200, 500 → 200, 501 → 422, as before.
  - **No 500:** section 7g of the main API run produces no Error log line and no InvalidOperationException out of the outbox (main host `CaptureLogger`). The crosscheck (Python jsonschema, code points) still has 0 disagreements over the larger corpus.
  - **e2e L5:** the astral reason probes 251 and 500 change from 500 to 201, and new 300 → 201 and 501 → 422 `login_config/reason:reason_length` probes are added. "failed-closed probes stored nothing" becomes "no probe failed with a 500, and every refused probe stored nothing".
- **B1 [TLD 2026-11-04 15:10 (2), replacing the 15:03 B1 rules] (probed first; behaviour already matches, so tests only, no product change):**
  - **Rule:** spec L131 says a blank reason (absent, null, whitespace-only) counts as no reason. So a plain create (MFA on, fallback off) with a missing, `""` or whitespace-only reason → 201 with reason null, and that is CORRECT (tested). A create that is a downgrade (MFA off or fallback on; A-5) and a PUT that downgrades must return `400 reason_required`.
  - **Where A-5 is defined:** spec L721 "A-5 create with MFA off / fallback on = downgrade (approved)"; also L261 step 9 "on create vs "none", A-5" and L663 "the reason is required only if the create turns MFA off or fallback on (A-5)".
  - **Where reason_required is defined:** spec L262 (step 10), L286 (error table); `contract/location-login-config.schema.json` L65 (`reason` description) and L515 (the error-code enum).
  - **DEC text (in the spec `contract/v1.1-S2a-location-login-config.md`, read only):**
    - Decision table L23: `| DEC-096 | Mohanraj, TL chat t145u, Wed Nov 4 2026 7:20 AM PT (LD L178/L180, row 159 L761) | | no four-eyes; applies immediately; no change_requested; fallback→true Super Admin only with a reason |`.
    - Decision table L24: `| DEC-098 | Mohanraj, TL chat t148u, 7:22 AM PT (LD L180, row 160 L762) | | "turning MFA off for a location should also be Super Admin only, with a reason, same as the password fallback." |`.
    - L6 label rule: Mohanraj's words cover only DEC-096 and DEC-098. The 10–500 length and the blank-means-no-reason rule are TL decisions (L131, TLD 7:39, OQ-S2a-7), not his words.
    - Step 10 (L262) and the error table (L286): a downgrade with a blank reason → `400 reason_required`, no event.
  - **Probe (Admin.Api.Checks 7g, Super Admin):** 8 blank forms were tried: absent, null, `""`, spaces, tab/CR/LF, NBSP ×12, U+3000 ×12, and a U+2028 / U+2029 / U+0085 mix. Each was tried on 6 required-reason actions: create with MFA off, create with fallback on, create with both, update with MFA off, update with fallback on, and update with both. Every one → `400 reason_required`, with no event, no change row, no new location and row_version unchanged. A Partner Admin downgrade is 403 `not_super_admin` at step 9, before the reason rule.
  - **Where blank is stored as null:** only where the reason is optional, that is, an update that is not a downgrade (here a display_name change; all 8 forms → 200, reason null in the change row and event) and a create with MFA on and fallback off (whitespace → 201, reason null). This matches spec L131. **No code change.**
  - **Note (15:03 wording, superseded by 15:10):** the brief's wording "existing reason 422" does not match what the spec and code return for a blank reason on a required action, which is `400 reason_required`. `422 reason_length` applies to a non-blank reason outside 10–500. Nothing was changed.
  - **Observed (open, not changed):** 10 × U+200B (not whitespace, so .NET `Trim` keeps it) on an MFA-off update → 200. An invisible reason therefore satisfies a required reason. The same applies to U+FEFF and other format characters. Pending Mohanraj via TL.
- **B2 / OQ-S2a-14 (pending Mohanraj via TL; no code change):** leading or trailing U+200B is not trimmed and is stored. name `\u200B…\u200B` → 201, stored hex `e2808b…e2808b`. display_name `\u200BCasino ZW\u200B` → 200, stored hex `e2808b436173696e6f205a57e2808b`. The e2e L5 `name: zero-width space padding (U+200B)` probe also stores it byte for byte.
- **B3 / OQ-S2a-15 (pending Mohanraj via TL; no code change):** name refuses an interior newline (422 `name:name_invalid`), and display_name accepts an interior tab (200; the e2e L5 probe 201, stored `3c737667096f6e6c…`). The 7g probe also shows that the name accepts an interior tab (201) and display_name refuses an interior newline (422). Both fields follow the same rule: interior tab yes, interior newline no.
- **Mutations (`/tmp/sse-llc1/run/p4h-mut.sh`; each restored by cp and md5 compare):**
  - mut1a–mut14 rerun.
  - mut15: `pattern` matched over UTF-16 again (F-ASTRAL reverted).
  - mut15b: the surrogate / U+FFFF pattern guard removed.
  - mut16: PUT `reason_required` check removed.
  - mut16b: create `reason_required` check removed.
  - mut17: a blank reason is kept as "" instead of null.
  - mut15–mut17 are run against the e2e and Admin.Api.Checks.
- **Evidence:** `/tmp/sse-llc1/p4h-evidence.txt`, produced at this commit in a detached worktree. It is a new file; p4g and older are left as they were. No sudo: `ff_scratch_*` leftovers are counted as `ff_scratchadm`.

### P4f — F-SURR: unpaired / reversed surrogate escapes → 400 malformed_body [SSE2 9b3b546d] [TLD 2026-11-04 15:58] (one new commit on top of a04cb82, which is under SSE2 review (CONDITIONAL GO, `/tmp/sse2-fverify/P4-a04cb82-ASTRAL-REVIEW.md` md5 9b3b546d, raw `raw-probes.jsonl` md5 6333a916); unpushed; contract/ untouched)
- **F-SURR (SSE2 9b3b546d finding 1, MEDIUM, already present in a6f1b47):** `JsonDocument.Parse` accepts an unpaired or reversed surrogate escape (`"\ud83d"`, `"\ude00\ud83d"`, a trailing high surrogate). `GetString()` / `JsonProperty.Name` then threw InvalidOperationException (`LoginConfigInput.cs` L39/72/82/128/156, `HasDuplicateKeys`, `AttemptedFields`), and `LoginConfigApi.ParseObject` caught only JsonException, so the answer was an empty-body 500 with 0 rows written (it failed closed).
  - **Fix (product, `LoginConfigApi.cs` only):** `ParseObject` now decodes every string value and every property name once, right after the parse (`StringsDecodable`). A string that cannot be decoded makes the body malformed, so the caller answers with the existing step-7 400. The `catch (InvalidOperationException)` wraps only those decode calls, not the handler [TLD 15:58]. Every later `GetString` / `Name` sees only bodies that passed. That covers both S2a write endpoints (create and PUT) and the refused-caller paths that read the body only to name attempted fields (they still answer 403 + one not_admin event, with no attempted fields because the body is unreadable).
  - **Code: `400 malformed_body`**, the contract's only malformed-body 400: spec v1.1-S2a §4 step 7 L259 ("valid JSON object, no duplicate keys … else 400 `malformed_body`; no event"), error table L283 (`| 400 | malformed_body | step 7 | none |`), `location-login-config.schema.json` error enum L510. No new code or copy; the body is exactly `{"error":"malformed_body"}`, as for the existing non-object / duplicate-key cases.
  - **Tests (Admin.Api.Checks 7h):** lone high, lone low, reversed pair and trailing high in every string field read — create: `partner_id`, `name`, `login_config/{provider_kind, display_name, entra_allowed_tenant_ids/0, reason}`; PUT: `provider_kind`, `display_name`, `entra_allowed_tenant_ids/0`, `reason` — plus a key name and the never-read `secret_ref`, each as a plain create, a downgrade create (MFA off + reason, A-5), a plain PUT and a downgrade PUT (MFA off): 112 probes, every one exactly 400 `malformed_body` with no field list, 0 rows (location, location_login_config, change), row_version unchanged, no event. not_admin create / PUT with a surrogate key: 403 + one valid not_admin event (8). Controls: a VALID escaped pair still → 201 / 200, stored byte-for-byte. No Error log line and no 500 in 7h.
  - **mut18:** `catch (InvalidOperationException)` → `catch (JsonException)` in `StringsDecodable` (the catch reverted); caught by 7h (500s).
- **SSE2 9b3b546d finding 2 (INFO), added:** 9 astral (→ 422 reason_length, nothing written) and 10 astral (→ 2xx byte-for-byte) on the downgrade path, in `downCases` (update and create).
- **Known items, no change (SSE2 9b3b546d findings 3–5, INFO):**
  - The F-ASTRAL load guard does not see octal escapes (`\0nnn`), `\p{Cn}` or similar category forms that could name a surrogate or U+FFFF; it also refuses a literal backslash-u `\\uFFFF` (over-refusal, fails closed). No schema in `contract/` uses any of these.
  - .NET `$` matches before a final `\n`, so `^\S(?:.*\S)?$` accepts `"ab\n"`. The API trims first, and its own regexes end in `\z`.
  - The claims and openid-configuration schemas do not load in JsonSchemaLite ("unsupported format 'uri'"); the Admin API does not use them.
- Evidence: `/tmp/sse-llc1/p4i-evidence.txt` (full suites + mut1a–mut18 at the new commit, detached worktree). Older evidence untouched.

## P3 follow-up — TLD 11:57 o1–o3 + TLD 12:04 F1 + SSE2 de9c1946 F2 [TLD 2026-11-04 11:57] [TLD 2026-11-04 12:04] (third amend of the unpushed 6a71fbf)

Review: `/tmp/sse2-fverify/RANGE-06dc8ac-6a71fbf-REVIEW.md` md5 de9c1946 (snapshot `/tmp/sse-llc1/RANGE-06dc8ac-6a71fbf-REVIEW.de9c1946.snapshot.md`). Evidence: `/tmp/sse-llc1/p3o-evidence.txt` (new file; p3f, p3h–p3n untouched).

- **o1 [TLD 11:57]:** the runid is `ff_scratch_<UTC yyyyMMddHHmmss><6 hex>` (`ScratchDb.NewName` / `RunIdUtc`). The start-up sweep (`SweepAsync`) skips this run's own DB, keeps a marked DB with no parsable timestamp, age ≤ 1 h or any `pg_stat_activity` connection (55006 on DROP also counts as kept), and drops the rest. It also drops orphan marked `ff_scratch_*_(admin_api|login)` roles whose DB is gone. The name is generated before the sweep and passed to it, so the sweep can never drop the DB this run is about to create; `scratchName` is set only after CREATE + COMMENT succeed, so the finally never drops a DB this run did not make.
- **o2 [TLD 11:57]:** an unmarked (or not-ours) `ff_scratch_*` DB is never dropped: the sweep prints its name and `FATAL scratch-sweep: unmarked …`, and the main run then refuses to set up the scratch DB, so N3 / N1 / Q2 FAIL (the run fails loudly). `--scratch-sweep` alone exits 3. If CREATE works but COMMENT fails, the run throws `FATAL scratch: created X but could not mark it; left in place UNMARKED`.
- **o3 [TLD 11:57] — NOT fully met; TL to decide.** No new cluster-level object is left behind by a run: `ClusterSnapshotAsync` (roles + attributes + comments, `pg_auth_members`, tablespaces, databases with owner/flags/ACL/comment, `pg_db_role_setting`, `pg_shdescription` counts) is taken before CREATE and after DROP and Q2 requires them to be identical (the diff is printed if not). But during a run the frozen migration (`db/` is not mine to change) needs the per-DB roles `<db>_admin_api` and `<db>_login` (LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE), so per-run marked roles are still created and dropped with the DB. The kit roles (`ff_sse_*`) cannot be reused without the migration targeting `ff_sse`'s role names, which would mean touching `db/` or widening. Also `ff_scratchadm` (role, created by me at P3 via `scratch-admin-setup.sh`) is itself a persistent cluster-level object. Options for TL: (A) keep `ff_scratchadm` persistent + per-run marked roles (what is built); (B) clone from a migrated DB as template (needs `ff_sse` as a template source, not allowed); (C) a kit DB via `db/setup.sh` (also creates roles); (D) CREATEDB for `ff_sse` (widening); (E) a fixed pool of scratch names whose roles are created once.
- **F1 (a) [TLD 12:04]:** `RequireLoopback` accepts only `127.0.0.1` or `::1` (`SCRATCH_PG_HOST`, and the Host in the env connection string must match); host names incl. `localhost`, other IPs (`10.0.0.1`, `127.0.0.2`, `0.0.0.0`, `::ffff:127.0.0.1`), multi-host lists and Unix-socket paths are refused before any connection (`ScratchDb.Opens` unchanged). Port `SCRATCH_PG_PORT` (default 5432) and maintenance DB `SCRATCH_MAINT_DB` (default `postgres`, was `ff_maint_scratch`) are overridable; `ff_sse`, `ff_sse2`, templates, `ff_scratch_*` and non-identifiers are refused as maintenance DB. The rules apply on any cluster. `--scratch-info` prints host, port, maintenance DB and `server_version` (recorded in the evidence). `scratch-admin-setup.sh` no longer creates a maintenance DB and writes `Database=postgres` (not re-run: that would rotate the password; Host/Port/Database in the existing env file are replaced at run time anyway).
- **`ff_maint_scratch`:** a DB I created at P3 with `scratch-admin-setup.sh` (OWNER postgres, comment `floorfunds-scratch-maint`, CONNECT for `ff_scratchadm`); it was not pre-existing. After F1 it is unused. It is still present; not dropped without approval.
- **Tension (open):** TLD 12:04's default maintenance DB `postgres` vs the standing rule "never point kit scripts at postgres". Only CONNECT and catalog reads/`CREATE DATABASE`/`DROP DATABASE` of `ff_scratch_*` are issued from that connection; nothing is created in `postgres` itself.
- **F2 [SSE2 review de9c1946]:** the N1 `Throws` checks now require `auth.app_user.status has an unknown value` in the message. Mutation (p3o): a scratch setup that fails (bad `SCRATCH_MAINT_DB`) makes N1 FAIL, not pass.
- Main run count unchanged (162/0, k1 pin); the self-test modes (`--scratch-info`, `--scratch-crash [--backdate-hours N]`, `--scratch-hold`, `--scratch-decoy-create` / `--scratch-decoy-drop`, `--scratch-drop`, `--scratch-sweep`, `--scratch-snapshot`, `--scratch-selftest old young live`) are not counted.
- **p3o → p3o2 fix (same amend):** the first p3o run (`/tmp/sse-llc1/p3o-evidence.txt`, kept) showed the self-test ending 68/2: its last two checks demanded zero `ff_scratch_*` DBs / roles while the live-connection case is by design still held (DropAsync never FORCEs). They now assert that only the held live DB and its two roles remain; the verify script drops it with `--scratch-sweep` after the hold and checks 0 DBs / 0 roles cluster-wide. `--scratch-drop` on an unmarked name aborted with rc 134 (unhandled exception); it now prints the refusal and exits 2. Evidence: `/tmp/sse-llc1/p3o2-evidence.txt`.
- Spec §14 "Scratch rules" + Sources rows TLD 11:57 (P3), TLD 12:04, RANGE3; checker +9 pins.

## P3 follow-up — TLD 1:54 PM items 1–4 [TLD 2026-11-04 13:54] (sixth amend of the unpushed P3, was 9c0967a)

Evidence: `/tmp/sse-llc1/p3r-evidence.txt` (new file; p3f, p3h–p3q untouched; unique prefix `ff_kit_ssep3r` reserved). No private cluster was started for p3r; its KF-A server-log result is p3q's (below). Not run in p3r because they need the postgres superuser on 5432 (item 3): the k1 kit tests (`db/tests/k1-kit-tests.sh` uses `sudo -u postgres`; db/ is frozen) and mutation M(b) (`ALTER ROLE` / `GRANT` on `ff_scratchadm`); their last results are p3q's (k1 200/0, M(b1)/M(b2) CAUGHT), past results, not current. Open question for TL: how k1 should run under the no-sudo rule.

- **Item 1, lock target refusal:** a lock target DB outside `ff_sse` / `ff_sse2` / `ff_kit_*` in the main run, `--scratch-selftest` or `--scratch-sweep` now exits 2 with the existing refusal message (`FATAL scratch-lock: refuse: the scratch single-runner lock is taken on the kit target DB only (ff_sse, ff_sse2 or ff_kit_*)`), checked by `ScratchLock.Refusal` before any connection; no unhandled exception (was rc 134), no name or other value echoed. Checks: one self-test check (`Refusal` for bad / good names) and, in p3r, one CLI check per mode and name (rc 2, message, no echo, no `Unhandled`, no new session). Mutation ml (the pre-checks removed) brings back rc 134 → those checks FAIL.
- **Item 2, private-cluster KF-A log check (p3q):** a throwaway PG17 cluster on loopback `127.0.0.1:18277`, local superuser `kfasuper`, `log_statement=all`, used ONLY for the KF-A server-log check and the m1+m2 cleartext mutations. It was stopped and removed afterwards (0 listeners on 18277); no cleartext reached 5432 (m1+m2 ran only there; m1 on 5432 is refused by the guard before sending). Accepted by TLD 1:54 PM under the TLD 12:04 ruling.
- **Item 3, no sudo on 5432 [TLD 13:54 NEW RULE]:** from p3r on, nothing on 5432 is inspected with `sudo` / `sudo psql`. Leftover `ff_scratch_*` DBs and roles are counted as `ff_scratchadm` (`pg_database`, `pg_roles`); `ff_sse` table counts as the `ff_sse` owner role. What `ff_scratchadm` cannot see is reported, not worked around: `pg_authid.rolpassword` (KF-A (c)) and the server log file. So p3r has no 5432 (c) or server-log check; those stand on p3q's private-cluster result ((c) PASS; log: 2 `PASSWORD` lines, both `SCRAM-SHA-256$4096:`, 0 other, 0 hex). p3q and earlier evidence used `sudo -u postgres` before this rule; it is left as it was. The k1 kit tests and M(b) also need the superuser, so they are not in p3r (see Evidence).
- **Item 4, TLD rulings [TLD 13:54]:** no cluster-wide lock (per kit DB stays, TLD 13:15 #5); the empty test lock DB `ff_kit_ssekfalock` on the private cluster is accepted; the private-cluster log check is enough, so 5432 (`log_statement=none`) is not changed.
- Self-test 139 → 140 checks (the item 1 pre-check); main run unchanged.

## P3 follow-up — TLD 1:15 PM item 2 on KF-A [TLD 2026-11-04 13:15] [KF-A] (fifth amend of the unpushed P3, was 1b9f5bd)

Evidence: `/tmp/sse-llc1/p3q-evidence.txt` (new file; p3f, p3h–p3p untouched; k1 prefix `ff_kit_ssep3q`). Details: `src/Auth.Data.Checks/README.md` § "KF-A".

- **TLD 1:15 PM:** items 1 and 3–9 accepted as proposed; item 2 (the self-test session-role password) accepted on the kit's **KF-A** rule: a role password reaches the server only as a client-side SCRAM-SHA-256 verifier, never cleartext in `CREATE/ALTER ROLE`; random per run, memory only.
- **Fix:** `CreateMarkedRoleAsync` sent `PASSWORD '<32 hex cleartext>'` for the self-test `sessRole`. It now sends `ScratchDb.ScramVerifier(password)` = `SCRAM-SHA-256$4096:<salt>$<StoredKey>:<ServerKey>` (RFC 5802/7677, PBKDF2-HMAC-SHA256 4096, random 16-byte salt), the format of `db/kit/role.sql` / `db/setup.sh` (KF-A; kit files and frozen files unchanged). RFC 7677 vector checked; the p3q script also checks the kit's perl formula gives the same verifier as the C# code for that vector.
- **Allowlist [TLD 13:15]:** the maint guard's create-role pattern now accepts only `PASSWORD 'SCRAM-SHA-256\$4096:[A-Za-z0-9+/]{22}==\$[A-Za-z0-9+/]{43}=:[A-Za-z0-9+/]{43}='` (`MaintSql.ScramVerifierRe`); hex cleartext is refused. Not widened to `SET log_statement`.
- **Checks (self-test "KF-A" block):** (a) SCRAM-only sign-in with the cleartext, wrong password 28P01; (b) statement capture: one `CREATE ROLE`, verifier only, cleartext in no statement; `pg_stat_activity` sampled during the CREATE shows the verifier, never the cleartext; server log for the run window grepped read-only as `postgres` by the verify script (`log_statement=none`: superuser GUC, not settable by `ff_scratchadm`, not changed — so statement capture + `pg_stat_activity` are the primary evidence, as TLD allowed); (c) `pg_authid.rolpassword` prefix `SCRAM-SHA-256$` read by the verify script as `postgres` while the self-test holds the role (prefix only). Limitation: the server hashes cleartext input too, so (c) alone cannot catch cleartext in transit.
- **Mutations (p3q):** m1 `CreateMarkedRoleAsync` sends the cleartext → refused by the guard, the self-test FAILs and stops (rc 1, nothing else created); m2 the regex also allows 32 hex → the "32 hex cleartext is refused" check FAILs; m1+m2 → the statement-text and `pg_stat_activity` checks FAIL.
- **SSE2 review 04dba71d [steering 13:21], folded into this amend.** Review `/tmp/sse2-fverify/RANGE-9771ad3-1b9f5bd-REVIEW.md` (md5 04dba71d9eb8f4a85d9425e4e7774505, PROVISIONAL GO on 1b9f5bd), snapshot `/tmp/sse-llc1/RANGE-9771ad3-1b9f5bd-REVIEW.04dba71d.snapshot.md` (same md5). SSE2's baseline: exactly one cleartext `CREATE ROLE … PASSWORD '<32 hex>'` (the self-test session role) in its `log_statement=all` log.
  - **KF-A, three places:** after every `PASSWORD` only `SCRAM-SHA-256$4096:` (or no `PASSWORD`) — statement text (whole self-test captured: 2 `PASSWORD` statements, both verifiers, neither test password anywhere), `pg_stat_activity` (sampled during both `CREATE ROLE`s) and the server log (p3q: shared cluster read-only for the run window, `log_statement=none`; plus one self-test on a private throwaway PG17 cluster with `log_statement=all`, removed afterwards).
  - **Low:** a refused admin connection-string key (and any other admin-target refusal) in a `--scratch-*` mode exits 2 with `FATAL scratch: refuse: …` naming the key, never the value (was an unhandled exception, rc 134). p3q: one CLI check per key (8 keys); mutation mk (the rc-2 mapping removed) → rc 134 → those checks FAIL.
  - **Info:** spec §13 "Allowed public getters": `EntraDeployment`'s read-only `Authority` / `ClientId` / `SecretRef` getters and redacted `ToString` are allowed; the `Admin.Api.Checks` reflection check allows exactly those members (its comment and label say so; count unchanged).
- **Scratch lock per kit DB [TLD 2026-11-04 13:15 #5]:** the advisory lock is on the kit target DB, so runs on different kit DBs do not block each other (documented in the spec §14 and the scratch README).
- Self-test 116 → 139 checks (not counted by k1); main run 162/0; spec 414 → 423/0 at P3 (+9 pins: TLD 13:15 and RANGE4 Sources rows, §14 KF-A / lock / rc 2, §13 getters).

## P3 follow-up — TLD 12:33 items 1–3 [TLD 2026-11-04 12:33] (fourth amend of the unpushed P3, was 9771ad3)

Evidence: `/tmp/sse-llc1/p3p-evidence.txt` (new file; p3f, p3h–p3o2 untouched; k1 prefix `ff_kit_ssep3p`). Details: `src/Auth.Data.Checks/README.md`.

- **Item 1, o3 [TLD 12:33]: "net role set identical; per-run roles allowed only if marked and dropped".** This replaces the "o3 NOT fully met" note below (option A as built, now ruled). Per-run roles `<runid>_admin_api` / `<runid>_login` are marked `floorfunds-scratch` and dropped in the same finally as the DB. `SweepAsync` now also sweeps roles: a marked role with a runid older than 1 h (UTC timestamp), no `pg_stat_activity` session as that role, not this run's and whose DB is gone is dropped; young, in-session, own-run or DB-still-present roles are kept. An unmarked `ff_scratch_*` look-alike role is reported (`FATAL scratch-sweep: unmarked ff_scratch_* look-alike role(s) …`), never dropped; the main run's scratch setup throws, so N3 / N1 / Q2 FAIL, and `--scratch-sweep` exits 3. Self-test cases: stale marked role dropped; young role kept; stale role with a live session kept, then dropped by the next sweep once the session ends; unmarked look-alike reported and kept (then cleaned up by exact name); exactly one stand-alone role dropped.
- **`ff_scratchadm` [TLD 12:33]:** documented kit infrastructure. `AdminRoleViolationsAsync` requires the `pg_roles` row `ff_scratchadm|false|false|true|true|true|false|false|-1||floorfunds-scratch-admin` (NOSUPERUSER NOINHERIT NOREPLICATION NOBYPASSRLS, only CREATEDB + CREATEROLE), no `pg_db_role_setting` rows, and no membership except the PG17 implicit grant on each **marked** per-run role it creates (`ff_scratchadm` member, ADMIN true, INHERIT false, SET false, grantor oid 10; `createrole_self_grant` empty). It runs in every main-run setup (a violation fails N1 / N3 / Q2), in Q2's condition, in the self-test and as `--scratch-admincheck` (exit 4). The live state already matched; no ALTER was needed. `scratch-admin-setup.sh` got doc comments only (not re-run). No password or secret for it is in the repo (p3p secret grep: `git grep -F` and `git log --all -p | grep -F` on the env-file password, both 0).
- **Item 2 [TLD 12:33]: `ff_maint_scratch` dropped** at 2026-11-04 12:42 PST after both checks passed: comment `floorfunds-scratch-maint` (owner postgres) and 0 `pg_stat_activity` connections; `DROP DATABASE ff_maint_scratch` (as postgres) rc 0, 0 rows after. Record: `/tmp/sse-llc1/run/p3p-ff_maint_scratch-drop.txt` (md5 d10a4acac16c9d6b0d96750eeeb17fb2), copied into the p3p evidence.
- **Item 3 [TLD 12:33]: the `postgres` maintenance-DB exception is scoped in code.** Every maintenance-connection statement goes through `MaintConn`; `MaintSql` holds the allowlist: connect; the fixed reads `MaintSql.Reads`, naming only `pg_database`, `pg_roles`, `pg_stat_activity`, `pg_auth_members`, `pg_shdescription`, `pg_tablespace`, `pg_db_role_setting` (the last two only for the o3 snapshot / settings check: beyond TL's list, open question); and `CREATE` / `COMMENT` / `DROP` `DATABASE` or `ROLE` on `ff_scratch_*` names in fixed shapes, DROP only for marked (and, for DBs, owned) objects. Anything else is refused before it is sent (`refuse [maint-guard TLD 12:33]`, the first two words only). `Program.cs` no longer opens raw maintenance connections. The rule against `postgres` as a target DB is unchanged. Self-test: 7 non-allowed statements refused (`SELECT 1`, a `pg_authid` read, `ALTER ROLE ff_scratchadm SUPERUSER`, multi-statement, `DO`, `SET ROLE`, another comment), unmarked / missing drops and a non-scratch CREATE refused, every read names only listed catalogs, refusal text echoes no literals.
- **Exact-name cleanup** of objects made unmarked on purpose (o2 decoy, look-alike role, roles leaked by mutation a): `CleanupUnmarkedAsync` (prefix, unmarked, owned, no sessions → marked, then dropped through the marked path); modes `--scratch-decoy-drop`, `--scratch-cleanup-role`, `--scratch-lookalike-create`.
- **Mutations (p3p):** M1–M3 as p3o2 (M3: the role DROP in `DropAsync` disabled); M(a) the `COMMENT ON ROLE` marker in `PrepareAsync` removed → caught (Q2 FAIL, next sweep FATAL on the unmarked roles; cleaned up by exact name); M(b1) `ALTER ROLE ff_scratchadm REPLICATION` and M(b2) `GRANT pg_read_all_data TO ff_scratchadm` → caught by `--scratch-admincheck` and the main run, then reverted and the saved role row / memberships / settings compared identical; M4 the maint guard disabled → the refusal checks FAIL.
- Main run unchanged at 162/0 (no new counted checks; Q2's name gains "ff_scratchadm as documented [TLD 12:33]"). Spec §14 "Scratch rules" paragraph updated (maintenance-DB exception, o3 wording) + Sources row TLD 12:33; checker +8 pins.
- **SSE2 review 09e31f68 [steering 12:45], folded into this same amend.** Review `/tmp/sse2-fverify/RANGE-6a71fbf-9771ad3-REVIEW.md` (md5 09e31f68f8a08505e2d057e3839c34ef), snapshot `/tmp/sse-llc1/RANGE-6a71fbf-9771ad3-REVIEW.09e31f68.snapshot.md` (same md5). Details in `src/Auth.Data.Checks/README.md` § "SSE2 review 09e31f68".
  - **F3:** p3p is produced in a detached worktree of the new P3 commit (`/tmp/sse-llc1/wt-p3p`, removed afterwards) and its header prints that sha and checks every md5 against the P3 tree; p4c is produced at the P4 HEAD (`/tmp/sse-llc1/p4c-evidence.txt`), header prints that sha.
  - **F4:** the self-test's env overrides restore the saved value (`WithEnv`), and a check compares the three `SCRATCH_*` values with the start snapshot after each of the 16 blocks; p3p runs the self-test also with `SCRATCH_PG_HOST=127.0.0.1 SCRATCH_PG_PORT=5432 SCRATCH_MAINT_DB=postgres` set explicitly (0 failed).
  - **M2:** `--scratch-drop` (`ScratchDb.DropCliAsync`) on a marked DB with an open connection → exit 2 with "N connection(s) in pg_stat_activity; not dropped", no crash; SQLSTATE 55006 at DROP is caught and refused the same way.
  - **M3 (covers L3, L4):** single-runner guard `pg_try_advisory_lock(7378711910451082339)` on the kit target DB connection (`CONNECTION_STRING`), not on the maintenance DB (the 12:33 allowlist is unchanged); main run, `--scratch-selftest` and `--scratch-sweep`. A second concurrent run exits 5 at once with "another scratch run holds the lock". p3p: 4 parallel main runs → exactly 1 runs (162/0), 3 exit 5 fast, 0 scratch objects left. Limitation: per database (runs on two different kit DBs do not exclude each other) — open question.
  - **L1:** a marked, owned DB or role whose runid is missing, invalid or > 5 min in the future is reported by name (`BAD RUNID`, FATAL), the main run fails and `--scratch-sweep` exits 3; never kept silently, never dropped automatically.
  - **L2:** every name / DDL pattern is anchored `\z` (no `$`); `ff_scratch_abcdef\n` is refused by `GuardName`, `DropAsync` and the maint-guard classifier. `--scratch-drop` prints "nothing to drop" when nothing existed (no "dropped").
  - **Info:** the admin connection string allows only Host/Server, Port, Database, Username, Password, Pooling (Passfile, Options, Target Session Attributes, Include Error Detail, SSL Mode refused in the self-test, key named, value never). The CLI has no "own DB" concept: `--scratch-drop` drops any marked, owned, unconnected scratch DB by name.
  - Self-test 97 → 116 checks (not counted by k1); main run still 162/0; spec and checker unchanged (430/0).
  - Mutations (p3p): M2 pre-check disabled (→ the 55006 path still refuses rc 2, the pre-check check FAILs); M3 lock always granted (→ the second-acquire check FAILs); L1 bad runid kept silently (→ L1 check FAILs); L2 `\z` reverted to `$` (→ the newline checks FAIL); F4 restore-to-null (→ the F4 check FAILs with an explicit override).

## P3 follow-up — TLD 11:19 Q1–Q4 + TLD 11:21 Q2 (b) [TLD 2026-11-04 11:19] [TLD 2026-11-04 11:21] (second amend of the unpushed ba85c77)

One amend, Q1–Q4 together (user 11:20: fold Q2/Q3 in before P4; no P4 commit existed yet, so no rebase was needed).

- **Q1** spec §5: 401 / 403 stay first (TLD 9:04 #8) and 408 comes straight after them, cited [TLD 2026-11-04 11:19]; check pin
  added. Code order unchanged (already so).
- **Q4** `/tmp/sse-llc1/run/p3-verify-template.sh` takes the venv as `VENV=<dir>` (required, no hard-coded path) and prints the
  python / jsonschema / referencing versions in the evidence; `ONLY=spec` runs only the spec checkers + frozen md5s. Old
  template kept as `p3-verify-template.bac3fc59.sh`. Spec §14 "Run:" line now uses `"$VENV"` too. Nothing installed; VENV used:
  `/tmp/sse-d0/venv` (python 3.13.5, jsonschema 4.23.0, referencing 0.37.0).
- **Q2 → option (b) [TLD 2026-11-04 11:21].** SSE2's N3 / N1 probes moved off the rolled-back owner transaction on
  AUTH_CHECKS_DB onto a throwaway scratch DB:
  - `Readers.FindOnConnectionAsync` is `internal` again (as at 06dc8ac); no `InternalsVisibleTo` anywhere. The `:619`
    "on-connection path == FindUserAsync" check is dropped (it needed the wider API).
  - New `Auth.Data.Checks/ScratchDb.cs`. Each Auth.Data.Checks run: start-up sweep → `CREATE DATABASE ff_scratch_<12 hex>`
    (`TEMPLATE template0`, read only as the copy source) → `COMMENT ON DATABASE … 'floorfunds-scratch'` straight after →
    two plain marked app roles `<db>_admin_api` / `<db>_login` (LOGIN, **no password**: they exist only because the migration
    requires them) → `MigrateAsync` (same runner as `Auth.Data.Migrator update`) → the probe rows (scratch-only fake users
    `…05d1..05d3`) and the FK / CHECK drops are **committed in the scratch DB only** → the 4 probes call the public
    `new NpgsqlExternalLoginReader(scratchDs).FindUserAsync(...)` and must throw `InvalidOperationException` (never null /
    Active) → `finally` drops the DB and its two marked roles, also on failure.
  - Guards: maintenance connection must be 127.0.0.1:5432 and the connect-only DB `ff_maint_scratch` (never postgres /
    template / ff_sse*); name must match `^ff_scratch_[a-z0-9]{6,24}$` and is explicitly never ff_sse, ff_sse2, postgres,
    template*, ff_maint_scratch; refuse when the name already exists; nothing is dropped without the marker AND ownership by
    the scratch role; unmarked roles are refused too. The sweep does not use `WITH (FORCE)`: a scratch DB still in use by a
    concurrent run is skipped (55006), not killed.
  - AUTH_CHECKS_DB is never altered: "N3: FK and CHECK still present in AUTH_CHECKS_DB" stays; the new counted check "Q2: this
    run's scratch DB dropped in the finally; no MARKED ff_scratch_* DB left" replaces `:619`, so the DB run stays at
    **162/0**, which k1 (`db/tests/k1-kit-tests.sh`, not mine, not edited) pins literally.
  - Self-test modes (verify script §4c, not counted by k1): `--scratch-crash` creates + marks + migrates and exits 70 without
    the finally (simulated crash); `--scratch-selftest <name>` checks FindOnConnectionAsync is not public (reflection),
    the name / connection guards (29 refusals), that the crashed DB was left MARKED, creates an UNMARKED decoy
    `ff_scratch_decoy<8 hex>`, DropAsync and CreateMarkedAsync refuse it, the sweep drops the crashed DB (+ roles) and keeps
    the decoy, then I drop the decoy myself (exact name, still unmarked) and check no ff_scratch_* DB or role is left.
  - **Cluster setup (decision, flagged):** the ff_sse owner has no CREATEDB, so `src/Auth.Data/tools/scratch-admin-setup.sh`
    (run once via `sudo -u postgres`) created role `ff_scratchadm` (LOGIN CREATEDB CREATEROLE, NOSUPERUSER NOBYPASSRLS
    NOREPLICATION; marker comment `floorfunds-scratch-admin`) and a connect-only DB `ff_maint_scratch` (owner postgres,
    CONNECT only for ff_scratchadm). PG17 CREATEROLE only manages roles it created; DROP DATABASE needs ownership, so it can
    not drop ff_sse / ff_sse2. Password in `/tmp/ff-db/ff_scratchadm.env` (0600, test-only, never printed); `tools/env.sh`
    sources it when present. ff_sse2 and its env files untouched.
- **Q3** commit subject reworded to the final code: the `Expect` 413 close is per-request `MaxRequestBodySize = 0` (TLD 11:00),
  not an abort; it also names F-A1 (validated `EntraDeployment.Create`), F-B1 (total-deadline 408) and the Q2 scratch DB.
- The MD2 record is accepted as is [TLD 2026-11-04 11:19].
- **SSE2 review of ba85c77** (`/tmp/sse2-fverify/RANGE-06dc8ac-ba85c77-REVIEW.md`, md5 `3fac1d52402e99371b9fc254b03b74f0`,
  provisional; snapshot `/tmp/sse-llc1/RANGE-06dc8ac-ba85c77-REVIEW.3fac1d52.snapshot.md`), folded into this same amend (user 11:29):
  - **F-C1 (Low)** under TLD 10:58 F-A1: `EntraDeployment.IsValidAuthority` and `SecretRefAllowed` are `internal`.
    `EntraDeployment.Reasons` (5 consts) and `EntraDeployment.Result` (Value / Reason / Ok / ToString) stay public as `Create`'s
    return surface; the instance getters and redacted ToString stay as at 06dc8ac (spec §13 note). `AdminApiOptions.IsValidAuthority`
    = `Create(v, null, null, null, null).Reason != AuthorityInvalid` (Create checks the authority first; null still throws
    ArgumentNullException as the old Regex call did). `AdminApiOptions.SecretRefAllowed(v)` = `Create(<fixed valid authority>,
    <fixed valid GUID>, v, SecretAccountIds, SecretRegions).Ok` (null still throws NullReferenceException). **Exact case where it is
    not identical:** on an options object whose own SecretRef:AllowedAccountIds / AllowedRegions are malformed (never one the host
    uses: Validated() refuses it at startup), the old predicate could return true for an entry that matches the ARN group but not
    the list rule (e.g. region `local`); through Create it returns false (the list is refused first). Nothing calls this wrapper
    today (git grep). Not widened; listed for TL. Startup refusals and reason codes unchanged (all existing startup checks pass).
  - New Admin.Api.Checks: "F-C1: EntraDeployment public surface is exactly {...}" (reflection over the type, Reasons and Result;
    anything else public fails) and two wrapper-equivalence checks (12 authorities, 9 secret refs, null behaviour) → 393/0.
    Mutation: `IsValidAuthority` public again → 392/1, the surface check FAILs.
  - **Info (MD4 / MD4b), harness only:** `Send` now turns an IOException / SocketException (incl. wrapped in HttpRequestException)
    into one FAIL "transport: <method> <path> got a response" with the exception types and a placeholder response; `Obj` turns a
    non-JSON body into one FAIL; any other exception out of the inline sequence is one FAIL "harness: check sequence completed"
    and cleanup still runs. Re-run: MD4 (zero limit on every unread-body path) → 390/4 clean (the broken pipe is now
    `FAIL transport: POST /api/orgs/locations ... HttpRequestException/IOException`, the run carries on); MD4b (zero limit at
    request start) → 113/4 clean (the sequence stops at the first null after the empty 201, by design of the inline checks:
    making every inline check null-safe is not a small change, left).
- **Open:** (o1) the start-up sweep cannot tell a crashed run's DB from one a concurrent run created but has not yet
  connected to (sub-second window between CREATE and migrate); it would drop it and that run would fail (no data loss).
  (o2) a crash between CREATE and COMMENT leaves an unmarked DB that only a human removes. (o3) the ff_scratchadm role /
  ff_maint_scratch DB are new cluster objects on the shared test cluster — keep, or move into the db/ kit (SSE2/TL)?

## P3 follow-up — TLD 10:58 / 11:00 (+11:10) [TLD 2026-11-04 10:58] [TLD 2026-11-04 11:00] (amend of the unpushed 4b3f849)

SSE2 range review of 8c3ccce..06dc8ac (`docs/reviews/RANGE-8c3ccce-06dc8ac-REVIEW.md`, md5 ce1151ae, snapshot
`/tmp/sse-llc1/RANGE-8c3ccce-06dc8ac-REVIEW.ce1151ae.snapshot.md`; SSE2 GO, TL pushed 06dc8ac). TL decisions 10:58, clarified 10:59,
11:00; TL + SSE2 steering 11:10. Amended into the 10:42 commit (unpushed); no UI; Sources review md5 (bfaa2efb) unchanged.

- **F-A1 validated `EntraDeployment` [10:58, clarified 10:59].** `Auth.Data/EntraDeployment.cs`: the constructor is `internal`; the only
  public way in is `EntraDeployment.Create(authority, clientId, secretRef, allowedAccountIds, allowedRegions)`, which runs the db8f360
  rules (moved here from `AdminApiOptions`: authority, GUID client id, account / region allowlists, ARN shape) and returns a `Result`
  (`Value` or `Reason`; `ToString` prints the reason only). Reason codes: `entra_authority_invalid`, `entra_client_id_invalid`,
  `secret_ref_allowed_account_ids_invalid`, `secret_ref_allowed_regions_invalid`, `entra_secret_ref_not_allowed`; none contains a value.
  `AdminApiOptions.Validated()` / `Entra()` go through `Create` and map each reason to the old key-only startup messages (same order).
  **No `InternalsVisibleTo` anywhere**: removed from `Auth.Data.csproj` and `Admin.Api.csproj`. To keep the checks compiling without it,
  `Readers.FindOnConnectionAsync` is now `public` (used by `Auth.Data.Checks/Program.cs:596`, `:617`) — listed as an open question.
  Callers changed: `Auth.Data.Checks/Program.cs:539` (now `EntraDeployment.Create(…SSE2-FAKE-TEST-ONLY client id, fake ARN 000000000000…)`),
  `Admin.Api/AdminApiOptions.cs` (`ValidatedWith`). `Admin.Api.Checks` already used `Opts().Validated().Entra()`.
  SSE2 (`feat/entra-signin`, `wt-sse2-entra`, read only): no call of the constructor; only comments / docs and SSE2's own stand-in type
  (`src/Login.Entra/Fixtures/FixtureStores.cs:16, 37, 71`; `tests/Login.EntraTests/EntFlowTests.cs:535`; `tests/Login.EntraTests/README.md:46-47`).
  Nothing of SSE2's breaks.
  New checks: reflection (no public constructor, all constructors internal; no `InternalsVisibleToAttribute` on either assembly);
  bypass `Create(…, "not-an-arn")` refused with `entra_secret_ref_not_allowed`, value absent from reason and `ToString`; valid fake → value;
  7 refusal cases with their codes; `Opts() with { EntraSecretRef = "not-an-arn" }.Entra()` throws naming the key, no value.
  The F-A1 block now runs before the startup refusal loops (so a `Create` mutation reaches it before the empty-SecretRef case).
- **F-B1 total deadline [10:58; 11:10 TL/SSE2: under-limit slow body].** New 7f: raw PUT, `Content-Length: 100` (well under 64 KiB, never
  reached, so the body never completes), 1 byte every 2900 ms (each gap under the 10 s deadline). Expect 408 `request_timeout`,
  `Connection: close`, `X-Request-ID`, within [9500, 11500] ms of the head being sent (the body read starts right after the head),
  ONE Warning, nothing written. Measured: dev1 10010 ms, dev2 10007 ms, dev3 10006 ms, 4 bytes sent each time. The 8 MiB trickle check
  (7d "413: bounded read …") is kept. `ReadBoundedAsync` already used one `CancellationTokenSource(deadline)` for the whole read: no code
  change; the per-read reset is recorded as mutation MD3.
- **F-A2** Info only, no change. **F-A3** spec §13 backlog note: P4 tests need org:V coverage against real PG, not only fakes.
- **TLD 11:00 #1 `Expect` close approved** (`MaxRequestBodySize = 0` on that request, `AdminApiHost.cs` L117; L96 resets the feature per
  request). Repeats 40 → **200** (100 PUT + 100 POST): 0 resets, every 413 received, close after the response max 0 ms, median 0 ms
  (bound < 1500 ms). New limit-scope check: after the Expect 413s, fresh-connection PUT and POST of 65472 B → 422 (`zz_pad`
  `unknown_field`, never 413), 64 KiB + 64 → 413, nothing written. MC2 (`ctx.Abort()`) kept below as the mutation record.
- **TLD 11:00 #2 §5 order** worded as ruled: on PUT 413 stays at step 7 after 404/428/409 (P3-3, TLD 9:28, stands); on create 408 and
  413 come first; 408 first everywhere; TL's 10:42 item 5 was too broad. Code checked against it: PUT 401/403 → 408 (read right after
  step 2) → 404 → 428/400 → 409 → 413; POST 403 → 408 → 413 → 400 → 404. Matches; no code change. New checks: PUT missing location +
  400 KiB → 404, PUT without If-Match + 400 KiB → 428, PA POST 400 KiB → 413 (no event), slow PUT with missing location / no If-Match /
  stale If-Match → 408, PA slow create → 408; "P3-3 unchanged" stale If-Match + 400 KiB → 409 kept.
- **TLD 11:00 #3** LD L171 quote kept verbatim ("`secret_ref` is never shown (only "set / rotated on")") with the note that S2a shows
  no date (V11-ROTATEDAT).

Checks: Admin.Api.Checks 365 → **390** (0 failed); crosscheck 548 docs / 342 responses (the 200 Expect repeats and the new 7d–7f
requests are in the corpus), 0 disagreements; check_v1_1_s2a 370 → **391**; check_schemas ALL PASS; Auth.Data 162. Build 0 warnings.

Mutations (`/tmp/sse-llc1/c2/mut.sh`, restore verified with `md5sum -c` against `preMD.md5` / `preMD2c.md5` after each):
- MD1 constructor made `public` → 389/1: F-A1 reflection check fails. Auth.Data 162.
- MD2 `Create` skips all validation → **not applied**: the mutated build failed (CS8604 nullable), the harness ran stale binaries; discarded.
- MD2b `Create` skips all validation (with `authority!`) → 19 FAILs (startup refusals accepted), then the run crashed at the empty
  SecretRef case (`SecretReference("")` throws) before the F-A1 block (which then ran later in the file).
- MD2c `Create` skips only the ARN / allowlist check → 3 FAILs, all F-A1 (bypass `not-an-arn`, account outside allowlist, `Entra()`
  refuses not-an-arn), then the same crash at the empty SecretRef loop. Run after moving the F-A1 block first.
- MD3 per-read deadline reset (`timer.CancelAfter(deadline)` after each read) → 387/3: 8 MiB trickle, F-B1 408 and F-B1 Warning fail.
- MD4 limit leaks to later requests (static flag sets every later request's limit to 0) → 383/7: both 11:00 limit-scope 422 checks,
  drain-timeout, Error-log and F-B1 checks fail.
- MD1, MD3, MD4 were run before the 11:10 relabel of 7f and the move of the F-A1 block (labels/order only; logic identical).
- MC1–MC3 (10:42 section above) stand; MC2 is the record for TL's literal `ctx.Abort()`.

Evidence: `/tmp/sse-llc1/p3m-evidence.txt` (script `/tmp/sse-llc1/run/p3m-verify.sh`, a copy of the P3 template; k1 prefix ff_kit_ssed1;
k1 out `/tmp/sse-llc1/run/p3m-k1.out`). p3l left as it was. The template's Python venv (`/tmp/sse2-venv`) no longer exists on the box;
p3m-verify.sh uses the existing `/tmp/sse-d0/venv` (jsonschema 4.23.0, referencing 0.37.0) instead: nothing installed.

## P3 follow-up — TLD 10:42 [TLD 2026-11-04 10:42] (on top of commit B 06dc8ac)

TL decisions 10:42 AM PT on 8c3ccce..06dc8ac. One commit; no UI; Sources review md5 (bfaa2efb) unchanged (P4 commit).

- **`rotated_at` dropped (V11-ROTATEDAT).** `LoginConfigApi`: the `max(change.at) … 'secret_ref' = ANY(changed_fields)` column and
  `LlcRow.RotatedAt` removed; GET / PUT 200 / POST 201 views carry `secret {set}` only, Super Admin only, omitted for the other roles.
  Schema `secret_view` = `{set}` (required, closed). Spec status line, §3.0 / §3.1 / §3.2 / §5 / §5.1 / §8 change table / §11 / §12 / §13 updated, A-3 retired, V11-ROTATEDAT in §12
  (the last-rotation date is to come from the secret store: rotation happens in AWS and the ref usually does not change).
- **Expect 413: connection closed right after the 413.** Before: Kestrel held the connection ~6 s (p3j: PUT 6047 ms, POST 6697 ms).
  TL asked for an abort after the flush. A `ctx.Abort()` right after `CompleteAsync` was tried first: in the first full check run
  (`/tmp/sse-llc1/c-run1`) the single POST and at least 3 of the 40 repeats got a reset and no 413 (status 0, "Connection reset by
  peer"). So the host instead lowers Kestrel's `MaxRequestBodySize` to 0 for that request (never started, so still writable); Kestrel's
  end-of-request drain then rejects the body at once and closes the connection itself once the 413 is out: a clean FIN, no reset.
  Approved [TLD 2026-11-04 11:00] (that request only; see the 10:58 / 11:00 section below).
- **Spec only:** §5 order note (408 before 404/428/409; 413 before 404 on create; PUT 413 still at step 7 after 404/428/409 per P3-3,
  ruled [TLD 2026-11-04 11:00]: P3-3 stands); V11-BODYTIMEOUT-CONFIG (`BodyReadTimeout` code-only 10 s); body-read reset / bad
  framing → 500 + Error recorded under V11-HTTPFRAMING; TL kept the `EntraDeployment` ctor parameter and `access_rules.secret_visible_to`.

Checks: Admin.Api.Checks 360 → **365** (+2 per-method close < 1.5 s, +1 40× repeat complete + closed < 1.5 s, +1 40× clean close (no
reset), +1 repeats log/write nothing; the Q2 secret checks now require exactly `{set}`). check_v1_1_s2a 345 → **370**. Auth.Data 162.
Measured close after the 413 (c-run2..c-run4): PUT 0 ms, POST 0 ms, 40 repeats max 0 ms / median 0 ms (bound < 1500 ms).

Mutations (`/tmp/sse-llc1/c2/mut.sh`, restore verified against `preMC.md5`):
- MC1 no close (limit not lowered) → 361/4: per-method close checks fail (PUT 6049 ms, POST 6697 ms), repeats max 6999 ms.
- MC2 `ctx.Abort()` after the flush (TL's literal form) → 363/2: repeats lose 413s (reset) and close with resets.
- MC3 `rotated_at: null` back in the view → 359/6: every Q2 `secret {set}`-only check fails.

Evidence: `/tmp/sse-llc1/p3l-evidence.txt` (script `/tmp/sse-llc1/run/p3l-verify.sh`, a copy of the P3 template; k1 prefix ff_kit_ssec1;
k1 out `/tmp/sse-llc1/run/p3l-k1.out`).

## P3 HTTP body handling — TLD 9:53/10:02 P3-7/P3-8/P3-9/P3-10 (commit B, on top of commit A 5db350a)

Findings from SSE2's P3 review (`/tmp/sse2-fverify/S2A-P3-REVIEW.md`; the spec still cites the snapshot md5 bfaa2efb, the original is now c6f7258b, see OPEN below). TL decisions 9:53 and 10:02 AM PT:

- **P3-7 [TLD 2026-11-04 9:53/10:02] 408 `request_timeout`.** It is an **S2a-only** top-level code: it is in `location-login-config.schema.json` `error_code` (next to `payload_too_large`) and in spec §5, in the same way as `payload_too_large` (TLD 9:28).
  - **`contract/errors.md` stays frozen** (md5 1e97dae4d3da00c429d56c0f2f78464e). The general code waits for v1.1.
  - `AdminApiOptions.BodyReadTimeout` sets the deadline for the whole body. It is a code default of 10 s (**DRAFT**), with no config key, and the validator requires (0, 60] s. The checks set it to 1 s.
  - A body under 64 KiB that is not fully received in time gets 408 `{"error":"request_timeout"}`, `Connection: close` (the body is unread) and `X-Request-ID`.
  - Nothing is written: no row and no event. It logs ONE Warning line, `admin-api request body not received within N s: 408 request_timeout, request_id=…`, with no exception object.
  - On PUT the 408 is returned as soon as the read gives up, which is right after step 2 and before steps 3–6. The body is read once there, so this is an SSE reading, listed in OPEN. 401/403 still come first: an unpermitted slow body gets 403 plus the `not_admin` event, as before.
- **P3-8 [TLD 2026-11-04 9:53/10:02] drain log and `Expect`.**
  - **Root cause found:** the body reads before B cancelled Kestrel's request-body read with a `CancellationToken`. That left the reader "already in progress", and Kestrel then logged `InvalidOperationException: Reading is already in progress` at **Error with a stack trace**. It also closed the connection, by accident.
  - Both reads (handler and drain) now go through `RequestEnvelope.ReadBoundedAsync`. It uses the `PipeReader` with a timer that calls `CancelPendingRead`, so no token is cancelled and the pipe stays consistent.
  - A drain that reaches 2 s logs ONE Warning line (`admin-api body drain timed out after 2 s, N bytes discarded, request_id=…`), with no exception and no stack trace. It does not log if the request already logged its 408 line. It then calls `ctx.Abort()`, so the connection closes at the 2 s bound as before; the client has had 2 s to read the response.
  - `Expect: 100-continue` with `Content-Length` over 64 KiB is refused by length before the first read. The client gets 413 immediately and never a `100 Continue`.
  - An `Expect` request whose body was never read is not drained. There is no `Abort` there either: an abort right after `CompleteAsync` lost the response in a test run, giving a reset.
- **P3-9 [TLD 2026-11-04 9:53/10:02]:** no code change. Backlog **V11-HTTPFRAMING** is added in spec §12, next to V11-AUDITRATE and V11-TIMING.
- **P3-10 [TLD 2026-11-04 9:53/10:02]:** the dead global `k.Limits.MaxRequestBodySize = 1 << 20` is removed from `AdminApiHost.cs` (`AddServerHeader = false` is kept).
  - The middleware still lifts the framework size limit per request, and now also the minimum body data rate (`IHttpMinRequestBodyDataRateFeature`), so only the API's deadline applies.
  - Spec §3.3, §5.1 and §13 OQ-P3-9 no longer mention "Kestrel's 1 MiB". §5.1 no longer says "+ one 8 KiB buffer", because bytes already buffered past the cap are discarded and never parsed.
- A body read that fails (reset, bad framing) still ends in 500, as before: `BadHttpRequestException` is caught in `Run`, and on the 403 attribution path it means no body.
- **Checks (Admin.Api.Checks 339 → 360, section 7e, raw sockets, a second host with `BodyReadTimeout` 1 s and a Warning-level `CaptureLogger`):**
  - P3-10: the Kestrel `MaxRequestBodySize` is the framework default; the `BodyReadTimeout` default is 10 s.
  - 408 for a slow SA PUT and a slow PA POST: body, `Connection: close`, `X-Request-ID`, no reset, received at or after the deadline.
  - Exactly one Warning line each, with no exception, no newline and no stack text; it names the PUT's `request_id`.
  - Nothing written: no event, change rows, location count or row_version change.
  - 401 (immediately) and 403 + `not_admin` for slow bodies, never 408; no 408 log for those.
  - `Expect: 100-continue` + 8 MiB PUT and POST: 413 immediately, no `100 Continue` bytes, nothing logged or written.
  - Drain timeout (401, declared 8 MiB, client stalls): closes at the 2 s bound with one Warning line and no stack.
  - A normal GET still works afterwards, and there is no Error-level line.
  - The `CaptureLogger` gained an optional minimum level and an `Entries` list (level, text, whether there is an exception). `Lines` is unchanged.
  - Auth.Data.Checks stays at **162**. The crosscheck validates the 408 bodies against the schema.
- **Spec checker** `check_v1_1_s2a.py` 328 → **345**: the 408 body is valid, fields and unknown codes are rejected, the enum has `request_timeout` once next to `payload_too_large`, errors.md has no `request_timeout`, plus spec pins (the §5 row, step 7, §5.1, V11-HTTPFRAMING in §12, the §13 rulings, Sources), no stale "Kestrel 1 MiB" / "8 KiB buffer" text, and the 408 row's position.
- **Mutation spot-checks** (`/tmp/sse-llc1/c2/mut.sh`, run sequentially, each restored and its md5 verified):

  | Mutation | Admin.Api.Checks | Auth.Data.Checks |
  |---|---|---|
  | MB1 no 408 (both throws removed) | 356/4 | 162/0 |
  | MB2 the 408 does not mark warned, so it logs twice | 358/2 | 162/0 |
  | MB3 `Expect` requests drained | 358/2 | 162/0 |
  | MB4 global 1 MiB limit re-added | 359/1 | 162/0 |
  | MB5 no `Content-Length` pre-check (so `Expect` gets a 100) | 356/4 | 162/0 |
  | MB6 drain Warning carries an exception | 359/1 | 162/0 |

- **Observation (not changed):** after an `Expect` 413 the server closes the connection about 6 s after the response, because Kestrel closes it itself (logged as INFO in the checks). The client already has the full 413 at once.
- **OPEN (SSE, for TL):**
  - Is the 10 s whole-body deadline the right value, and should it stay a code default with no config key?
  - Is it right that PUT answers 408 before steps 3–6?
  - The S2A-P3-REVIEW original md5 has changed (c6f7258b vs the cited bfaa2efb).
- Evidence: `/tmp/sse-llc1/p3j-evidence.txt` (script `/tmp/sse-llc1/run/p3j-verify.sh`, a fresh copy of the template; k1 run for real with prefix `ff_kit_sseb1`).

## P3 C1 follow-up — TLD 10:02 Q1/Q2/Q3 + TLD 10:12 (commit A, on top of merge e159336)

Review cited: SSE2 GO on db8f360, `/tmp/sse2-fverify/C1-db8f360-REVIEW.md` md5 2caa1aba7c5952c89c10b9eaa02ac429 (snapshot `/tmp/sse-llc1/C1-db8f360-REVIEW.snapshot.md`). **C1-2 is Info only [TLD 2026-11-04 10:12, confirmed directly]:** no traversal rejects were added; the ARN suffix stays opaque.

- **Q1 [TLD 2026-11-04 10:02] config is the source of truth.** `Entra:SecretRef`, `Entra:Authority` and `Entra:ClientId` are read from config, never from the per-row copy. `NpgsqlLocationLoginConfigReader(NpgsqlDataSource, EntraDeployment)` no longer selects `oidc_authority` / `oidc_client_id` / `secret_ref`. For entra rows it fills `OidcAuthority`, `OidcClientId` and `SecretRef` from `Auth.Data.EntraDeployment` on every read; database rows get null. A null `EntraDeployment` throws `ArgumentNullException`. The admin API Super Admin view takes `oidc_*` and `secret.set` from options, and its `LlcRow` no longer selects the copies. The row still stores a copy because the DB CHECK needs it (db/ frozen). Nothing reads it, and Entra → Entra PUTs leave it untouched. Backlog: **V11-ROWSECRETCLEANUP** (spec §12). A config change applies to all entra locations after a restart, with no row rewrite. This is covered by the restart test in `Admin.Api.Checks` §4b: a second host plus reader with a new allowlisted ARN and client id; the row, the change rows and the events are unchanged. No SSE2 code was edited. `Login.Entra` reads `c.SecretRef` / `c.OidcAuthority` / `c.OidcClientId` through `ILocationLoginConfigReader`, unchanged.
- **TLD 2026-11-04 10:12 (final):** there is NO change to `Login.Abstractions` and no new file there; `LocationLoginConfig.SecretRef` is kept with the same type and redaction. The `Entra:SecretRef` validator stays in `src/Admin.Api/AdminApiOptions.cs` with the db8f360 rules. The file is identical to db8f360/8c3ccce except for two `using` lines and the added `Entra()` method, which runs `Validated()` and then builds the carrier. `Auth.Data/EntraDeployment.cs` (new) is a plain, non-validating carrier with null/empty guards and ToString `[redacted]`. `Login.Entra` does not bind `Entra:SecretRef` or call our validator. **Config is read once at startup; changes apply after a restart, no hot reload [TLD 2026-11-04 10:12].**
- **Q2 [TLD 2026-11-04 10:02] `secret {set, rotated_at}` is Super Admin only** (UX mockup cbb3ab4b). It appears in GET, POST 201 and PUT 200 for a Super Admin. For Partner Admin and org:V the key is **omitted, not null**. In the schema, `login_config_view_restricted` has no `secret` (closed, so a `secret` key or `secret: null` is rejected) and `access_rules.secret_visible_to` = `["super_admin"]`. The spec changed in §3.0/§3.1/§6/§6.1. Per-role checks cover create, GET, PUT and org:V.
- **Q3 [TLD 2026-11-04 10:02] confirmed:** both `secret_ref_required` and `secret_ref_format` are removed (as done in db8f360).
- **rotated_at fix:** it is now emitted only when `set` is true (entra). Before this, a database location with past `secret_ref` change rows could show `set:false` with a non-null `rotated_at`, which `secret_view` does not allow. A check was added. **OPEN (SSE, for TL):** a config-only rotation of `Entra:SecretRef` (restart) does not move `rotated_at`, because no change row is written (spec §13).
- **Auth.Data.Checks stays at exactly 162** (db/tests/k1-kit-tests.sh and db/README hard-code `162 passed, 0 failed`; db/ frozen). The reader checks were changed in place to assert config values that differ from the row's, and the new tests went into `Admin.Api.Checks` (now 339).
- **Mutation spot-checks (each restored; md5 verified):** MA1 reader uses row `secret_ref` → caught (Admin 1 FAIL, Auth.Data 161/1). MA2 secret removed from SA view → caught; MA2b secret for non-super → caught (7). MA3 `secret: null` for non-super → caught (7). MA4 stale view client id → caught (restart check). MA5 `rotated_at` on a database location → caught. MA6 PUT rewrites the copy on Entra → Entra → caught. MA7 validator ignores the allowlists → caught (5, incl. the TLD 10:12 `Entra()` check).
- Evidence: `/tmp/sse-llc1/p3i-evidence.txt` (script `/tmp/sse-llc1/run/p3i-verify.sh`, k1 run for real with prefix `ff_kit_ssea1`; TL 10:08 I-7: no static k1 line).

## TLD 9:42: Low follow-ups after SSE2 GO (commit after 63d1c1e)

- **Source:** SSE2 review `/tmp/sse2-fverify/KF-REWORK-REVIEW.md` md5 `8b91c5e96d2ae0bc76aa79af44b681d1`, snapshotted to `/tmp/KF-REWORK-REVIEW.8b91c5e9.md`. It gave GO on 7dff464 + 63d1c1e, and TL pushed 63d1c1e.
- **KF-G:**
  - **What:** a membership between two roles of the same install (for example the owner granted to `<db>_login`) passed everything with rc 0, which let the login role SET ROLE to the owner.
  - **Preflight:** a new line kind 12 refuses a kit role that is a member of another kit role.
  - **role.sql:** a new clause refuses any pg_auth_members row with the role as member or as granted role.
  - **Tests:** `owner TO login` and `login TO admin_api` on sh and ps1 (rc 5, nothing changed), plus the DO block run directly on both sides of each grant.
- **I-1:** the README's Known limits now say a failed run isn't atomic across steps, list what can be left behind, and give the recovery (`--force`, or `--force --drop`). The script headers point there.
- **I-3:** the README, the setup.sh `--help` and the setup.ps1 synopsis warn that `--password` / `-Password` (and the pwsh app-password parameters) put the password in argv and the process list. They recommend the existing hidden prompt or the `FF_*_PASSWORD` env vars. The kit has no password-file option, and the README says so.
- **I-4:** the live test now creates `ff_kit_kfadm` without a password and sets it through psql's stdin. The scripts get it only through PGPASSWORD in the child env. No test passes a password on a command line any more.
- **I-5:**
  - **Load:** passwordcheck is loaded per connection, only for the kit's superuser psql: `PGOPTIONS=-csession_preload_libraries=passwordcheck`, via `sudo -u postgres env …` for sh and `--preserve-env` for ps1.
  - **Deviation:** SSE2 used a private initdb cluster with shared_preload_libraries, not a session LOAD. I used a per-connection setting instead. It's the session-level equivalent and changes no server config (postgresql.conf untouched).
  - **Result:** sh and ps1 both give rc 5 with `floorfunds-kit: ALTER ROLE ff_kit_kfa_login failed (22023)`, and the login password is unchanged. No verifier and no inner message ('must not') are in the output or in the server-log lines after the offset, and the wrapped error was logged.
  - **Caveat:** the password equals the role name by design of this hook, so the cleartext-absence check is meaningless here. It's covered by the KF-A CREATE test instead.
- **Results:** k1-kit-tests 197/0. See `/tmp/sse-kf/evidence-kf4.txt`.

### I-6 follow-up (TL ruling, after SSE2 GO on b5fa2fa, review 781b53a8)

- **Finding (SSE2 I-6, Info, pre-existing):** `Get-Help ./db/setup.ps1 -Full` printed only the syntax line, so the I-3 -Password warning was visible only in the source.
- **Root cause (bisected in /tmp/i6):** the help block was not missing or misplaced. It already sat before `param()`. But `<#` was on the line directly after `#Requires -Version 7.0`. Removing the `#Requires` line or adding one blank line after it makes Get-Help render. Removing the comment line between `#>` and `param(` does not help.
- **Deviation from the ruling's wording:** TL said to move the block before param(), and it already was. I kept `#Requires` and the block position and added one blank line instead. That is the minimal fix the bisection supports.
- **Tests:** three new k1 checks (Get-Help shows SYNOPSIS, the WARNING (I-3) text, and EXAMPLE 1). There is no behaviour change to setup.ps1.
- **Results:** k1-kit-tests 200/0, prefix ff_kit_ssei6b. See `/tmp/sse-kf/evidence-kf6.txt`.

## Decisions (this slice)

- Project names: `Login.Abstractions` (mandated by F9). The data project will be `src/Auth.Data` (matches the `auth` schema; shared by the admin API and the login service, so not `Login.*`).
- Roles: per-DB `<db>_admin_api`, `<db>_login`, created by a superuser script (to be `src/Auth.Data/tools/create-app-roles.sh`); the migration grants and RAISEs if they are missing (spec §8.2).
- RLS off on the login tables (F7); delete-to-unassign for `user_location` (F10); `user_location` FK ON DELETE RESTRICT; status/provider mapping throws on unknown values (TLD 7:53).
- OQ-S2a-8 log: no `partners` table and no FK on `partner_id` in S2a (TLD 7:39).

## Assumptions / open

See spec §13 (A-21 server-filled OIDC keep/startup validation; A-22 Entra partner match). The P3 need for a .NET JSON Schema validator is met by the in-repo `src/Admin.Api/JsonSchemaLite.cs` (no package); the P3 open questions are listed under P3 below.

## Sources (md5)

MK9 `90d2a8fa0052d5496f8fc4c274bc779c` (D-103; was 939e41bd, then 9dae6040); UX error-copy review (D-102) `8c7d5274d549d45d18ae0415964e2236`; SSE2 F-verify `/tmp/sse2-fverify/F-STATUS-VERIFY.md` `70f77c7402918580e5980874f2d2adfd`; SSE2 review @c35863f `536109ccc2115b9c7185ecf08809aa85`; `src/Login.StandIn/fixtures.json` `612918cc3bd6c9453a3031d0a3668b15`.

## Toolchain

.NET SDK 10.0.403 / runtime 10.0.14 at `/home/box/.dotnet`. PostgreSQL 17 (box). PowerShell 7.6.9 installed from packages.microsoft.com debian/13 trixie prod, per TL's OK at 7:25, used for testing db/setup.ps1 only.
