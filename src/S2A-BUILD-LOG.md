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
  - KF1 doesn't look at roles that were granted membership *in* a kit role. That was out of scope; ask TL if it's wanted.
  - setup.ps1 has never run on Windows.
  - NU3018 still needs re-checking on a normally networked machine.

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
