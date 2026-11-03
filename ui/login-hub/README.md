# ui/login-hub (TL build-order step 2)

Server-rendered sign-in screens for DEC-081's clickable screens, served by the stand-in (src/Login.StandIn/Ui.cs).
[ASSUMPTION A46] The build plan names an Angular port; no packages can be fetched, so these are static HTML templates
(`{{x}}` HTML-encoded, `{{{x}}}` raw). Testids: contract/ui-driver.md (header says "DRAFT v1-d4, not frozen"; treated as frozen per instruction).

Ported from the UX mockups (read only), snapshot /tmp/sse-d0/ux-snap-2132:

| Template | UX source | md5 |
|---|---|---|
| signin.html (screen-signin) | 02-db-signin.html | 62eda529d4a087058930e794f62c752d |
| totp.html (screen-totp, contract screen 6) | 03-totp.html | 5f9032b052811c5376abaeb6ea00b8ff |
| resign-in.html (page-resign-in, #u2/#p2) | 07-handoff.html | cd527610d51891dd3ff237d47ae26761 |
| handoff.html (interstitial, no testid, A38) | 07-handoff.html | cd527610d51891dd3ff237d47ae26761 |
| screen-8a.html, screen-8b.html | 08-session.html | cc5cf4e9e114698d6187a10215bd5070 |
| (403: no template; plain text body unchanged, DEC-094 / D-88) | 10-blocked.html | 229d28399d550a9adae53e342de8f180 |
| layout.html, card.html, error-banner.html | 01-location.html / 02 | 7f3c323647713947c82772a16e4403c6 |
| theme.css (verbatim copy) | theme.css | 80ea155d58e4e8685dbf2bdc11a502ba |
| fonts/ (Poppins, OFL.txt, verbatim) | login-hub-mockups/fonts | see /tmp/sse-d0/ux-snap-2132/MD5SUMS |

Every page: CSP `default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self'; connect-src 'self' http://localhost:{control}; base-uri 'none'; object-src 'none'; frame-ancestors 'none'`.
Every other stand-in response: `default-src 'none'; frame-ancestors 'none'`.

Scripts: ui/login-hub/js/signin.js, js/handoff.js (served at {issuer}/ui/js/); config via data-nonce-url / data-agent-url on the form. No inline script or style (TL 9:38).
