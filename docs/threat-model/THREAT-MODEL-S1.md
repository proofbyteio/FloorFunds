# Threat model S1: login service (stand-in), hub, hand-off (STRIDE)

- **Author:** SSE2. Written Mon Nov 2, 2026, about 12:30 PM PT.
- **Scope:** S1 slice, made of the stand-in login service implementing contract `6e8a106` (DRAFT v1-d2), the hub, the Cash Advance hand-off and the UX mockups.
- **Sources (md5):**

  | Source | md5 |
  |---|---|
  | LD rev 11:41 | `88878681a39e23b82e30d6ad0c505647` |
  | PLAN | `ae18d66482606f9fdf334f0b953065b4` |
  | contract `6e8a106` | `contract-v1.md` `bd0d2085…` |

- **Not assumed:** anything the sources don't state is marked **OQ** and appears in the test matrix as pending.

## Assets
1. User credentials (password, TOTP secret, recovery codes) and the password store.
2. Login session cookie (15 min sliding idle, 12 h absolute cap; LD L229, L934; contract §7.4).
3. Authorization codes (≤60 s, single use), access tokens (5 min), ID tokens and refresh families (rolling, 30 s reuse leeway).
4. Signing keys (RS256, JWKS) and the enrolled device key registry (TPM public keys, `kid` = device id).
5. `hub_launch` values and attestation nonces (server-side, single use, ≤60 s).
6. Audit trail (outbox → Logs & Reporting DB), including `username_hmac`.
7. Cage actions (payment, ID scan, print): the business asset that the hand-off and check 4b protect.

## Trust boundaries
- **B1:** browser (managed Edge on a cage PC, or any browser) ↔ front listener (IP allowlist, `/l/{slug}`, authorize, endsession, discovery, JWKS).
- **B2:** server-side clients (Cash Advance BFF, back office) ↔ back listener 8084 (token, revoke, introspect).
- **B3:** sign-in page ↔ local device agent on loopback (attestation JWS).
- **B4:** login service ↔ device key registry and Transaction DB.
- **B5:** stand-in ↔ test control API and fake agent (must not exist in a real build: check 11).

## STRIDE

