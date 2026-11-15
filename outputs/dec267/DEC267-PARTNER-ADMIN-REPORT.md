# DEC-267 / DEC-268 Partner Admin screen — local build report

**Branch:** `dec267-partner-admin` (local only; not pushed)  
**Base:** `682ab67183adfde9b52c3b300674a075efd454d5` (tree `6d28db4ee366573fe83fad5437f90c7557f41a1e` = `origin/main`)  
**Tip:** `cdf8a4f98abe279f881572d2f1fbf6ec9ef2ffb2`  
**Tip tree:** `7a7d0526970954c705805e062c99cffc7a16be9a`  

Owner (relayed): DEC-267 9:03 AM PT — complete Partner Admin screen; DEC-268 9:04 AM PT — integrate UX mockup.  
Steer: PM 9:25 AM PT confirmed C9 interims as **PM DEC-241 defaults, not owner decisions**.  
Follow-up D1–D3: on top of `8ecc214` (no amend/rewrite/push; no new/edited tests).

C4 (lookup display-name migration / API `name` keys) was **not built** (owner has not approved TO CONFIRM 4). No migration changed; no scratch-DB migrate required.

Inputs: `uploads/dec267-inputs.tar.gz` (sha256 `7915aa75dbecbb6b96cd405942880fd91c1df6c7e06c3662992f22445a848d80`); `sha256sum -c SHA256SUMS-dec267-inputs.txt` **OK**. **validation-rules-central** wins over the mockup.

---

## Commit list (oldest → newest)

