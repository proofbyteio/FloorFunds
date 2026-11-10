# Admin shell (dev only)

One URL: <http://127.0.0.1:5088/>

Start it with `ui/admin-shell/start.sh`. The script refuses to run unless this machine is the dev stand-in (`ADMIN_DEV_SHELL=1`, `ASPNETCORE_ENVIRONMENT=Development`, loopback only). It starts the stand-in, Admin.Api, and this shell. The browser uses the one URL. `/api` is proxied to Admin.Api and `/standin` is proxied to the stand-in. The access token stays on the server.

Sign in as the fixture Super Admin. The generated password is in the stand-in state directory `fixture-secrets.json` (mode 0600, under `/tmp`, not in this repo). The one-time code comes from the stand-in control port, which is not proxied.

The menu is `GET /api/nav` from the resolved grant. A screen that is not in the grant is also refused by Admin.Api. Clients is a placeholder: the screen loads, and the write stays held.

`/dev-session` only attaches a session this process already created through the stand-in. It does not accept a token.

Screens 13–26 are not in this shell.

## Partner picker (PQ-284)

Location create asks the server for existing partners (`GET /api/screens/location-partners`) and offers them in a select. A partner id cannot be typed. The shell also refuses a create whose partner id is not in that list. This is a reversible default while PQ-284 is open. To swap it, change `fillPartners()` in `shell.js` and `enforceExistingPartner()` in `server.mjs` together.
