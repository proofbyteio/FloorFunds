# UI driver contract (DRAFT v1-d4, not frozen)

What browser tests may rely on. Source for every rule: [TL Decision 2026-11-02, CR-05] (rulings md5 5bcaedc9, Addendum 2, §A; answers OQ-SSE2-04, OQ-SSE2-05), unless tagged otherwise.
**The `data-testid` values below are accepted** [TL Decision 2026-11-02 8:26 PM] (CR-05); closed list in `standin-ui.schema.json` (`testid`).

## 1. Sign-in form `/l/{slug}`

| # | Rule | Source |
|---|---|---|
| 1.1 | Password step: `GET /l/{slug}` renders the form; `POST /l/{slug}` with fields `username`, `password`, `device_attestation` (hidden; empty if the agent didn't answer, contract §6.5) and `__RequestVerificationToken` (ASP.NET antiforgery, taken from the GET; **no bypass**) | [TL Decision 2026-11-02, CR-05]; field `device_attestation` [contract §6.6] |
| 1.2 | TOTP step: `POST` to the step's form action with fields `totp_code` and `__RequestVerificationToken`. The test gets the code from `GET /_standin/users/{username}/totp` | [TL Decision 2026-11-02, CR-05]; field name `totp_code` [TL Decision 2026-11-02 8:26 PM] (`totp_form`) |
| 1.3 | Attestation at submit: before the POST the page calls `POST /l/{slug}/attest-nonce` and then the agent (contract §6.5). Tests drive the real page; they never post `device_attestation` by hand except in negative tests | [TL Decision 2026-11-02, CR-25] |

## 2. `data-testid` values [TL Decision 2026-11-02 8:26 PM]

| Screen / element | `data-testid` |
|---|---|
| Screen 2, sign-in (password step) | `screen-signin` |
| Username / password / submit | `signin-username`, `signin-password`, `signin-submit` |
| Screen 6, TOTP step | `screen-totp`; field `totp-code`; submit `totp-submit` |
| Screen 7, hub | `screen-hub` |
| Hub tile per app | `hub-tile-{app_code}` (fixed by CR-05) |
| Screen 8 (Session) variant A, remote session | `screen-8a` |
| Screen 8 (Session) variant B, security software not responding | `screen-8b` |
| Error area (any screen) | `error-area` |
| Hub tiles, placeholder `app_code`s | `hub-tile-cash_advance`, `hub-tile-back_office`, `hub-tile-reports` **provisional** [TL Decision 2026-11-02, provisional, pending Mohanraj via PM]. The `app_code`s are data (`auth.app`, stand-in fixtures), not a schema enum; the schema keeps the generic `hub-tile-{app_code}` pattern |

Screen numbers: contract screen 6 (TOTP) = UX `03-totp.html`, contract screen 7 (hub) = UX `06-hub.html` (UX D-67, matched by meaning) [TL Decision 2026-11-02 8:31 PM].

## 2a. Re-sign-in and admin sign-in pages [TL Decision 2026-11-02 8:31 PM]

**SSE-proposed ids:** UX D-67 (DECISIONS.md md5 b6fa0d1f) lists no ids for these forms ("07 re-sign-in form u2/p2 and 05 admin form carry no ids"); names below follow the UX form fields and the `page-` / `field-` / `error-` prefixes; UX to add them to the mockups.

| Page / element | `data-testid` | UX element |
|---|---|---|
| Re-sign-in page (UX screen 07, hand-off re-prompt, contract 5.3) | `page-resign-in` | `07-handoff.html` re-sign-in form |
| Username / password | `field-resign-in-username`, `field-resign-in-password` | `#u2`, `#p2` |
| Submit | `resign-in-submit` | form submit |
| Error banner | `error-resign-in` | error banner (also `error-area`) |
| Admin sign-in page (UX screen 05, `/admin`) | `page-admin-sign-in` | `05-admin.html` |
| Username / password / submit | `field-admin-username`, `field-admin-password`, `admin-signin-submit` | `#username`, `#password` |
| TOTP step field / submit | `field-admin-totp`, `admin-totp-submit` | `#totp` |
| Recovery-code field / submit | `field-admin-recovery-code`, `admin-recovery-submit` | `#rcode` |
| Error banner; field errors | `error-admin-sign-in`; `error-admin-username`, `error-admin-password` | banner (also `error-area`); `#username-err`, `#password-err` |


## 3. Visible strings tests may assert (only these)

| String | Where | Source |
|---|---|---|
| "Sign-in didn't work." | error area | [TL Decision 2026-11-02, CR-05] |
| "This PC's security software isn't responding. Contact IT." | screen 8B | [TL Decision 2026-11-02, CR-05]; contract §5.5 |
| "Try again after the remote session ends" | screen 8A (LD text) | [TL Decision 2026-11-02, CR-05]; [SRC: LD L1382] |

All other copy belongs to UX and is not asserted.
