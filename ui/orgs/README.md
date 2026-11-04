# ui/orgs: screen 9 "Sign-in method" (S2a P4)

Stand-in page for the location sign-in method (spec `contract/v1.1-S2a-location-login-config.md` §3, §6, §11).
It is self-contained and talks only to the admin API. It has no app chrome (OQ-S2a-1 is still open).

## Sources

| What | Where | md5 |
|---|---|---|
| Mockup 09 (MK9, live; D-103) | `/workspace/cachd-ones/login-hub-mockups/09-location-method.html`, snapshot `/tmp/sse-llc1/09-location-method.90d2a8fa.snapshot.html` | 90d2a8fa0052d5496f8fc4c274bc779c |
| Theme | `/tmp/sse-llc1/theme.aaae47e3.snapshot.css`, copied verbatim to `theme.css` | aaae47e38fd25078476773adca67cf33 |
| Fonts (Poppins, OFL) | copied from `ui/login-hub/fonts` (same files the mockup uses) | see `fonts/` |
| Error copy review (UX, D-102) | `/workspace/cachd-ones/reviews/sse-error-copy-review.md`, snapshot `/tmp/sse-llc1/sse-error-copy-review.8c7d5274.snapshot.md` | 8c7d5274d549d45d18ae0415964e2236 |
| UI rulings | TLD 2026-11-04 9:49 (D-51) C2 / C3, TLD 11:57, D-102, D-103, spec §11 | |

D-103: the page, this README, the spec and the build log all cite MK9 90d2a8fa. Against the 9dae6040 snapshot the only changes are that the
"Microsoft app connection" card is no longer drawn in `create` and its design note says so (TLD 11:57, V11-CREATE-SECRETCARD).

## Files

- `location-sign-in.html`: one page with every state. It has no inline script or style, so the CSP is `script-src 'self'; style-src 'self'`.
- `orgs.css`: page additions on top of the verbatim `theme.css`. Only the D-102 Request ID line (`.request-id`: own last line, small, #6b6b7b, selectable, wraps, never an ellipsis).
- `js/llc-core.mjs`: pure logic. It covers the route, the viewer kind, the downgrade rule, the request bodies, labels and PT time.
- `js/llc-page.mjs`: the DOM and the fetch calls.
- `js/error-copy.mjs`: the error copy MK9 does not give, **approved by UX (D-102)** with three changes; renamed from `PROPOSAL-error-copy.mjs` with `git mv`. It also holds the `Request ID: <X-Request-ID>` text, shown on its own last line of top-level banners only (also the 409 and 422 tenant banners), never under a field, and hidden when there is no id.
- `tests/core.test.mjs`: unit tests (`node --test ui/orgs/tests/core.test.mjs`).
- `tests/e2e.mjs`: headless Chrome over CDP against the real Admin API and real PostgreSQL.

## Routes (served by the host app)

- `…/orgs/{partner_id}/locations/{location_id}/sign-in`: view and edit. Add `#saved` to show the saved banner once, after a create.
- `…/orgs/{partner_id}/locations/new`: create. The partner comes from the URL (C3). The API answers 404 for a Partner Admin of another partner.
- `<body data-api-base>` is the API base. `data-admin-type` (`super` / `partner`) only picks the create layout. On every other screen the viewer kind comes from the API: a `secret` key in the response means Super Admin; a restricted view plus `/changes` 200 means Partner Admin; 403 means an org:V reader.

## Run the tests

```
export PATH=/home/box/.dotnet:$PATH
(cd src && dotnet build Admin.Api.Checks --no-restore)
node --test ui/orgs/tests/core.test.mjs
node ui/orgs/tests/e2e.mjs ff_sse          # or a scratch ff_kit_<name> DB; the auth tables must be empty
```

`e2e.mjs` starts `Admin.Api.Checks --ui-host ui/orgs` (TEST ONLY, `src/Admin.Api.Checks/UiHost.cs`). That runs the real API on a free port in 18130–18139 and seeds four locations through the API as the Super Admin. It serves this page under `/as/{sa|pa|pb|rd|anon}/orgs/…` and proxies `/as/{persona}/api/…` to the API with that persona's test token.

Chrome runs with every host except 127.0.0.1 made unresolvable. When the run ends, the host prints a DB summary read as the owner, including the org:V reader's change rows (must be none), and empties the tables.

## Open items (not decided here)

- **TO CONFIRM (Mohanraj):**
  - The reason length. The page does not check length on the client. The server's `reason_length` code shows the D-102 copy "This reason is too short or too long. Change it, then try again.", which gives no numbers. Once Mohanraj confirms 10 to 500 characters it becomes "Enter a reason of 10 to 500 characters."
  - Whether a Partner Admin may undo a downgrade (turn MFA back on or backup back off). The page follows the API, which allows it (spec §6). Those controls are not locked, and no hint is shown.
  - The test sign-in deferral. There is no Test sign-in button and no gate on Save (spec §11, §12).
- OQ-S2a-1: the host app, how it supplies `data-admin-type` and the bearer. The e2e host is a stand-in.
- V11-CREATE-SECRETCARD (TLD 11:57): the create state has no "Microsoft app connection" card, as MK9 90d2a8fa draws it. The card appears in the view and edit states once the location exists. Backlog item for showing it during create.
- V11-PARTNERNAME (TLD 11:57): the Partner Admin chip shows "Partner Admin: <partner id>" (mono), like the actor ids. No API gives the partner name to this page. The id is the viewed location's `partner_id`, or the URL partner on create.
- OQ-S2a-13 stays open and waits on OQ-S2a-1: no location list or other entry point to `…/locations/new` beyond the URL is built.
