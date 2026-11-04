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

MK9 `939e41bd0d087a35e1f00688dca53a6c`; SSE2 F-verify `/tmp/sse2-fverify/F-STATUS-VERIFY.md` `70f77c7402918580e5980874f2d2adfd`; SSE2 review @c35863f `536109ccc2115b9c7185ecf08809aa85`; `src/Login.StandIn/fixtures.json` `612918cc3bd6c9453a3031d0a3668b15`.

## Toolchain

.NET SDK 10.0.403 / runtime 10.0.14 at `/home/box/.dotnet`. PostgreSQL 17 (box). PowerShell 7.6.9 installed from packages.microsoft.com debian/13 trixie prod, per TL's OK at 7:25, used for testing db/setup.ps1 only.