| # | Threat | Where | Control in contract / LD | Gap → finding / OQ | Test |
|---|---|---|---|---|---|
| S-1 | **Spoofing**: credential stuffing and brute force | B1 sign-in | Lockout 10/30 min (PCI); Nevada preset 3 tries (LD L338) | Nevada preset missing in contract (CR-13); throttling unstated (CR-11) | SEC2x, check 10 |
| S-2 | Spoofing: hand-off without the user re-authenticating, from a non-cage PC | B1/B3 | Rules 1-6 + valid attestation, fail closed (§5.1-5.5) | Purpose binding (CR-20); device revoked or at another location (CR-22) | SEC10-13, 4a |
| S-3 | Spoofing: forged hub proof | B1 | `hub_launch` server-stored, bound to session + client, single use (§5.7) | Resolved (CR-09) | CT/SEC pending control API |
| S-4 | Spoofing: forged attestation (alg `none`, HS256 with the public key, embedded `jwk`/`jku`) | B3 | `alg` const ES256; embedded keys refused | Verifier must pin ES256; `crit`/header allowlist (CR-21) | AT02, AT03 (run), SEC11 |
| S-5 | Spoofing: session fixation | B1 | Not stated: login-session id rotated on sign-in? | OQ-SSE2-40 | SEC24 (pending) |
| S-6 | Spoofing: authorization-code interception | B1 | PKCE S256 mandatory, codes ≤60 s single use, exact `redirect_uri` | — | CT, check 3/8 |
| T-1 | **Tampering**: open redirect through `redirect_uri` / `post_logout_redirect_uri` | B1 | Exact match; bad value → error page (OQ-5) | — | SEC, check 6 |
| T-2 | Tampering: `remote_session` flag flipped by a local process | B3 | JWS signed by the TPM key | **Accepted residual risk (LD §16.18.5):** a vendor admin can use the TPM key; a local process can reach the agent port | documented, not testable in S1 |
| T-3 | Tampering: config sets the cap to 0 or ≤ idle | deploy | Startup check cap > 15 min (§7.4) | Range OPEN (OQ-40) | SEC61 (skips until src exists), SEC62 pending |
| R-1 | **Repudiation**: actions without audit | B4 | Outbox in the same DB transaction (LD §8 L410) | Invalid attestation on plain sign-in writes no event (OQ-11, CR-25) | audit tests pending |
| R-2 | Repudiation: hand-off attribution | B4 | `auth.app.signed_in method=hub_handoff` with the original `auth_time` and login session id | — | check 2 |
| I-1 | **Information disclosure**: username enumeration (message, status, timing) | B1 | One generic error ("Sign-in didn't work."); `username_hmac` | Timing parity threshold unstated (OQ-SSE2-11) | SEC20-23, E2E02 |
| I-2 | Info disclosure: `hub_launch` / attestation in URL, Referer, logs | B1 | Single use, ≤60 s, bound | Referrer-Policy, log a hash, POST only (CR-30/31) | pending |
| I-3 | Info disclosure: discovery/JWKS on the public front | B1 | Allowlist applies (OQ-32) | Ties to CR-14 | CT01 |
| I-4 | Info disclosure: error detail leaks (`error_description`) | B1/B2 | None sent (OQ-17) | OpenIddict default emits it (S2) | errors CT |
| D-1 | **Denial of service**: lockout abuse (lock a victim with 3 or 10 wrong tries) | B1 | Lockout by design | Accepted trade-off; Nevada 3 tries makes it easier; admin unlock path (LD L338) | SEC2x |
| D-2 | DoS: device agent down → cage actions blocked | B3 | Screen 8 variant B; fail closed (4b) | Enforcement point unstated (CR-12) | E2E14/15 (mockups, run), SEC50-54 pending |
| D-3 | DoS: nonce/`hub_launch` table growth | B4 | ≤60 s TTL | Cleanup unstated (nit) | — |
| E-1 | **Elevation of privilege**: SSO from a weak session into Cash Advance | B1 | `prompt=login` dropped only under rules 1-6; `cage_consumed` | — | check 2/4 |
| E-2 | EoP: refresh after role removal | B2 | Refresh re-checks role → `invalid_grant` (OQ-12) | — | check 7 |
| E-3 | EoP: stand-in artifacts in a real build (fake agent, `standin-` kids, control API, `x_standin` tokens) | B5 | Check 11 | Real must refuse `standin-` kids; `x_standin` must be required/forbidden (CR-06) | TOOL0x, SEC40, `tools/check-no-standin.sh` |
| E-4 | EoP: remote session gets through plain sign-in | B1/B3 | `remote_session=true` → refused (§6.1) | **CR-25 blocker:** expired attestation → "go ahead" after 60 s | SEC15/16 pending |
| E-5 | EoP: after `attestation_invalid` fallback, cage actions still allowed | Cash Advance | 4b: refused until re-attest | Component and scope unstated (CR-12, OQ-SSE2-34) | SEC50-54 |

## Attestation: what it does and does not prove (LD §16.18.5 L1360-1395)
- **It proves:** an enrolled TPM key signed this server nonce within 60 s, and that the agent then reported `remote_session`.
- **It does not prove (accepted residual risks, LD):**
  - that the agent is honest when a vendor admin controls the PC;
  - that no local malware calls the agent port;
  - that a hidden remote tool is detected.
- **Plain sign-in is best effort only.** No attestation → proceed (§6.1). An attacker who blocks the agent loses only the hand-off and cage actions, not sign-in. That is by design. CR-25 shows a non-attacker path (slow typing) that also silently drops the remote check.

## Out of scope for S1
- Entra federation (S2+), real TPM and agent, production key management, and the Logs & Reporting DB relay.
