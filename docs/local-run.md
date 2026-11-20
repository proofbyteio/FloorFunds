# Local run (laptop Postgres)

Owner path (DEC-339): get latest on your laptop, connect to **your own** Postgres, sign in as Super Admin / Partner Admin / Client Admin / Location Admin, and create / edit / view / test the org forms.

**Windows is first-class** — use the `.ps1` scripts. Bash twins are provided for macOS/Linux.

Cloud agents do **not** push or open PRs for this work; you pull/merge yourself after verifying.

---

## 1. Prerequisites

| Tool | Version (from repo) | Install |
|---|---|---|
| .NET SDK | **10.0** (`TargetFramework` `net10.0` in `src/Admin.Api/Admin.Api.csproj`, `Login.StandIn`, `Auth.Issuer`) | Windows: `winget install Microsoft.DotNet.SDK.10` · macOS: `brew install --cask dotnet-sdk` |
| Node.js | Any current LTS (shell is plain `node ui/admin-shell/server.mjs`; no `package.json` required) | Windows: `winget install OpenJS.NodeJS.LTS` · macOS: `brew install node` |
| PostgreSQL | **17** client + server (migrations assume PG17; `gen_random_uuid` / modern SQL) | Windows: `winget install PostgreSQL.PostgreSQL` · macOS: `brew install postgresql@17` |
| PowerShell 7 | Required for `.ps1` | Windows: usually present · macOS: `brew install powershell/tap/powershell` |
| `psql` on PATH | Same major as the server | Add Postgres `bin` to PATH (Windows often `C:\Program Files\PostgreSQL\17\bin`) |

Also need `curl` (Windows 10+ has it) for TOTP helper health checks.

---

## 2. Connect to your Postgres

Defaults (overridable):

| Setting | Env / flag | Default |
|---|---|---|
| Host | `FF_DB_HOST` / `-DbHost` / `--host` | `127.0.0.1` |
| Port | `FF_DB_PORT` / `-Port` / `--port` | `5432` (your laptop server — fine) |
| Superuser | `FF_PG_SUPERUSER` / `-AdminUser` | `postgres` |
| Superuser password | `PGPASSWORD` or peer auth | — |
| Database + owner role | `FF_DB_NAME` / `-Db` | `floorfunds_local` |
| Role | `FF_DB_ROLE` / `-Role` | same as db name |

`db/setup.sh` and `db/setup.ps1` accept host/port/db/role via **CLI** and, as of this tip, via **`FF_DB_*` env defaults** when CLI is omitted. They never hard-require a specific database name.

**Passwords for kit roles** (`FF_DB_PASSWORD`, `FF_ADMIN_API_PASSWORD`, `FF_LOGIN_PASSWORD`, `FF_ISSUER_PASSWORD`): set in the environment, or let `init-db` generate them into `.local-run/secrets.env` (git-ignored, `0600` / Windows ACL). **Never commit passwords. Docs never print them.**

---

## 3. One-time database create

### Windows (preferred)

```powershell
cd <repo>
# Optional: $env:PGPASSWORD = '<your postgres superuser password>'
pwsh -File .\scripts\local\init-db.ps1
# If the DB already exists and you intend to wipe it:
pwsh -File .\scripts\local\init-db.ps1 -Reset
```

### macOS / Linux

```bash
cd <repo>
# Optional: export PGPASSWORD='...'
bash scripts/local/init-db.sh
bash scripts/local/init-db.sh --reset   # wipe + recreate
```

What it does:

1. **Refuses** if `floorfunds_local` already exists unless `-Reset` / `--reset`.
2. Runs `db/setup.ps1 -Seed` / `db/setup.sh --seed` (migrations + kit TEST-ONLY seed).  
   **SA L-6:** `setup.ps1 -Seed` plants `db/kit/sse2-fake-partners.sql` the same way `setup.sh` does.
3. Loads **`scripts/local/review-seed.sql`** (review-only; not part of kit seed).
4. Writes `.local-run/secrets.env` (connection strings + role passwords).

Touches **only** the dedicated kit database/roles. It does not alter other databases on your server.

---

## 4. Review users (sign-in)

| Username | DB role_wire | Notes |
|---|---|---|
| `local_sa` | `super_admin` | Full create/edit/view |
| `local_pa` | `partner_admin` | Own partner (Partner A) |
| `local_ca` | `client_admin` | Own client; **no** partners list (DEC-331) |
| `local_la` | `supervisor` → Location Admin | **2+ active locations** under one client (DEC-334 picker) |
| `local_la_one` | `supervisor` → Location Admin | Single location → lands on detail |

