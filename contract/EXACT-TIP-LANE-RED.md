# Exact-tip lane-red decisions


## Exact-tip lane-red follow-up (2026-11-12 06:23:06 PST)

Parent tip `96410c7537ab61c81f42020b905a7b486a31137e` finished with both lanes red. Intermediate `52ed0a47` is void as a final tip (f20 expect was switched to the sentinel; admin-signout was a brittle grep). Causes and fixes:

### 1. m5-admin-type-branch / r-public-temp (harness)

- **Failing check names:** none from the suite — both fail and pass logs are only `Unhandled exception: … No such file or directory` for `Catalog.Query.Checks` / `Auth.Data.Checks` (QUERY/AUTH run with `--no-build` and no `build=`).
- **Post-restore git diff:** empty (only `?? .cursorignore`). Reproduce: apply m5 Grants.sql patch → `git diff` shows the one-line change → `git checkout --` restore → `git diff` is `''`.
- **r-public-temp** restore is SQL `REVOKE TEMP` (no source files); same missing AUTH binary on both legs.
- **Not** dirty DB/worktree after restore. **Fix:** pass `QUERY_BUILD` / `AUTH_BUILD` into those `cycle(...)` calls (kept from 52ed0a47).

### 2. f20 (Medium — test validity)

- Under the mutant, source-string checks still PASS; `orgs-route-mutant-applied` / `mutant-orgs-server-route-removed-killed` are tautologies on the test's own mutated text — not kill evidence.
- **Fix:** `orgs-routes.test.mjs` starts `server.mjs` from disk and HTTP GETs `/orgs/{p}/locations/new` and `/orgs/{p}/locations/{l}/sign-in`. Under f20 those return 404 → `FAIL server-serves-orgs-new` / `FAIL server-serves-orgs-sign-in`. Href mutants are confirmed with live GETs (`every-orgs-href-served-by-live-server`). Runner expect reverted to real FAIL lines (not the sentinel).

### 3. orgs-e2e finish()

- Strict MEDIUM-2: own assert for A1.3 truncate PASS line; own assert for exact `PASS cleanup: last-SA delete guard still fires (P0001)`; ui-host must exit 0 with summary `N passed, 0 failed`.

### 4. admin-signout authorize-hands-off-to-admin-sign-in

- Brittle grep for `return Results.Redirect(issuerPath + "/admin?ret=")` was **stale**: product builds `var admin = …` then `Results.Redirect(admin)` (F-TL-B/DEC-220).
- Behavioral check (`--admin-handoff-check`) proved a **product bug**: path-relative `Results.Redirect("/standin/admin?ret=…")` emits `Location: /standin/admin%3Fret=…` (query path-encoded), so the handoff URL is not `/admin?ret=` + escaped ret.
- **Product fix:** redirect with an absolute URL from `issuer` (`issuer.TrimEnd('/') + "/admin?ret=" + …`). Grep replaced by the HTTP 302 behavioral check + a mutant that breaks the Location and is killed.

### 5. Citations

- Re-anchored `cite-definer-owner.sql` lines to **78** (`CREATE POLICY login_cutoff_owner`) and **81** (`GRANT EXECUTE ON FUNCTION auth.resolve_grant(text, uuid)`), matching current `db/kit/definer-owner.sql` text (not just numbers).

### 6. catalog-nav

- Suite wrapper builds `Catalog.Query.Checks` (and Auth.Data.Checks) so catalog-nav's API checks restore/run on a clean worktree.

## Admin handoff Location repro (2026-11-12 06:31:05 PST) — claimed, not reproduced

**(a) Config-only issuer.** `Program.cs:28-29`: `issuer` from `cfg["StandIn:Issuer"]` or `http://localhost:{ports.Front}/standin`; `issuerPath = new Uri(issuer).AbsolutePath`. Never from `Host`, `X-Forwarded-Host`, or request scheme.

**(b)/(c) Probe.** Exact raw `Location` equality to `issuerPath + "/admin?ret=" + Uri.EscapeDataString(expectedRet)` (path-relative). Forged `Host` / `X-Forwarded-Host: evil.example` + `X-Forwarded-Proto: https` must yield the same Location. Two mutants in `ui/admin-shell/admin-signout.test.mjs` (not the Python runner): `mutant-admin-handoff-broken-path-killed` and `mutant-admin-handoff-forged-host-killed`.

**Repro.** `/tmp/wt-handoff-before` at `96410c75` with `issuerPath + "/admin?ret="` — curl did **not** show `admin%3Fret=`. Product change to absolute `issuer` URL reverted. Prior HttpClient `admin%3Fret=` observation was probe artifact (Uri/PathAndQuery), not Kestrel output.

### before.txt Location
```
Location: /standin/admin?ret=%2Fstandin%2Fconnect%2Fauthorize%3Fclient_id%3Dadmin_dev_shell%26redirect_uri%3Dhttp%253A%252F%252F127.0.0.1%253A42075%252Fsignin-oidc%26response_type%3Dcode%26scope%3Dopenid%2520backoffice_api%26state%3Dst1%26code_challenge%3DcdsJ0n5bq8EeKfVOeBDSoiaIy0V1r-aJdXJL0QU7KZI%26code_challenge_method%3DS256
```

### after.txt Location (reverted path-relative product)
```
Location: /standin/admin?ret=%2Fstandin%2Fconnect%2Fauthorize%3Fclient_id%3Dadmin_dev_shell%26redirect_uri%3Dhttp%253A%252F%252F127.0.0.1%253A43673%252Fsignin-oidc%26response_type%3Dcode%26scope%3Dopenid%2520backoffice_api%26state%3Dst1%26code_challenge%3DttpxRVCDa42tfmdTCFzyY3gTe_E6jbXej4GSNJiyXZI%26code_challenge_method%3DS256
```
