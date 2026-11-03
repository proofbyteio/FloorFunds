# Login contract v1: DRAFT (not frozen)

Owner: SSE. Status: **DRAFT v1-d4** (NOT frozen; awaiting SSE2 check and TL freeze), written Mon Nov 2, 2026, 11:30-11:40 AM PT (v1-d1); revised from 11:37 AM PT with the TL's answers (v1-d2); revised ~7:55-8:20 PM PT with the TL rulings, Addendum and G1/G2 (v1-d3); revised 8:16-8:24 PM PT with the TL's 8:12 PM / 8:14 PM replies, LD §16.19 rows 110-119 and Addendum 2 (v1-d4).
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
| `standin-control.md` | Stand-in control API on the separate control listener (reset, clock, PCs, fake agent, users/TOTP, faults, audit) and fixtures [TL Decision 2026-11-02, CR-05] |
| `ui-driver.md` | How tests drive the stand-in sign-in UI: form fields, test ids, assertable strings [TL Decision 2026-11-02, CR-05] |
| `check_schemas.py` | Schema self-check with positive and negative samples (run: `/tmp/sse-d0/venv/bin/python contract/check_schemas.py`; exit 0 = all pass) |
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
- **Cite precedence:** where this contract and `reviews/contract-v1-tl-rulings.md` differ on an LD line cite, the contract's cites (LD md5 `560cb6ab`) govern [TL Decision 2026-11-02 8:26 PM].
- **[DOC SILENT → OQ-n]**: a TL answer or source leaves this open; the draft states what it does meanwhile.

## Sources (read only; md5 at read time)