| # | Commit | Parent | Tree | Subject |
|---|--------|--------|------|---------|
| C1 | `0ffd842ff97d55b5f2471f7b061d4f21ed1fbc76` | `682ab671…` | `e06176155f828d97c6e6e11890bc1d6797e9801c` | BUG-001: admin-shell banner and disabled-CTA contrast (tree-equal to 1b963b5e's theme.css change) |
| C2 | `99a0f149bd5befe84dc5ddab72fca33a9a02ad11` | `0ffd842f…` | `35494c95de320aee360b13f4e9115f1a6b7fe972` | C2: await applyCountry on Partner/Location edit; clear postal on country change; flash banner |
| C3 | `4ef030cb2f7dbc9cf82381601e8b650a300e2f74` | `99a0f149…` | `f220fa11ed848b95a4ecfa40b5d336b36305ef45` | C3: Enter a phone number when country is null; email @-edges and trailing-dot parity |
| C5 | `033ceb10ed843ea97a3350523d2d2d576576599e` | `4ef030cb…` | `a761d586940bc1d3b5ebf79c0f8276ca3b672695` | C5: Partner form sections, Country-first blank default, mockup labels/hints, +1 phone, Cancel |
| C6 | `484e1130621715c3ef307a2e28f14113a7ec9b48` | `033ceb10…` | `c13ba3480985f1f3d06d0cbb06a1f1aa87b766b0` | C6: Partner inline errors and error summary; map all client and 422 fields |
| C7 | `d5a16d84c61fb43dc0e850491836a14268924e70` | `484e1130…` | `95ae72c2fc6de46302d5d8a32ea3c64535e62392` | C7: Partner compact CSS — 720px card, 8-col grid, dark error summary, joined phone |
| C8 | `76ff5f8ecb18b7cf121b231d7da10a06334990a4` | `d5a16d84…` | `5ecd61f8d920a2dde9fc9c2d3450b336a214ee57` | C8: 60px header bar, Sign out pill, mobile lockup, single column below 64rem |
| C9 | `8ecc214a78f727fca97e1cc5669bc73471b1c240` | `76ff5f8e…` | `59c9cd27c6aab6717f4c917c25b58a43d6355ecf` | DEC-268 interims pending PM (B1/B3/B4) |
| D1 | `534507bc6c1ba52ab80142032e45a2bd356ac383` | `8ecc214a…` | `d5b7923716b30a3d06bb75a454fa8b35df8ed6e9` | D1: remove stray `outputs/dec267/questions.md` (keep questions in report only) |
| D2 | `7a05f090fb725c367858bb9a5278c5fbad89bc2a` | `534507bc…` | `36769195b65336d096109b86d25987d79c680a29` | D2: match mockup error rhythm, ZIP error row, summary icon, sidebar lockup |
| D3 | `cdf8a4f98abe279f881572d2f1fbf6ec9ef2ffb2` | `7a05f090…` | `7a7d0526970954c705805e062c99cffc7a16be9a` | D3: LIVE shots, compare.html, report + patches refresh |

C4 skipped (not built). C9 is separately revertible and holds B1/B2/B3/B4/B6 as PM DEC-241 defaults (9:25 AM PT).

### D2 line-by-line mapping (one commit)

| Letter | Mock / rule | Built change |
|--------|-------------|--------------|
| **(a)** | Mock HTML L40–L42 / L327–L353 field+section spacing; error tighten L555–L571 (`body.state-error`) | `shell.css` `.partner-form` card padding `24px 32px 24px`, section `22px`/`20px`, grid gaps; `.partner-form--errors` mirrors L555–L571 (section `10px`, row-gap `10px`, summary `8px 12px`, h2 `margin-bottom:2px`, li `1px`, hide hints, page-head tighten) |
| **(b)** | ZIP inline error right-aligned under ZIP, inside card (HTML L364–L374 grid sibling) | `partner-form.mjs`: ZIP `.error-zip` inserted as grid sibling after `#zip-hint`; `shell.css` `.error-zip`/`.hint-zip` `grid-column:1/-1; text-align:right; max-width:100%` (no spill) |
| **(c)** | Error summary “!” before “There is a problem”; list spacing (HTML L294–L324 / L682–L688) | `partner-form.mjs`: SVG circle+bang in summary `h2`; `shell.css` h2 `display:flex; gap:8px`; ul `padding-left:22px`; li `margin 0 0 4px`, `:last-child { margin:0 }` |
| **(d)** | DEC-230 sidebar lockup: 36px mark, 8px gap (HTML L126–L150) | `shell.css` `.shell__nav .admin-lockup` flex row `gap:0.5rem` (8px), mark `36×36`, name ellipsis |

---

## `git diff --stat 682ab671..tip`

```
outputs/dec267/DEC267-PARTNER-ADMIN-REPORT.md      | 183 +++++++++
 outputs/dec267/DEC267-PARTNER-ADMIN-REPORT.sha256  |   1 +
 ...in-shell-banner-and-disabled-CTA-contrast.patch | 109 ++++++
 ...plyCountry-on-Partner-Location-edit-clear.patch | 134 +++++++
 ...phone-number-when-country-is-null-email-e.patch |  50 +++
 ...form-sections-Country-first-blank-default.patch | 337 ++++++++++++++++
 ...inline-errors-and-error-summary-map-all-c.patch | 175 +++++++++
 ...compact-CSS-720px-card-8-col-grid-dark-er.patch | 267 +++++++++++++
 ...der-bar-Sign-out-pill-mobile-lockup-singl.patch | 210 ++++++++++
 ...0008-DEC-268-interims-pending-PM-B1-B3-B4.patch | 103 +++++
 ...tray-outputs-dec267-questions.md-from-the.patch |  32 ++
 ...ckup-error-rhythm-ZIP-error-row-summary-i.patch | 246 ++++++++++++
 outputs/dec267/shots/after/LIVE.txt                |   3 +
 outputs/dec267/shots/after/SHA256SUMS.txt          |   7 +
 .../shots/after/partner-admin-ca-1440x900.png      | Bin 0 -> 92611 bytes
 .../shots/after/partner-admin-edit-1440x900.png    | Bin 0 -> 83962 bytes
 .../shots/after/partner-admin-empty-1440x900.png   | Bin 0 -> 65014 bytes
 .../shots/after/partner-admin-error-1440x900.png   | Bin 0 -> 89625 bytes
 .../after/partner-admin-filled-us-1440x900.png     | Bin 0 -> 74948 bytes
 .../shots/after/partner-admin-mobile-390.png       | Bin 0 -> 49708 bytes
 .../after/partner-admin-server-422-1440x900.png    | Bin 0 -> 78106 bytes
 outputs/dec267/shots/before/LIVE.txt               |   3 +
 outputs/dec267/shots/before/SHA256SUMS.txt         |   7 +
 .../shots/before/partner-admin-ca-1440x900.png     | Bin 0 -> 77069 bytes
 .../shots/before/partner-admin-edit-1440x900.png   | Bin 0 -> 80775 bytes
 .../shots/before/partner-admin-empty-1440x900.png  | Bin 0 -> 75757 bytes
 .../shots/before/partner-admin-error-1440x900.png  | Bin 0 -> 72540 bytes
 .../before/partner-admin-filled-us-1440x900.png    | Bin 0 -> 82666 bytes
 .../shots/before/partner-admin-mobile-390.png      | Bin 0 -> 88229 bytes
 .../before/partner-admin-server-422-1440x900.png   | Bin 0 -> 80775 bytes
 outputs/dec267/shots/compare.html                  |  22 ++
 .../shots/mock/partner-admin-empty-1440x900.png    | Bin 0 -> 73153 bytes
 .../shots/mock/partner-admin-error-1440x900.png    | Bin 0 -> 99085 bytes
 .../mock/partner-admin-filled-us-1440x900.png      | Bin 0 -> 85731 bytes
 .../dec267/shots/mock/partner-admin-mobile-390.png | Bin 0 -> 74019 bytes
 src/Admin.Api/OrgFormValidation.cs                 |   4 +-
 ui/admin-shell/index.html                          |  17 +-
 ui/admin-shell/org-form-rules.mjs                  |   4 +-
 ui/admin-shell/partner-form.mjs                    | 364 +++++++++++++++++
 ui/admin-shell/shell.css                           | 436 ++++++++++++++++++++-
 ui/admin-shell/shell.js                            | 104 +++--
 ui/admin-shell/theme.css                           |  34 +-
 42 files changed, 2790 insertions(+), 62 deletions(-)
```

Patches regenerated: `outputs/dec267/patches/` via `git format-patch 682ab671..<tip>`.

---

## Checks (report only; DEC-263 — no new/edited tests)

| Check | Result |
|-------|--------|
| `node --check` on changed `.js`/`.mjs` (`shell.js`, `partner-form.mjs`, `org-form-rules.mjs`) | **OK** |
| `dotnet build src/Admin.Api` | **0 Warning(s) / 0 Error(s)** (verified on disposable instance builds) |
| Migration apply | **N/A** (C4 not built; no migration change) |
| Shot `SHA256SUMS.txt` | **OK** (`sha256sum -c` in before/ and after/) |

### Existing suites (stale asserts expected under DEC-263)

| Suite | Pass / Fail | Notes |
|-------|-------------|-------|
| `create-client.test.mjs` | **1 / 8** | Matches known stale (DEC-263) |
| `orgs-routes.test.mjs` | **7 / 4** | Matches known stale |
| `route-gates.test.mjs` | **8 / 5** | Matches known stale |
| `session-closed.test.mjs` | **3 / 0** | Matches known |
| `create-location.test.mjs` | **5 / 8** | **UNVERIFIED** (suite still stale vs current location form) |
| Other dotnet suites | **UNVERIFIED** | Not re-run this pass |

---

## Screenshots (D3 — LIVE)

**LIVE** on disposable kit DBs via out-of-tree harness `/tmp/dec267/shots/run-instance.sh` + CDP `/tmp/dec267/shots/capture.mjs`.

| | before | after |
|--|--------|-------|
| Tree | `/tmp/dec267/main` @ `682ab671` | `/tmp/dec267/wt-partner` @ tip (D2 `7a05f09` + this report commit) |
| Shell | `http://127.0.0.1:5190` | `http://127.0.0.1:5191` |
| Stand-in base | `28080` | `29080` |
| DB | `ff_kit_dec267_main` | `ff_kit_dec267_branch` |

Chrome: `google-chrome --headless=new` with fresh `mktemp -d` `--user-data-dir` and its own `--remote-debugging-port` (no shared profile). Sign-in: fixture `super_admin` through shell `/signin` → stand-in admin + TOTP from control `base+2` (`/_standin/users/super_admin/totp`). Passwords from each instance’s `fixture-secrets.json` (never logged).

Seed note (harness-only): kit FORCE RLS grants `admin_assignment` / `user_issuer` INSERT to `_test_seed`, not the owner role; `seed-dev-assignment.sh`’s `ON CONFLICT DO UPDATE` also needs UPDATE on `user_issuer`. Harness seeded as `_test_seed` with INSERT-only SQL and `STANDIN_ISSUER=http://127.0.0.1:<shell>/standin`. No network workaround.

Side-by-side: `outputs/dec267/shots/compare.html` (mock | before | after).

### before (`outputs/dec267/shots/before/`) — LIVE

| File | sha256 |
|------|--------|
| partner-admin-empty-1440x900.png | `c01c53f7bfb5c8a1acc5cce2f3a696fdcd6505b80393f60587b06cf7b2257221` |
| partner-admin-filled-us-1440x900.png | `37c0fe1420479ae3bddfdd05053b61a26525bd87beaf9552ccb106541e39c36e` |
| partner-admin-error-1440x900.png | `a32a7eb7a2260e50525ce824cb8ac347ed49bb7f95ef4e6682043804f7519220` |
| partner-admin-mobile-390.png | `e9ac27148dee8fb7ccb17bcd1a0b325f13d6dfdc11ef44fa84073a26d9b6c091` |
| partner-admin-ca-1440x900.png | `f7b0f360ff6a10cbc657b6f1d0b6681198f3f838bd81f168e8fbbd7768563cf0` |
| partner-admin-edit-1440x900.png | `3cedde1ab1973841d105430c19a3f487f479123af02b98e1b302758ac8513ab2` |
| partner-admin-server-422-1440x900.png | `3cedde1ab1973841d105430c19a3f487f479123af02b98e1b302758ac8513ab2` |

### after (`outputs/dec267/shots/after/`) — LIVE

| File | sha256 |
|------|--------|
| partner-admin-empty-1440x900.png | `765b61eb10f5cb0c59274e0861a1a1bda58cb323cd05b6c2ddeb3d72f2289d2f` |
| partner-admin-filled-us-1440x900.png | `475f81ce321d46b15a0c04362c94701873d952ca3d641c21bdaeadb1f5708e06` |
| partner-admin-error-1440x900.png | `78e076eb5a517ee3d1acea492b0910ec8c4d389bf798c05784e9a739d4b0d87a` |
| partner-admin-mobile-390.png | `df59f1817c80a9b59b049cab491cf16c076ae7f50e585dfd70116a6db7643832` |
| partner-admin-ca-1440x900.png | `5f5561ae114a9ef1a5954c23648767db46ac89e20192228a014cafbf3e9e4744` |
| partner-admin-edit-1440x900.png | `86e304d26beb2b2a9bcf0b20d5a0cce1575826184b79a98c04a436df4304eccc` |
| partner-admin-server-422-1440x900.png | `95561bace7652297f9bf016c473dfbdf41095fb3ff013d7c533d50208e584e21` |

---

## Build vs mockup differences (full list)

### GAPS §3.1 (rules win)

1. **No `maxlength` on text inputs** — built (Partner); rules length sentences can surface.
2. **US state list** — DB 50+DC via lookups (not mock’s 15).
3. **Inline/summary sentences** — from `validation-rules-central.md`, not mock “PROPOSED” sample-only set.
4. **Phone hint** — mock “Use 10 digits…” kept; validation remains Rules L16 / server libphonenumber.
5. **Phone required when Country blank** — C3 server emits “Enter a phone number.” even if country/region null.
6. **Country change clears region/postal/tz** — C2 `clearDependent`.
7. CA label/format switching built; placeholders removed on Partner text inputs; ZIP hint as drawn.

### C4 display names (not built)

Country/region options still show main API labels (`United States (US)`, etc.). Time zones still `Pacific (America/Los_Angeles)`, not mock `Pacific Time — America/Los_Angeles`. **Listed as mockup difference; for PM (TO CONFIRM 4).**

### PM DEC-241 defaults (9:25 AM PT), not an owner decision — C9

| Id | Choice | Label |
|----|--------|-------|
| **B1** | Nav label stays **Create Partner Admin**; h1 **Create partner** + lead “Add a partner organization.”; **no breadcrumb parent** | PM DEC-241 default (9:25 AM PT), not an owner decision |
| **B2** | **No Partner list** | PM DEC-241 default (9:25 AM PT), not an owner decision |
| **B3** | **No menu button**; nav **stacked under the header** below 64rem | PM DEC-241 default (9:25 AM PT), not an owner decision |
| **B4** | Cancel → `#/home` | PM DEC-241 default (9:25 AM PT), not an owner decision |
| **B6** | Keep main’s lookup option labels (no C4) | PM DEC-241 default (9:25 AM PT), not an owner decision |

Also: **PQ-NEW-10** not built (no admin user account). Libphonenumber validation on main kept. Nav **order unchanged** (C8 override vs GAPS reorder).

### Chrome / shell vs mock visual leftovers

- Mock sidebar nav set (Partners, Clients, …) ≠ built grant-driven nav (Home, Locations, Create Location Admin, Catalog, Create Client Admin, Create Partner Admin).
- Mock breadcrumb `Partners / Create partner` absent (B1).
- Mock active-nav gold left bar vs built gold pill on active item.
- Mock time-zone option wording (C4) and country/region option wording (C4).
- Client and Location forms intentionally unchanged (Partner-scoped CSS/markup).
- Success copy remains “Created”/“Saved” + “Partner \<name\> was accepted.” (flash survives hashchange via C2).

---

## Open questions (in report only; `questions.md` removed in D1)

1. C4 / TO CONFIRM 4 — lookup display names (owner not approved).
2. B2 — Partner list still unanswered for a durable edit entry point.
3. PQ-NEW-10 — Partner Admin user account.
4. Breadcrumb parent (deferred with B1).

---

## Footer (filled after D3 commit)

- **Tip:** `cdf8a4f98abe279f881572d2f1fbf6ec9ef2ffb2`
- **Tip tree:** `7a7d0526970954c705805e062c99cffc7a16be9a`
- **Report sha256:** recorded in `DEC267-PARTNER-ADMIN-REPORT.sha256`
