
## TL 9:32 PM steering + build-order step (2) ui/ pages

Rulings logged:
- A43 TL Decision: hub token response keeps omitting access_token in the stand-in. **v1.1 MUST-FIX (SA):** add a hub audience to the access-token aud enum, scoped to /apps only, no roles, and have the stand-in issue it (RFC 6749 §5.1, OIDC Core 3.1.3.3 require access_token).
- A41 accepted. Checkpoint (1) accepted at 8aab98a.
- A42 CHANGED: compare in ms; refuse > 300000 ms (no rounding down, 300.4 s refused); same for hub_launch (> 60000 ms). `Handoff.Over()` compares ticks. Boundary tests at 300000/300001 (+300400, +1 tick) and 60000/60001 ms run as a startup self-test (`A42 boundary selftest ... PASS` in si1.log and evidence). [GAP] the frozen clock control advances whole seconds and runs from real time, so exact-ms cases can't be hit live; live checks are 299 s accept / 300 s refuse and 59 s accept / 60 s refuse.
- CSP: every stand-in response gets frame-ancestors 'none' (global fallback middleware); ui/ pages send the full page CSP. Driver asserts this over every response of the run.

Step (2):
- ui/login-hub templates (README lists UX sources + md5s). [A46] HTML templates instead of Angular (no packages).
- [A44] after attestation_invalid at the interstitial (5.5) the re-prompt is screen-8b wrapping the page-resign-in ids (redirect adds `&agent=down`; display-only). The banner is not kept after a failed re-prompt POST.
- [A45] the allowlist 403 body is unchanged (DEC-094, D-88); only the CSP header is added by the global fallback.
- Evidence: /tmp/sse-d0/evidence-step2.txt, 158/158 live on base 18080 (guard: all 3 ports owned by our pid).
- Evidence md5: 7edafa2569cf9c8681b810b21db2f56a
