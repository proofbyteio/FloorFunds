# Login contract v1: DRAFT (not frozen)

Owner: SSE. Status: **DRAFT v1-d2**, written Mon Nov 2, 2026, 11:30-11:40 AM PT (v1-d1); revised from 11:37 AM PT with the TL's answers (v1-d2).
It is not frozen. Per build plan §3.1 "Versioning" (TL Decision), v1 freezes only after SSE2's
adversarial pass and TL review. No stand-in code (SI1) is written until then.

Clients (Cash Advance BFF, hub, back office, reports) may depend **only** on what is written
in this folder (plan §3.1). The stand-in and the real OpenIddict service implement the same contract.

## Files

| File | What it fixes |
|---|---|
| `contract-v1.md` | Endpoints, listeners, discovery, JWKS, authorize, token, revoke, introspect, endsession, hand-off and attestation, versioning |
| `openid-configuration.schema.json` | JSON Schema (2020-12) for the discovery document |
| `claims.schema.json` | JSON Schema for access-token and ID-token payloads (and the JOSE header) |
| `errors.md` | Error codes clients see, per endpoint; internal reason codes; allowlist 403 |
| `audit-events.schema.json` | JSON Schema for the audit events contract tests may assert |
| `OPEN-QUESTIONS.md` | Every point the sources don't state (OQ-n), for the Tech Lead to answer or relay |
| `SSE-BUILD-LOG-PENDING.md` | SSE's step log. SSE2 creates `code/docs/BUILD-LOG.md` and `code/README.md` and copies these entries over; SSE never writes in `docs/` (TL Decision 2026-11-02, OQ-27) |

## Labels used on every item

- **[SRC: ...]**: stated in a source below, with section and line. Line numbers refer to the snapshots named here.
- **[ASSUME A-n → OQ-n]**: not stated in any source. A POC assumption made so the draft is complete;
  it must be confirmed or changed by the person named in `OPEN-QUESTIONS.md` before freeze.
- **[CONFLICT → OQ-n]**: two sources disagree. The draft picks one side and says which.

## Sources (read only; md5 at read time)

| Short name | File | md5 | Notes |
|---|---|---|---|
| PLAN | `plans/login-hub-build-plan.md` (TL) | `ae18d66482606f9fdf334f0b953065b4` (508 lines, 11:27:56 PT) | Newest at v1-d2 (re-checked 11:38 PT). v1-d1 brief named `8c42fbdd`; it changed at 11:27:56 (adds S1 check 4b, L243). Snapshot: `/tmp/sse-d0/plan-ae18d664.md` |
| LD | `login-service-proposal.md` (SA) | `5c68265cdd2d966734109ec28da4c312` (rev 11:32 PT, file time 11:32:04) | Newest at v1-d2 (re-checked 11:38 PT; matches the TL's `5c68265c`). v1-d1 cited `bd70f29a` (rev 11:27/11:28). 11:30/11:32 revs add only the Nevada password-class rule (§7, LS-OQ-52, Q28, decision rows 107-108) and password-class contract tests (§16.17.4); no contract item changes, line numbers shift +2/+4/+5, all cites re-checked. Snapshot: `/tmp/sse-d0/login-doc-5c68265c.md` |
| AUD | `audit-log-proposal.md` §7A.4 envelope | `d835c3575874c5c2f2b8ae6f17c551fd` | Envelope field names. Snapshot: `/tmp/sse-d0/aud-d835c357.md` |
| ALW | `ip-allowlist-poc/README.md` L30-50 | `eb7cbf130e8d49fcdc041e79a4fac270` | Exact minimal 403 (read only, not edited). Snapshot: `/tmp/sse-d0/alw-readme-eb7cbf13.md` |
| UX | `login-hub-mockups/` README, DECISIONS D-37, `07-handoff.html` notes | (read only) | Screens 2, 6, 7 built; screen 8 (two variants) being built by UX (TL, 2026-11-02) |

Rule (TL Decision 2026-11-02, OQ-2): always cite the newest source; snapshot each into `/tmp/sse-d0/` with the md5 in the file name.

## Rules for this folder

- Code and contract live only under `/workspace/cachd-ones/code/`, local git only: no remote, no push
  (pre-push hook always fails), no credentials. Branch `feat/contract-v1` (plan §5.1).
- SSE writes only `contract/`, `src/`, `ui/`. `tests/`, `docs/`, `tools/` belong to SSE2.
- Contract docs are Tier 0. Changes after freeze go to `v1.1` with the reason and a contract-test
  change in the same change set (plan §3.1). The SA folds the frozen contract into LD §16.17.6.
