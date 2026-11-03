# UI driver contract (DRAFT v1-d4, not frozen)

What browser tests may rely on. Source for every rule: [TL Decision 2026-11-02, CR-05] (rulings md5 5bcaedc9, Addendum 2, §A; answers OQ-SSE2-04, OQ-SSE2-05), unless tagged otherwise.
**The `data-testid` values below are an SSE proposal; the TL freezes them with v1** (CR-05).

## 1. Sign-in form `/l/{slug}`

| # | Rule | Source |
|---|---|---|
| 1.1 | Password step: `GET /l/{slug}` renders the form; `POST /l/{slug}` with fields `username`, `password`, `device_attestation` (hidden; empty if the agent didn't answer, contract §6.5) and `__RequestVerificationToken` (ASP.NET antiforgery, taken from the GET; **no bypass**) | [TL Decision 2026-11-02, CR-05]; field `device_attestation` [contract §6.6] |
| 1.2 | TOTP step: `POST` to the step's form action with fields `totp_code` and `__RequestVerificationToken`. The test gets the code from `GET /_standin/users/{username}/totp` | [TL Decision 2026-11-02, CR-05]; field name `totp_code` [SSE proposal → TL at freeze] |
| 1.3 | Attestation at submit: before the POST the page calls `POST /l/{slug}/attest-nonce` and then the agent (contract §6.5). Tests drive the real page; they never post `device_attestation` by hand except in negative tests | [TL Decision 2026-11-02, CR-25] |

## 2. `data-testid` values (SSE proposal)

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

## 3. Visible strings tests may assert (only these)

| String | Where | Source |
|---|---|---|
| "Sign-in didn't work." | error area | [TL Decision 2026-11-02, CR-05] |
| "This PC's security software isn't responding. Contact IT." | screen 8B | [TL Decision 2026-11-02, CR-05]; contract §5.5 |
| "Try again after the remote session ends" | screen 8A (LD text) | [TL Decision 2026-11-02, CR-05]; [SRC: LD L1382] |

All other copy belongs to UX and is not asserted.