Stand-in fixtures: `scripts/local/review-fixtures.json` (includes base fixture users plus the `local_*` users).  
CA/LA use stand-in `admin_type=partner` so `/admin` + Issuer accept them; **authorization still comes from `auth.resolve_grant`** via matching `user_id`.

Passwords: generated at stand-in start into `.local-run/standin-passwords.json` (copy of `fixture-secrets.json`).  
TOTP: `scripts/local/totp.ps1 local_sa` or `scripts/local/totp.sh local_sa`.

---

## 5. Start / stop

### Windows

```powershell
pwsh -File .\scripts\local\start.ps1
# other terminal:
pwsh -File .\scripts\local\totp.ps1 local_sa
pwsh -File .\scripts\local\stop.ps1
```

### macOS / Linux

```bash
bash scripts/local/start.sh          # shell stays in foreground
bash scripts/local/totp.sh local_sa
bash scripts/local/stop.sh
```

Bound to **127.0.0.1** only. Default ports (override with `FF_SHELL_PORT`, `FF_STANDIN_PORT`, `FF_API_PORT`, `FF_ISSUER_PORT`):

| Service | Port |
|---|---|
| Admin shell | 5099 |
| Stand-in front / token / control | 18280 / 18281 / 18282 |
| Admin.Api | 18290 |
| Auth.Issuer | 18291 |

PID files and logs: `.local-run/*.pid`, `.local-run/*.log`.

Open:

- Shell: `http://127.0.0.1:5099/`
- Admin sign-in: `http://127.0.0.1:5099/standin/admin`

---

## 6. Role matrix (verified by R7 HTTP evidence, tip of this branch)

| Action | SA | PA | CA | LA |
|---|---|---|---|---|
| Landing after sign-in (DEC-333/334) | Partners list | Clients list | Locations list | Home picker (2+) or detail (1) |
| GET partners list (DEC-331) | 200 | 200 (own) | **403** | **403** |
| GET clients list | 200 | 200 | — (my_client) | **403** |
| GET locations list | 200 | 200 | 200 (scoped) | 200 (assigned only) |
| Create partner | yes | no | no | **no** (DEC-335) |
| Create client | yes | yes | no | **no** (DEC-335) |
| Create location | yes | yes | no (OPEN: server `IsAdmin`) | **no** (DEC-335) |
| Edit partner | yes | no | no | no |
| Edit client | yes | yes | no | no |
| Edit location | yes | yes | no | no |
| Activate / deactivate | SA partner; SA/PA client+location | PA client+location | no | no |
| **Create Partner Admin (user)** | **Deferred (DEC-336 / DEC-337)** | — | — | — |

HTTP evidence this round: `evidence/r7-02-counts.txt` → **pass=51 fail=0** (includes DEC-331 CA/LA partners 403, DEC-335 LA create 403s, DEC-334 LA multi `total=2`).

---

## 7. Known OPEN items

See `REQ068-BUILD-R7.md` finding table. Highlights:

- **DEC-336 / PQ-362** Create Partner Admin — deferred (DEC-337); blocked by `no-revoke-or-assign-api` + credential-store design (§6d).
- **PQ-363 / UX-R16** dummy-data banner — HELD.
- **UX-R14** seed name padding — HELD (DEC-263).
- **PQ-366 / PQ-367** — still OPEN as listed in R7.
- **CA Create location** — OPEN (server does not grant `create_location` to CA).
- **SA L-8 / I-2** — NOT FIXED (EF migrator drift / cannot split existing migration txns).

---

## 8. Troubleshooting

| Symptom | Fix |
|---|---|
| Port in use | `stop.ps1` / `stop.sh`, or change `FF_*_PORT`; on Windows `Get-NetTCPConnection -LocalPort 5099` |
| Execution policy | `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` |
| `psql` / `pg_ctl` not found (Windows) | Add `C:\Program Files\PostgreSQL\17\bin` to PATH; open a new terminal |
| Firewall prompt | Allow Node / `dotnet` for private networks (loopback-only binds should not need it) |
| `Refused: database … already exists` | Intentional — pass `-Reset` / `--reset` only when you mean to wipe **that** kit DB |
| TOTP 404 | Start services first; control port is stand-in base **+2** |
| Auth.Data.Checks / suite “not post-seed” | Do **not** run review-seed into the suite kit DB; review-seed is local-run only |

---

## 9. Git-ignored local files

`.local-run/` (secrets, PIDs, logs, stand-in state, passwords). Never commit.
