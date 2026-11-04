# FloorFunds local database kit

This folder lets you create the FloorFunds Postgres database on your own
machine and run the latest code against it.

Status: slice S2a. The migration, roles step, test seed and DB checks are in
place. The admin API (S2a P3) and the Entra sign-in (S2b) build on this database.

## 1. Install PostgreSQL 17

Overview of all platforms: https://www.postgresql.org/download/

**Windows**: use the EDB installer.
- https://www.postgresql.org/download/windows/
- https://www.enterprisedb.com/downloads/postgres-postgresql-downloads
- Pick version 17. Keep the default port, 5432. Remember the `postgres` superuser password you set during install.

**macOS**: use either Homebrew or Postgres.app.
- Homebrew (https://formulae.brew.sh/formula/postgresql@17):
  ```
  brew install postgresql@17
  brew services start postgresql@17
  ```
  Homebrew makes your macOS user a superuser, so plain `psql` works.
- Postgres.app (https://postgresapp.com/): install it, then add a server for version 17.
- See also https://www.postgresql.org/download/macosx/

**Linux (Debian/Ubuntu)**: use the PGDG apt repository.
- https://www.postgresql.org/download/linux/ubuntu/
- https://www.postgresql.org/download/linux/debian/
- https://wiki.postgresql.org/wiki/Apt
- Then: `sudo apt install postgresql-17`

Check that it works: `psql --version` should show 17.x.

## 2. Create the roles and database, apply the migrations (one script)

`db/setup.sh` (macOS / Linux) and `db/setup.ps1` (Windows, PowerShell 7) do the
same four steps. A second run on the same names is refused unless you add
`--force` / `-Force` (see "What the kit will and will not touch" below).

1. **Owner role + database** (as a Postgres superuser): a login role (default
   `floorfunds_dev`) and a database it owns. CONNECT is revoked from PUBLIC.
2. **App roles** (as a superuser; spec §8.2): `<db>_admin_api` (the admin API)
   and `<db>_login` (the login service). Both are plain login roles: no
   superuser, no BYPASSRLS, no CREATEDB/CREATEROLE/REPLICATION, and they own
   nothing. The migration refuses to run if either is missing or privileged.
3. **Migrations**: applies `db/migrations.sql` **as the owner role** over TCP.
   This is the only way the `auth` schema and tables are created. The script is
   idempotent; re-running it is a no-op. Skip with `--no-migrate` / `-NoMigrate`.
4. **Test seed** (optional, `--seed` / `-Seed`): loads `db/seed/*.sql`. See step 5.

**macOS / Linux**: `db/setup.sh`
```
# Linux (runs the superuser psql as the postgres OS user via sudo; this is the default)
./db/setup.sh                                  # db and owner role: floorfunds_dev; prompts for 3 passwords
./db/setup.sh --seed                           # same, plus the TEST-ONLY seed

# macOS Homebrew (you are already a superuser)
./db/setup.sh --superuser-cmd "psql"
```
Options:
- `--db NAME` and `--role NAME`. Both default to `floorfunds_dev`. Names must match `^[a-z_][a-z0-9_]{0,62}$`; `--db` is at most 53 characters so that `<db>_admin_api` fits.
- Owner password: `--password PW`, or env `FF_DB_PASSWORD`, else a hidden prompt.
- App-role passwords: env `FF_ADMIN_API_PASSWORD` and `FF_LOGIN_PASSWORD`, else hidden prompts.
- Prefer the prompts: `--password` can end up in your shell history. No password is ever printed.
- `--host` (default `127.0.0.1`) and `--port` (default `5432`): where the owner connects to apply the migrations and the seed (TCP, password auth). Also used in the printed connection strings.
- `--admin-host H`: where the superuser psql connects. The default is the local socket.
- `--superuser-cmd "CMD"`: the command that runs psql as a superuser. The default is `sudo -u postgres psql`.
- `--no-migrate`, `--migrations FILE` (default `db/migrations.sql`), `--seed`.
- `--force`: allowed only on objects the kit itself created. Re-sets the three role passwords and re-applies the migrations (and the seed with `--seed`).
- `--drop`: needs `--force`. Drops and recreates **only** the named database, and only if the kit created it and `--role` owns it (see step 7).

**What the kit will and will not touch** (both scripts)
- Every database and role the kit creates gets the comment `floorfunds-kit` (`COMMENT ON DATABASE/ROLE ... IS 'floorfunds-kit'`). That marker is how the kit recognises its own objects.
- Before any SQL that changes anything, a read-only preflight (`db/kit/preflight.sql`) checks the names. The script stops with exit code **5** and a `Refused: ...` message, changing nothing, if:
  - `--db` is `postgres`, `template0`, `template1` or any template database;
  - a role name is `postgres` or starts with `pg_`;
  - the owner role or an app role is a superuser (even if it carries the marker);
  - the owner role, an app role or the database already exists **without** the marker (someone else's object);
  - the `--role` role owns any database other than `--db`;
  - `--db` exists but is owned by someone other than `--role`;
  - the owner role or an app role has `BYPASSRLS`, `CREATEROLE`, `CREATEDB` or `REPLICATION` (the kit creates its roles with none of them, and they need none);
  - the owner role or an app role is a member, directly or through other roles, of any role other than this run's own kit-marked roles (for example `pg_read_all_data`, `pg_write_server_files`, any `pg_*` role, a superuser or `BYPASSRLS` role, an unmarked role, or another kit install's role). The kit grants no role memberships, so any membership was added by someone else;
  - the preflight's output does not end with its end marker (empty or cut-off output is a refusal, not a pass).

  `--force` / `-Force` never overrides these.
- If the kit's own objects already exist, a run without `--force` stops with exit code 5 and changes nothing.
- The kit never runs `ALTER ROLE` on a role it did not create and never changes a database's owner. The changing SQL re-checks after the preflight:
  - `db/kit/role.sql`: one `DO` block (one statement, one transaction) checks the role and then creates it and marks it, or alters it. It alters only a marked, non-superuser role with none of the attributes and memberships above. Otherwise it raises an error and changes nothing.
  - `db/kit/database.sql`: PostgreSQL does not allow `CREATE DATABASE` or `DROP DATABASE` inside a transaction or a `DO` block, so they cannot share a statement with their check. Before `DROP`, a `DO` block checks the database (marked, owned by `--role`, which is not a superuser, and not a template), and the `DROP` itself is generated only for a database that still matches those conditions. After `CREATE`, one `DO` block re-checks the new database (owner `--role`, not a template, no comment yet) and then marks it and sets `CONNECT` in one transaction. If that re-check fails, the database is left unmarked, so every later run refuses it.
  - What is left: between those separate statements, another superuser working at the same moment could swap in a different object with the same name. That needs superuser rights, which can already do anything to the server.
- A password containing a newline or carriage return is refused (exit code 2) before anything runs.
- Exit codes: 0 done; 2 bad arguments or password; 3 psql not found; 4 migrations or seed failed; 5 refused, or a kit guard stopped a change.
- Tests: `db/tests/k1-kit-tests.sh` (engineers' box: needs `sudo -u postgres` and pwsh). It runs every refusal against both scripts, first with a stub psql that touches no database (including `postgres`, `template0/1`, `ff_sse`, `ff_sse2` with `--force --drop`), then live on scratch objects that it creates and drops. Every scratch name is built from one prefix (`KIT_TEST_PREFIX`, default `ff_kit_kf`), and cleanup removes everything with that prefix.

**Known limits** (read before you rely on the kit)
- `db/setup.ps1` has been tested only with pwsh 7.6.9 **on Linux**. It has never been run on Windows.
- Package signature revocation (NuGet warning NU3018) could not be checked on the engineers' box, because it cannot reach the certificate revocation servers. Re-check it on a machine with normal network access before release, starting from an empty NuGet cache.
- Scope of the KF1/KF2 fixes: they cover the three roles this run names (`--role`, `<db>_admin_api`, `<db>_login`) and the one `--db`. They check what those roles are members of, not which other roles were granted membership in them. They do not remove memberships or attributes, they refuse instead. They do not close the window between `CREATE`/`DROP DATABASE` and their checks (see above).

**Windows**: `db/setup.ps1` (needs PowerShell 7, and psql on PATH, e.g. `C:\Program Files\PostgreSQL\17\bin`).
```
./db/setup.ps1                                   # floorfunds_dev; prompts for the passwords
./db/setup.ps1 -Db floorfunds_dev -Role floorfunds_dev -Force -Drop -Seed   # rebuild the kit's own DB
```
- Same options as setup.sh: `-Db`, `-Role`, `-Password`, `-AdminApiPassword`, `-LoginPassword` (SecureStrings; otherwise env `FF_DB_PASSWORD`, `FF_ADMIN_API_PASSWORD`, `FF_LOGIN_PASSWORD`; otherwise hidden prompts), `-Host`, `-Port`, `-AdminHost`, `-AdminUser`, `-Force`, `-Drop`, `-NoMigrate`, `-Migrations`, `-Seed`.
- The admin connection defaults to `psql -U postgres -h localhost`. The postgres password is read from `PGPASSWORD` if it is set; otherwise you are prompted. Use `-AdminHost ''` to connect through the local socket instead (Linux/macOS).
- **Tested only with pwsh 7.6.9 on Linux**, installed from packages.microsoft.com (debian/13 trixie, prod) per TL's OK, run as the `postgres` OS user with `-AdminHost ''`. **Untested on Windows**: the Windows admin login over TCP with the postgres password, finding psql on PATH, and the EDB install directory.

## 3. Connection strings (never in a repo file)

The scripts print three templates (passwords are the ones you entered):
```
owner:     Host=127.0.0.1;Port=5432;Database=floorfunds_dev;Username=floorfunds_dev;Password=<password>
admin API: Host=127.0.0.1;Port=5432;Database=floorfunds_dev;Username=floorfunds_dev_admin_api;Password=<password>
login:     Host=127.0.0.1;Port=5432;Database=floorfunds_dev;Username=floorfunds_dev_login;Password=<password>
```
The migration runner and the DB checks read them from env vars:
`CONNECTION_STRING` (owner), `ADMIN_API_CONNECTION_STRING`, `LOGIN_CONNECTION_STRING`.
```
# macOS / Linux
export CONNECTION_STRING='Host=127.0.0.1;Port=5432;Database=floorfunds_dev;Username=floorfunds_dev;Password=<password>'
# Windows PowerShell
$env:CONNECTION_STRING = 'Host=127.0.0.1;Port=5432;Database=floorfunds_dev;Username=floorfunds_dev;Password=<password>'
```
Never put a password in appsettings*.json or any other file in the repo.
Connection-string reference: https://www.npgsql.org/doc/connection-string-parameters.html

## 4. Apply the migrations (creates the `auth` schema)

`setup.sh` / `setup.ps1` already did this (step 2.3). To do it by hand:

**Option A: plain psql, no .NET needed**
```
psql "host=127.0.0.1 port=5432 dbname=floorfunds_dev user=floorfunds_dev" -f db/migrations.sql
```
`db/migrations.sql` is generated by the repo's migration runner
(`src/Auth.Data.Migrator`, `script` command: EF Core `GenerateScript`,
idempotent), so it is safe to run more than once. Its first statement stops
with "S2a: app roles … must exist" if the app roles are missing.

**Option B: the migration runner** (.NET 10 SDK; run from `src/`, with `CONNECTION_STRING` set to the owner)
```
dotnet run --project Auth.Data.Migrator -- update        # apply all (EF Core MigrateAsync)
dotnet run --project Auth.Data.Migrator -- list          # applied / pending
dotnet run --project Auth.Data.Migrator -- update 0      # revert everything (test DBs only)
dotnet run --project Auth.Data.Migrator -- script ../db/migrations.sql   # regenerate the script
```
There is no `dotnet ef` tool: the migrations are hand-written EF Core
migration classes (TL ruling 2026-11-04 7:56 AM PT). The runner uses only the
approved runtime packages.

## 5. Load the seed data (TEST-ONLY)

`db/seed/` is **test-only**: `--seed` / `-Seed`, or by hand as the owner:
```
psql "host=127.0.0.1 port=5432 dbname=floorfunds_dev user=floorfunds_dev" -f db/seed/s2a-test-app-user.sql
```
It loads the 7 SI1 fixture users into `auth.app_user` (INTERIM table) and is
idempotent. Every id in it is fake and marked `SSE2-FAKE-TEST-ONLY`. There is
no real data. Never load it into a database with real data.

## 6. Run and test

**Prerequisites**
- PostgreSQL 17 (step 1), with TCP password login on `127.0.0.1` (the default for the installers above).
- `psql` 17 on PATH.
- .NET SDK 10.0.4xx (only for the runner and the checks): https://dotnet.microsoft.com/download/dotnet/10.0
- Network access to nuget.org for the first restore. `src/nuget.config` only accepts the approved package ids, signed by the approved owners.

**Setup**: step 2, with `--seed` (`-Seed`) if you will run the checks.

**Run the DB checks** (162 checks: schema, roles, grants, RLS, constraints, concurrency, readers). They write test rows
and change constraints inside rolled-back transactions, so use a **throwaway** database (e.g. `--db ff_kit_test`),
never one with real data. Name it `ff_kit_<something>`: the checks take the target from `AUTH_CHECKS_DB`,
refuse any name other than `ff_kit_*` (or the engineers' `ff_sse` / `ff_sse2`), and refuse if a connection string points elsewhere.
```
./db/setup.sh --db ff_kit_test --role ff_kit_test --seed
export CONNECTION_STRING='Host=127.0.0.1;Port=5432;Database=ff_kit_test;Username=ff_kit_test;Password=<owner password>'
export ADMIN_API_CONNECTION_STRING='Host=127.0.0.1;Port=5432;Database=ff_kit_test;Username=ff_kit_test_admin_api;Password=<admin api password>'
export LOGIN_CONNECTION_STRING='Host=127.0.0.1;Port=5432;Database=ff_kit_test;Username=ff_kit_test_login;Password=<login password>'
cd src
AUTH_CHECKS_DB=ff_kit_test dotnet run --project Auth.Data.Checks -- Login.StandIn/fixtures.json
```
Expected last line: `162 passed, 0 failed`. The checks delete the rows they add, so they can be run again on
the same database. They first check that the database is in the state the seed leaves it in; if it is not
(for example, leftover rows from an earlier interrupted run), they stop with exit code 3 and change nothing.
Rebuild it with `--force --drop --seed`. Other checks (no database needed), from `src/`:
```
dotnet run --project Login.Abstractions.Checks        # expect: 52 passed, 0 failed
```
Then drop the throwaway database (as a superuser; the kit has no "delete" command): `DROP DATABASE ff_kit_test WITH (FORCE);` and, if you like,
`DROP ROLE ff_kit_test_admin_api, ff_kit_test_login, ff_kit_test;`.

**Run the app**: the admin API (S2a P3) instructions are added with that code.
The SI1 login stand-in (`src/Login.StandIn`) does not use this database.

## 7. Drop and rebuild

```
./db/setup.sh --force --drop          # add --db/--role/--superuser-cmd/--seed as in step 2
./db/setup.ps1 -Force -Drop
```
This works only on a database the kit created (it carries the `floorfunds-kit` marker) and that `--role` owns.
Anything else is refused (exit code 5). It drops **only** the named database (using `DROP DATABASE ... WITH (FORCE)`,
which disconnects active sessions), recreates it, and re-applies the migrations
(and the seed with `--seed`). The roles are kept.

## 8. Differences: engineers' box vs your machine

| | Engineers' shared box | Mohanraj's machine |
|---|---|---|
| Databases | One per engineer: `ff_sse` and `ff_sse2` | One dev database: `floorfunds_dev` |
| Server | localhost:5432 (Postgres 17, cluster 17/main) | your local Postgres 17 |
| Passwords | Generated, stored in `/tmp/ff-db/<db>.env` (mode 0600), never committed | Chosen by you, kept in an env var or user-secrets |
| Create / reset | `/workspace/cachd-ones/ff-db/ff-db.sh` (outside the repo) + `src/Auth.Data/tools/create-app-roles.sh` | `db/setup.sh` or `db/setup.ps1` |

The SI1 login stand-in (`src/Login.StandIn`) still runs on in-memory fixtures
and does not use Postgres. Only the S2a/S2b code (location login config and
Entra sign-in) uses this database.