| Short name | File | md5 | Notes |
|---|---|---|---|
| PLAN | `plans/login-hub-build-plan.md` (TL) | `ae18d66482606f9fdf334f0b953065b4` (508 lines, 11:27:56 PT) | Newest at v1-d2 (re-checked 11:38 PT). v1-d1 brief named `8c42fbdd`; it changed at 11:27:56 (adds S1 check 4b, L243). Snapshot: `/tmp/sse-d0/plan-ae18d664.md` |
| LD (prev) | `login-service-proposal.md` (SA) | `88878681a39e23b82e30d6ad0c505647` (rev 11:41 PT, file time 11:41:20) | Newest at v1-d2 (re-checked 12:03 PT). TL expected `5c68265c` (rev 11:32); the SA revised again at 11:41, so per OQ-2 the newest is used. 11:41 adds the TL attestation path and `hub_launch` (§16.18.5 L1381-1391), the rule 4 pointer (§16.16.1 L1170), `notfromhub` in §8 (L423) and decision row 109 (L693); line shift +1 from L7, +2 from L692, +14 from L1378, all cites re-mapped. Earlier: `5c68265c` (rev 11:32, Nevada password-class rule only), `bd70f29a` (v1-d1). Snapshots: `/tmp/sse-d0/login-doc-88878681.md`, `login-doc-5c68265c.md`, `login-doc-bd70f29a.md` |
| LD | `login-service-proposal.md` (SA) | `b73857941e426bbb796d2e44a92d2f43` (rev 8:15 PM PT, file time 20:10:07) | Newest at v1-d3 (re-checked 8:11 PM PT; PLAN, AUD, ALW unchanged). Changed from `88878681` during this pass: adds rev note L6, §16.19 (L1412-1445, SA proposals on OQ-15/22/23/24 + `t43u`), rows 110-118 (L695-703), §17 items 6-7, OQ-38 rename, OQ-11 `attestation`, L1401 OQ-41 wording. Shift: +1 from old L6, +10 from old L694, +44 from old L1402, +46 from old L1409; all LD cites re-mapped by `/tmp/sse-d0/wip/remap_ld.py` and spot-checked. Snapshot: `/tmp/sse-d0/login-doc-b7385794.md` |
| TLR | `reviews/contract-v1-tl-rulings.md` (TL) incl. Addendum and G1/G2 | `9012597c8e0b145988d95a2e034d20e0` | Applied at v1-d3. Snapshot: `/tmp/sse-d0/tl-rulings-9012597c.md` |
| PLAN (v1-d4) | `plans/login-hub-build-plan.md` (TL) | `10fa9853877c6e966a5de232f08d3e1a` | Newest at v1-d4. Only L240 (S1 check 3: "no valid single-use `hub_launch`") changed; no line shift. L130 rule 4 is stale; TL fixes it next revision, C §5.7 governs [TL Decision 2026-11-02, Addendum 2 (b)]. Snapshot: `/tmp/sse-d0/plan-10fa9853.md` |
| PLAN (latest) | `plans/login-hub-build-plan.md` (TL) | `444ddba482fdfc0b69abd9aa5215c8b2` | [TL Decision 2026-11-02 8:26 PM]. From `10fa9853`: L128, L130 (rule 4 now `hub_launch`, contract §5.7 governs), L133 changed in place; no line added or removed, so every PLAN cite still maps. Snapshot: `/tmp/sse-d0/plan-444ddba4.md` |
| LD (v1-d4 start) | `login-service-proposal.md` (SA) | `c2b7e689e900e6e6d5d6ad7988aa3d05` (rev 8:20 PM PT, file time 20:11:45) | Newest at v1-d4. From `b7385794`: +1 line from L6, +2 from old L704 (rows 117-119 rewritten). All LD cites re-mapped (commit e312124). The rulings cite `b7385794` line numbers; the contract cites `c2b7e689`. Snapshot: `/tmp/sse-d0/login-doc-c2b7e689.md` |
| LD (latest) | `login-service-proposal.md` (SA) | `560cb6abd69ca4b5099eab57a17405b8` (rev 8:25 PM PT, file time 20:17:36) | Moved during v1-d4 (found at the final check 8:23 PM PT). From `c2b7e689`: +1 from L6; +3 after old L705 (rows 120-122, SA-Q1..Q3); +42 after old L1447 (new §16.20); +1 after old L1471. All contract LD cites re-mapped (script `/tmp/sse-d0/wip/remap_ld3.py`); §16.20 items are Proposal (SA) and stay OPEN. Snapshot: `/tmp/sse-d0/login-doc-560cb6ab.md` |
| TLR (now) | `reviews/contract-v1-tl-rulings.md` (TL) incl. Addendum 8:12 PM, Correction 8:14 PM, Addendum 2 | `5bcaedc9fc1c748743243a7dfd1a3a38` (file time 20:14:13) | Applied at v1-d4. Snapshot: `/tmp/sse-d0/tl-rulings-5bcaedc9.md` |
| AUD | `audit-log-proposal.md` §7A.4 envelope | `d835c3575874c5c2f2b8ae6f17c551fd` | Envelope field names. Snapshot: `/tmp/sse-d0/aud-d835c357.md` |
| ALW | `ip-allowlist-poc/README.md` L30-50 | `eb7cbf130e8d49fcdc041e79a4fac270` | Exact minimal 403 (read only, not edited). Snapshot: `/tmp/sse-d0/alw-readme-eb7cbf13.md` |
| UX | `login-hub-mockups/` README, DECISIONS D-37, `07-handoff.html` notes | (read only) | Screens 2, 6, 7 built; screen 8 (two variants) being built by UX (TL, 2026-11-02) |

Rule (TL Decision 2026-11-02, OQ-2): always cite the newest source; snapshot each into `/tmp/sse-d0/` with the md5 in the file name.

## Rules for this folder

- Code and contract live only under `/workspace/cachd-ones/code/`, local git only: no remote, no push
  (pre-push hook always fails), no credentials. Branch `feat/contract-v1` (plan §5.1).
- SSE writes only `contract/`, `src/`, `ui/`. `tests/`, `docs/`, `tools/` belong to SSE2.
- Contract-test validators MUST run with format assertion on (e.g. JsonSchema.Net `RequireFormatValidation`); the UUID `pattern` is a backstop, not a substitute [TL Decision 2026-11-02, CR-21].
- Contract docs are Tier 0. Changes after freeze go to `v1.1` with the reason and a contract-test
  change in the same change set (plan §3.1). The SA folds the frozen contract into LD §16.17.6.
