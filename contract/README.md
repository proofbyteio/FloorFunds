# Login contract v1: DRAFT (not frozen)

Owner: SSE. Status: **DRAFT v1-d1**, written Mon Nov 2, 2026, 11:30-11:40 AM PT.
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
| `SSE-BUILD-LOG-PENDING.md` | SSE's step log; `code/docs/BUILD-LOG.md` doesn't exist and SSE may not create it |

## Labels used on every item

- **[SRC: ...]**: stated in a source below, with section and line. Line numbers refer to the snapshots named here.
- **[ASSUME A-n → OQ-n]**: not stated in any source. A POC assumption made so the draft is complete;
  it must be confirmed or changed by the person named in `OPEN-QUESTIONS.md` before freeze.
- **[CONFLICT → OQ-n]**: two sources disagree. The draft picks one side and says which.

## Sources (read only; md5 at read time)

| Short name | File | md5 | Notes |
|---|---|---|---|
| PLAN | `plans/login-hub-build-plan.md` (TL) | `ae18d66482606f9fdf334f0b953065b4` (508 lines, 11:27:56 PT) | Brief named `8c42fbdd`; I read that version in full, then it changed at 11:27:56 (adds S1 check 4b, L243). Snapshot: `/tmp/sse-d0/plan-ae18d664.md` |
| LD | `login-service-proposal.md` (SA) | `bd70f29ac04305ba570253192ccbf58a` (rev 11:27/11:28 PT) | Brief named rev 11:23 `6c489506`; that file no longer exists anywhere on the box (the SA's `.v1q` backup was overwritten 11:29:36). Snapshot used for line cites: `/tmp/sse-d0/login-doc-bd70f29a.md`. See OQ-2 |
| AUD | `audit-log-proposal.md` §7A.4 envelope | `d835c3575874c5c2f2b8ae6f17c551fd` | Envelope field names |
| ALW | `ip-allowlist-poc/README.md` L30-50 | (read only, not edited) | Exact minimal 403 |
| UX | `login-hub-mockups/` README, DECISIONS D-37, `07-handoff.html` notes | (read only) | Screens 2, 6, 7 built; 8 not built |

## Rules for this folder

- Code and contract live only under `/workspace/cachd-ones/code/`, local git only: no remote, no push
  (pre-push hook always fails), no credentials. Branch `feat/contract-v1` (plan §5.1).
- SSE writes only `contract/`, `src/`, `ui/`. `tests/`, `docs/`, `tools/` belong to SSE2.
- Contract docs are Tier 0. Changes after freeze go to `v1.1` with the reason and a contract-test
  change in the same change set (plan §3.1). The SA folds the frozen contract into LD §16.17.6.
