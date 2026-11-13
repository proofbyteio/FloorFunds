# Admin shell (dev only)

One URL: <http://127.0.0.1:5088/>

**Windows (this machine):** run `pwsh -File ui/admin-shell/start.ps1` (requires `FF_PG_SUPERUSER_PASSWORD`). Prefer free ports 18080/18081/18090/18091/5088; if 18080 is busy the script uses **5188 / 19080–19082 / 19090 / 19091**. Then: `$env:REQ064_DEV_SEED=1; $env:STANDIN_ISSUER='http://127.0.0.1:<shell-port>/standin'; $env:FF_PG_SUPERUSER_PASSWORD=…; pwsh -File ui/admin-shell/seed-dev-assignment.ps1`. Open the shell URL → Sign in as `super_admin`. TOTP from the control port (`base+2`). Password from `%TEMP%\ff-db\admin-shell-standin\standin-<base>\fixture-secrets.json`.

The supported path on the Linux box is `ctl.sh`. `ui/admin-shell/start.sh` is the in-repo bash script and uses the same host settings. It refuses to run unless this machine is the dev stand-in (`ADMIN_DEV_SHELL=1`, `ASPNETCORE_ENVIRONMENT=Development`, loopback only). It drops and recreates the synthetic kit database `ff_kit_adminshell` on `127.0.0.1:5432` when that database already carries the `floorfunds-kit` marker, then loads the current migrations and the seed through `db/setup.sh`. A database of that name without the marker is refused. Passwords are generated in the script. It does not read `/tmp/req064-env.sh` or any file outside this repo. It starts the stand-in, the issuer, Admin.Api, and this shell only after the listen port is free, and it accepts the listener only when the process is the one this script started. Admin.Api is given `BackofficeAuth__IssuerUrl` and is not given `BackofficeAuth__JwksPath`, the issuer connection string, or the issuer secret. The browser uses the one URL. `/api` is proxied to Admin.Api and `/standin` is proxied to the stand-in. The access token stays on the server. An expired, revoked, or logged-out session is HTTP 401 and the shell goes to `/signin`. It does not reopen the session.

The script does not grant the fixture Super Admin or the fixture Partner Admin. Those assignments are a separate dev-only step:

`REQ064_DEV_SEED=1 ui/admin-shell/seed-dev-assignment.sh`

The seed refuses to run unless `REQ064_DEV_SEED=1` is set, the environment is not Production, and the target is a local disposable database: host `127.0.0.1`, `localhost`, or `::1`, port `5432` or `5433`, name `ff_kit_*`, and the `floorfunds-kit` marker. It reads `CONNECTION_STRING` or the file `start.sh` writes at `/tmp/ff-db/adminshell.env`. It does not read `/tmp/req064-env.sh`.

Sign in as the fixture Super Admin after that seed. The generated password is in the stand-in state directory `fixture-secrets.json` (mode 0600, under `/tmp`, not in this repo). The one-time code comes from the stand-in control port, which is not proxied.

The menu is `GET /api/nav` from the resolved grant. A screen that is not in the grant is also refused by Admin.Api. Clients is a placeholder: the screen loads, and the write stays held.

`/dev-session` only attaches a session this process already created through the stand-in. It does not accept a token.

Screens 13–26 are not in this shell.

## Partner picker (PQ-284)

Location create asks the server for existing partners (`GET /api/screens/location-partners`) and offers them in a select. A partner id cannot be typed. The shell also refuses a create whose partner id is not in that list. This is a reversible default while PQ-284 is open. To swap it, change `fillPartners()` in `shell.js` and `enforceExistingPartner()` in `server.mjs` together.
