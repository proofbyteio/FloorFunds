# Login contract v1: DRAFT (not frozen)

Owner: SSE. Status: **DRAFT v1-d3** (not frozen), written Mon Nov 2, 2026, 11:30-11:40 AM PT (v1-d1); revised from 11:37 AM PT with the TL's answers (v1-d2); revised ~7:55-8:20 PM PT with the TL rulings, Addendum and G1/G2 (v1-d3).
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
| `device-attestation.schema.json` | JSON Schema for the device agent's attestation JWS (header and claims), sent as `device_attestation` |
| `OPEN-QUESTIONS.md` | Every point the sources don't state (OQ-n), for the Tech Lead to answer or relay |
| `SSE-BUILD-LOG-PENDING.md` | SSE's step log. SSE2 creates `code/docs/BUILD-LOG.md` and `code/README.md` and copies these entries over; SSE never writes in `docs/` (TL Decision 2026-11-02, OQ-27) |

## Labels used on every item

- **[SRC: ...]**: stated in a source below, with section and line. Line numbers refer to the snapshots named here.
- **[OPEN → OQ-n]**: not decided. The draft uses the safe default or TL Rec named in `OPEN-QUESTIONS.md`; the named owner must confirm before freeze.
- **[POC assumption → OQ-n]**: a working choice with no source, kept only until the owner answers (only §7.4 setting location, OQ-40).
- (Retired at v1-d3: the v1-d1/d2 `ASSUME` and `SSE Proposal` labels; none remain in the contract files.)
- **[CONFLICT → OQ-n]**: two sources disagree. The draft picks one side and says which.
- **[TL Decision 2026-11-02, OQ-n]**: answered by the Tech Lead on Nov 2 (v1-d2 relay; v1-d3 rulings file below); the source doc may not say it yet.
- **[Mohanraj Decision 2026-11-02 (t43u), OQ-n]**: decided by Mohanraj in his own message t43u, Nov 2 12:00 PM PT, verbatim: "In GitHub and 12 hr is fine but if we can have it configurable then it will be better".
- **[DOC SILENT → OQ-n]**: a TL answer or source leaves this open; the draft states what it does meanwhile.

## Sources (read only; md5 at read time)

| Short name | File | md5 | Notes |
|---|---|---|---|
| PLAN | `plans/login-hub-build-plan.md` (TL) | `ae18d66482606f9fdf334f0b953065b4` (508 lines, 11:27:56 PT) | Newest at v1-d2 (re-checked 11:38 PT). v1-d1 brief named `8c42fbdd`; it changed at 11:27:56 (adds S1 check 4b, L243). Snapshot: `/tmp/sse-d0/plan-ae18d664.md` |
| LD (prev) | `login-service-proposal.md` (SA) | `88878681a39e23b82e30d6ad0c505647` (rev 11:41 PT, file time 11:41:20) | Newest at v1-d2 (re-checked 12:03 PT). TL expected `5c68265c` (rev 11:32); the SA revised again at 11:41, so per OQ-2 the newest is used. 11:41 adds the TL attestation path and `hub_launch` (§16.18.5 L1381-1391), the rule 4 pointer (§16.16.1 L1170), `notfromhub` in §8 (L423) and decision row 109 (L693); line shift +1 from L7, +2 from L692, +14 from L1378, all cites re-mapped. Earlier: `5c68265c` (rev 11:32, Nevada password-class rule only), `bd70f29a` (v1-d1). Snapshots: `/tmp/sse-d0/login-doc-88878681.md`, `login-doc-5c68265c.md`, `login-doc-bd70f29a.md` |
| LD | `login-service-proposal.md` (SA) | `b73857941e426bbb796d2e44a92d2f43` (rev 8:15 PM PT, file time 20:10:07) | Newest at v1-d3 (re-checked 8:11 PM PT; PLAN, AUD, ALW unchanged). Changed from `88878681` during this pass: adds rev note L6, §16.19 (L1412-1445, SA proposals on OQ-15/22/23/24 + `t43u`), rows 110-118 (L695-703), §17 items 6-7, OQ-38 rename, OQ-11 `attestation`, L1401 OQ-41 wording. Shift: +1 from old L6, +10 from old L694, +44 from old L1402, +46 from old L1409; all LD cites re-mapped by `/tmp/sse-d0/wip/remap_ld.py` and spot-checked. Snapshot: `/tmp/sse-d0/login-doc-b7385794.md` |
| TLR | `reviews/contract-v1-tl-rulings.md` (TL) incl. Addendum and G1/G2 | `9012597c8e0b145988d95a2e034d20e0` | Applied at v1-d3. Snapshot: `/tmp/sse-d0/tl-rulings-9012597c.md` |
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
