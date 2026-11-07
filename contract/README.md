# Login contract v1: FROZEN v1 @ e363090 (tag contract-v1)

Owner: SSE. Status: **FROZEN v1 @ e363090** (local annotated tag `contract-v1`) [Freeze status, TL 2026-11-02 8:53 PM]. Freeze record: `reviews/contract-v1-tl-rulings.md` "FREEZE RECORD" (md5 33e71504). Basis: SSE2 FREEZE-OK on an exact export of e363090 (sse2/tests-docs ac3652c). Post-freeze edits are v1.1 only, each with its reason and a contract-test change. History: v1-d1 11:30-11:40 AM PT; v1-d2 from 11:37 AM; v1-d3 ~7:55-8:20 PM; v1-d4 8:16-8:48 PM PT.
Frozen per build plan §3.1 "Versioning" after SSE2's adversarial pass and TL review. SI1 (the stand-in) builds against tag `contract-v1`.

Clients (Cash Advance BFF, hub, back office, reports) may depend **only** on what is written
in this folder (plan §3.1). The stand-in and the real OpenIddict service implement the same contract.

## v1.1 changes (post-freeze; reason + contract-test change in the same change set, plan §3.1)

| Change | Reason | Contract tests |
|---|---|---|
| **S2a location login config (screen 9)**: new `v1.1-S2a-location-login-config.md` and `location-login-config.schema.json`; `audit-events.schema.json`: `auth.provider.changed` re-specified (closed details, `approval_id` removed, `row_version`/`change_id`/`changed_fields`/`reason` added, downgrade ⇒ Super Admin + reason), `auth.config.change_denied` gains a `location_login_config` branch (v1 `config` branch unchanged), `auth.provider.change_requested` removed | Confirmed DEC-096 (Mohanraj 7:20 AM PT Nov 4: no four-eyes, changes apply immediately, fallback-on Super Admin only with a reason) and DEC-098 (7:22 AM PT: MFA-off Super Admin only with a reason); TL S2a brief + 7:27 AM addition (API, readers, tables, grants). Sources/md5s in the spec §1 | `check_schemas.py`: `change_requested` now expected rejected; v1 `provider.changed` shapes now expected rejected. New `check_v1_1_s2a.py`: validates every spec example, secret_ref leak checks, negatives. Run both: `/tmp/sse-d0/venv/bin/python contract/check_schemas.py && /tmp/sse-d0/venv/bin/python contract/check_v1_1_s2a.py` |
| **DEC-107 configurable reason length (screen 9 reason only)**: `v1.1-S2a-location-login-config.md` §1 (rows DEC-107, TLD 16:32, D-106), §3.2 Reason, §5.2 `reason_length`, §7 `reason`, §11 copy and hint, §13, §14 and the label rule now say `LoginConfig:ReasonMinLength`..`LoginConfig:ReasonMaxLength` code points after trim (startup snapshot, defaults min 10 / max 500, Confirmed DEC-108 (PM, Nov 5 2026 6:47 AM PT), startup refuses anything outside 10 ≤ min ≤ max ≤ 500); `location-login-config.schema.json` `access_rules.reason_trimmed_length` const `{min 10, max 500}` → `{min_config_key, max_config_key, default {10, 500}, hard_bounds {10, 500}}`; `$defs/reason` and `field_error_code` descriptions | DEC-107 (PM t149u/t150u; t151u via TL: default min not 100; TL approval 4:11 PM PT); TLD 2026-11-04 16:32 Q1=A (DB CHECK `llcc_reason` 10..500 stays, db/ frozen; follow-up DB-WIDEN); no new error code (errors.md frozen) | `check_v1_1_s2a.py`: §11 pin now the copy "Enter a reason of {min} to {max} characters."; access_rules old fixed `{10, 500}`, hard bounds 1..500 and default min 100 rejected; config keys pinned; spec pins for §3.2 / §3.5 / §13 / Sources and no stale fixed-range text; `Admin.Api.Checks` startup refusal, boundaries from the constants and on a 50/200 host |
| `audit-events.schema.json` `auth.provider.changed` `details.reason` (L374): string branch gains `minLength: 10` (pattern / maxLength 500 unchanged) and the DEC-107 description. The `auth.config.change_denied` reason (L393) already had `minLength 10` / `maxLength 500` / the pattern at 760521e, so it is not edited | TLD 2026-11-04 16:32 Q1=A (the 4 schema places use the pattern + minLength 10 + maxLength 500) | `check_v1_1_s2a.py`: event reason 9 / 501 chars and 9 astral rejected, 10 / 500 chars and 10 astral accepted |
| `location-login-config.schema.json` `change_history` item `reason` (L488 at 760521e): string branch gains `minLength: 10` | TLD 2026-11-04 16:32 Q1=A | `check_v1_1_s2a.py`: history reason of 9 / 501 chars rejected |
| `location-login-config.schema.json`: new `$defs/reason_length_limit` (integer 10..500, readOnly); `login_config_view_super` / `_restricted` gain optional `reason_min_length` / `reason_max_length`; new `login_config_get_super` / `_restricted` (GET 200, both required) and `login_config_saved_super` / `_restricted` (PUT 200 / POST 201, both forbidden); spec §3.1 GET examples now `login_config_get_*` with the two fields, `post_201_db` now `login_config_saved_restricted` | TLD 2026-11-04 16:32 Q2=(ii): Edit reads the bounds from the location GET; PUT / POST success bodies unchanged | `check_v1_1_s2a.py`: GET without either field, min 9, max 501 rejected; saved body with either field rejected; write / create bodies with either field rejected (unknown_field); `Admin.Api.Checks`: every GET / PUT / POST 2xx validated against the get / saved defs |
| New `GET /api/orgs/login-config/limits` (spec §3.5, example `limits_200`) and `$defs/login_config_limits` = exactly `{reason_min_length, reason_max_length}` | TLD 2026-11-04 16:32 Q2=(ii): Create has no location yet; same read rule as the location GET (401 / 403 `forbidden`, existing codes and shapes) | `check_v1_1_s2a.py`: `limits_200` required and valid; extra key, missing key, min 9, max 501, string value rejected; 50/200 accepted; `Admin.Api.Checks`: 200 (defaults and 50/200), 401, 403, no event |
| **DEC-106 Partner Admin and Super Admin can both turn MFA back on (screen 9)**: `v1.1-S2a-location-login-config.md` label rule, §1 (row DEC-106; D-106 row now also names `partner-mfa-off`), §4 upgrades paragraph, §6 sources, §11 (`partner-mfa-off`, `partner-create` keeps MFA locked), new examples `put_req_pa_mfa_on` / `ev_changed_pa_mfa_on`. Relabel only: the rules were already in §4 step 9 / §6 (`mfa_required` → true passes for both admins, no reason); `allow_db_fallback` → false for a Partner Admin stays as is, **Confirmed DEC-118/120/121** (PM, Nov 5 2026; own partner only (DEC-120), no reason needed, a sent reason follows §3.2 (DEC-121); relabelled in P4n). No schema, `access_rules` or error-code change; `errors.md` untouched | Mohanraj, PM t145u (DEC-106, project-plan row logged 4:08 PM PT); UX D-106 mockup 09 md5 2b17464c | `check_v1_1_s2a.py`: both examples required and valid; event pins (partner actor, `changed_fields` [mfa_required], before false → after true, reason null; no actor / no before rejected); spec pins for the DEC-106 labels and the `partner-mfa-off` hint; the old TLD 'upgrades being confirmed' labels gone; §6 Partner Admin own-partner row unchanged (✔ ✔ 403 403) |

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
| `v1.1-S2a-location-login-config.md` | v1.1 S2a: location login config API (screen 9), check order, errors, role × scope matrix, events, DDL intent, RO/RW grants, readers, deferred, OPEN/ASSUMPTION register |
| `location-login-config.schema.json` | v1.1 S2a: JSON Schema for the location login config request/response bodies (secret_ref server-filled from deployment config `Entra:SecretRef`, never in a body or response [TLD 2026-11-04 9:49, D-51]) |
| `check_v1_1_s2a.py` | v1.1 S2a contract test (run: `/tmp/sse-d0/venv/bin/python contract/check_v1_1_s2a.py`; exit 0 = all pass) |
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
- v1.1 S2a also uses **ASSUMPTION A-n**, **OPEN OQ-S2a-n** and **DRAFT** (proposed; named owner must sign off), defined in that spec.
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
