Earlier decision history is kept outside the repo and is not published here.

## DEC-238

Owner Mohanraj (cu:509531342) in Project Manager chat (PM pos ~1918, 2026-11-12):

> commit  and push the changes if it looks good, even if there is few minor question please make a note and checkin the code. I need this changes completed in 6 hours

## DEC-239

Owner Mohanraj (cu:509531342) in Project Manager chat (PM pos 1975, 2026-11-12 8:13 AM PT):

> Has it been completed. If so we can push the code to main branch

## DEC-240

Owner Mohanraj (cu:509531342) in Project Manager chat (PM pos 1989, 2026-11-12 ~10:07 AM PT):

> Let's approve the PR and merge to main

## Accepted Lows (PR #5)

Copied verbatim from `outputs/req064-119b0e95-pr-body-final.md` (sha256 `fcf0347e32ff57c8116ac533fd521ccfcab9773324ab3478a7b7cf006be2ddd1`).

### SA

- **L-1 Low — `history-partner-function-follows-the-record` stays HELD**
  `Req064Suite.cs:1070` still prints the HELD; covered by `HistoryLabel` at `Req064Suite.cs:217` (record partner `Pb`). Fix: leave direct `EXECUTE` withheld; keep the label check. (SA report L33–37; file:line per SA report.)

- **L-3 Low — `setup.sh --force --no-migrate` on main still runs the catalog transfer too early**
  `db/setup.sh:221-235` still applies `catalog-definer-owner.sql` whenever schema `catalog` exists, before `history_partner` exists on `main` at `18372896`. Fix: skip the catalog transfer until `catalog.history_partner` exists, or order migrations ahead of that file. (SA report L39–43.)

- **L-4 Low — `--force` still drops a database whose history contains the 13 intermediate ids**
  `db/kit/database.sql:34-57` still sets the drop flag when history contains any of the 13 listed ids (including ids a ce29 database already has). Fix: do not treat those ids as a recreate signal when `20261111180000_Req064SessionClose` is already present, or align the design sentences with `database.sql`. (SA report L45–49.)

- **L-5 Low — production layout still does not assert that `_test_seed` is absent** (NEW)
  `KitAdmin.cs:352-356` only checks that `setup.sh` mentions the seed gate / `test-seed-role.sql`; it does not assert that `_test_seed` is absent. The mutant kills a missing string, not a `--force` without `--seed` DB queried via `pg_roles`. Fix: fail unless a no-seed production layout has no `*_test_seed` role. (SA report L51–57.)

- **L-6 Low — the positive last-SA result is still the superuser statement**
  Positive last-SA assertions still run as the scratch superuser (`Program.cs:2691`, `:2703-2726` / `KitAdmin.SqlState`; reason `admin-checks-last-sa-delete` expects `P0001`). Runtime roles are denied by privilege (`42501`), a different check. Fix: keep the superuser guard assertion if that is the trigger under test, and add a separate runtime-role `42501` check. (SA report L59–63; `Program.cs:2703` per SA report.)

- **L-7 Low — `kitadmin-call-sites.md` does not match the code list** (NEW, doc only)
  Prior L-2 is closed. Executable `KitAdmin.CallSites` matches (41). But `kitadmin-call-sites.md:42` omits `admin-checks-last-sa-delete` (`KitAdmin.cs:36`) and still lists `admin-checks-cleanup`; the `req064-history-label-fixture` why-text also differs (`KitAdmin.cs:46` vs `DECISIONS.md:810` / `kitadmin-call-sites.md:34`). Fix: replace the cleanup row and align why-text. (SA report L65–73.)

- **L-8 Low — three mutant kills are fixture errors; two classes can survive a variant**
  Three mutant kills are fixture errors; two classes can survive a variant (see DEC-238 note 2 / SA report L75–85). (SA report L75–85.)

- **I-2 Info — migrator `GRANT INSERT` on `login_cutoff` still warns instead of `42501`**
  No product `GRANT INSERT ON auth.login_cutoff TO CURRENT_USER` in tip SQL; migrator revoke is `definer-owner.sql:58`. Prior executed warning was `01007`, not `42501`. Still open. (SA report L87–91.)

- **119b0e95 SA I-3 (m7 records other backends, peers 10; isolation recorded, not required)** — heading in report: **I-3 Info — `m7` records other backends and does not require zero**
  `Req064Suite.cs:293-306` records `pg_stat_activity` peer count and does not fail when peers are non-zero. `m7-no-nextval-fail` kill is `FAIL resolver-store-is-read :: peers 10 is_called f 0->0` (named check, not a fixture error). Isolation is recorded, not required. (SA report L93–97.)

### UX

- **F-12** (L): Clients placeholder copy is still shorter than accepted screen 10 — Restore the accepted clarifying sentences if the placeholder remains.
- **F-15** (L): Admin sign-in (password and TOTP/recovery steps) still has no visible h1; SignIn.Html defaults the document title to "Sign in" — Add an h1 and a specific title on Admin(), consistent with the Sign out confirm page which now has both (OAuth.cs:419-423).

### PO (R14-F4..F8)

| **R14-F4** | Low | **OPEN** | Natural 15-minute wait is still UNVERIFIED. The packaged req064 log has `PASS ttl-over-15-minutes-rejected` (suite TTL), not a natural wall-clock wait. No new natural-wait artifact is in this package. |
| **R14-F5** | Low | **OPEN** | Playerhub visual look (DEC-224/227, press, halo, lockup paint) is still UNVERIFIED in a browser. This exact-tip package has no contrast TSV or browser-paint capture. Token contrast 85/0 from the f259 package is not re-presented here. |
| **R14-F6** | Low | **OPEN** | Leftover hash routes remain. Tip `ui/admin-shell/shell.js` still uses `#/home`, `#/locations`, `#/create` (e.g. `location.hash`, nav `a.href = "#/" + id`, `hashchange`). `/orgs/...` paths are now served (F-20 CLOSED). `browser-reachability.log` still has `PASS browser-f21-hash-create-shows-form` and `PASS browser-f21-hash-config-shows-view`. Residual Low, same rationale as on f259: accepted `/orgs` paths exist; leftover hashes are not a shown High/Medium break. |
| **R14-F7** | Low | **OPEN (narrowed)** | `forged-resolved-guc-does-not-change-catalog-actor` is now `PASS` (`suites/req064.log` line 11; un-hold is in commit `c73be2a`). One HELD remains: `history-partner-function-follows-the-record` (line 43). Packaged PR body (SA L-1) says there is no runtime EXECUTE and a covering check `history-label-uses-the-record-partner`. Still a Low residual, not a product path this check can treat as High/Medium. |
| **R14-F8** | Low | **OPEN** | Run-13 F5 (no session ACs in the PO AC file; PQ-293 Open) and F16 (AC-MD-046 full flow / PQ-278 Open) are unchanged. AC file hashes are still `dfdc503bfd236c640c0b53ea81e7c3aa6b5440584e8b11d005670420861b4202` (roles-menus) and `51ade65470f23673377ad6cba73d83ab4ee4d8b6b2ebd87b715192b75696fdb4` (master-data). Documentation / coverage; not a product FAIL. |

### TL runner Lows (four)

1. The admin-handoff broken-path and forged-host mutants are killed only in `ui/admin-shell/admin-signout.test.mjs`, not in the 75-case runner (Low). Evidence: `node/admin-signout.log` lines `PASS mutant-admin-handoff-broken-path-killed` / `PASS mutant-admin-handoff-forged-host-killed`; `proofs/handoff-mutants.md`; `tools/req064-mutants.py` has no handoff cases.


2. **SA L-8** (Low, SA report L75–85). Three mutant kills are fixture-path errors, not the named assertions; the product functions do not contain these bugs. `history-first-partner` dies on `install_fn` owner/`REVOKE` (`42501` on `history_partner` in the fee fixture, 0 passed) — a grant-preserving `CREATE OR REPLACE` that only returns `(p_actor)[1]` can survive. `guc-trusted-again` dies on fee-fixture `P0001 catalog_actor_context` when `app.request_sub` is empty — a `COALESCE(guc, binding)` variant (or forged-`app.request_sub` after `begin_request`) can survive the named principal check. `actor-trusts-resolved-guc` also aborts Seed with the same `P0001`, but a set-only-when-present fallback then fails `CatalogActorIgnoresForgedGuc` (does not survive the named forged-actor assertion once Seed runs). open-session-granted-to-admin remains a valid product startup-guard kill (noted). Evidence (`mutants/runner.log`):
   - `history-first-partner-fail rc 1 FAIL req064-finished :: InvalidOperationException: req064 fee fixture kit admin sql failed: ERROR:  42501: permission denied for function history_partner | 0 passed, 1 failed`
   - `guc-trusted-again-fail rc 1 FAIL req064-finished :: InvalidOperationException: req064 fee fixture kit admin sql failed: ERROR:  P0001: catalog_actor_context | 0 passed, 1 failed`
   - `actor-trusts-resolved-guc-fail rc 1 FAIL req064-finished :: InvalidOperationException: req064 fee fixture kit admin sql failed: ERROR:  P0001: catalog_actor_context | 0 passed, 1 failed`
   Fix (follow-up): kill history-first-partner via FAIL history-label-uses-the-record-partner with an in-place body; for guc-trusted-again set a forged app.request_sub after begin_request and assert the bound principal; don't count the fee-fixture P0001 as the kill.

3. m4, m4b and m12 abort after the named check fails with `admin_assignment_user_fk` (Low). Evidence:
   - `m4-last-sa-no-raise-fail rc 1 FAIL last-active-sa-user-truncate | FAIL last-active-sa-truncate-guard | FAIL req064-finished :: PostgresException: 23503: insert or update on table "admin_assignment" violates foreign key constraint "admin_assignment_user_fk" | 65 passed, 12 failed`
   - `m4b-last-sa-delete-trigger-removed-fail rc 1 FAIL last-active-sa-user-truncate | FAIL last-active-sa-truncate-guard | FAIL req064-finished :: PostgresException: 23503: insert or update on table "admin_assignment" violates foreign key constraint "admin_assignment_user_fk" | 71 passed, 6 failed`
   - `m12-truncate-guard-fail rc 1 FAIL last-active-sa-user-truncate | FAIL last-active-sa-truncate-guard | FAIL req064-finished :: PostgresException: 23503: insert or update on table "admin_assignment" violates foreign key constraint "admin_assignment_user_fk" | 73 passed, 4 failed`

4. The 8 UI/node mutant pass runs print no pass count in `runner.log` (Low). UPDATE 41 packaged `ui-pass-logs/*.log` show per-check `PASS` lines but **no** aggregate `N passed, 0 failed` line. See `ui-pass-logs/README.md`.

## Merge record (PR #5)

- PR: https://github.com/proofbyteio/FloorFunds/pull/5
- Merge commit: `fc7bb6f41049286d4110fbd46f52987bab8ab462`
- Parents: `18372896912c00678ce75e718d094041763c7d84` and `119b0e952beef9b96597f296115f326fc13247de`
- Tree: `ac6607cdbaca8900a52a0b9c1c9b82693bcdd465`
- Merged by PM via the GitHub connector (recorded as proofbyteio) at 2026-11-12 18:09:09Z (10:09 AM PT)
- Normal merge commit
