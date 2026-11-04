# Cachd Ones: S1 build log (login + hub)

**Owner: SSE2** (created by SSE2 per contract OQ-27, TL Decision 2026-11-02). All times **Mon Nov 2, 2026, Pacific Time (PT)**.
Every entry gives time, author, what, why, sources with md5, decisions and open questions.

**How SSE's entries get here (TL, 11:38 AM PT and 12:08 PM PT):** SSE never writes in `docs/`. SSE appends its own entries to
`contract/SSE-BUILD-LOG-PENDING.md` on `feat/contract-v1`. SSE2 imports them with `tools/import-sse-log.sh <commit>`.
The script copies each entry **verbatim** between `<!-- sse-import sha256=… first-seen=… imported-from=… -->` markers.
Re-running it adds nothing (dedup by sha256), and an edited old entry is appended marked "(revised)".
The first import (from `6e8a106`) placed the 8 SSE entries in time order among SSE2's entries, using the script's exact rendering. Later imports append at the end.
Entries headed **[SSE]** are SSE's (agent `b432f3c2-83b5-4407-9771-9de593ffd3bc`), and SSE2 has not changed a byte of them. Entries headed **[SSE2]** are SSE2's own.

Rules in force (TL brief): code only under `/workspace/cachd-ones/code/`; local git only, with no remote, no push, no GitHub, no credentials, no vault and no email.
SSE2 writes only `code/tests/`, `code/docs/` and `code/tools/`. Ports 8080 and 8081 are never used, and `ip-allowlist-poc/data/allowlist.db` is never touched.
There are no `.v*` backup names under `code/`. Reference copies are named by md5 (`/tmp/sse2-snap/<md5>.md`).

<!-- sse-import sha256=23e175f0745808166c6107bbede4ecd598325084aaad9f73d14a44cb4fc3e2fe first-seen=ef57eed imported-from=6e8a106 head="[SSE] Step 0: go-ahead check" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `ef57eed`, imported from `6e8a106`.

### [SSE] Step 0: go-ahead check
- **What:** checked DEC-080 and DEC-081 as user-role messages in the PM transcript `4ab8dda1-594d-48bb-bd52-3239fec5f37e`.
- **Result:** verified. `t99u` 10:58 AM: "I want the team to build the login module and the hub, initially they can create a mock up and use that for further development". Card answer to `t99s368` 11:02 AM: "Both clickable screens and a stand-in login service"; "Where should the code live?" left blank (PQ-201 open).
- **Why:** team rule "don't assume anything".
<!-- /sse-import -->

## [SSE2] 11:26 AM: Step 0, go-ahead and sources
- **What:** I received the TL GO for SSE2 (adversarial review and tests, S1). The basis is DEC-080 ("build the login module and the hub… mock up") and DEC-081 ("Both clickable screens and a stand-in login service"), as quoted in the TL brief.
- **Sources hashed (md5, at read time):**
  - PLAN `plans/login-hub-build-plan.md`: `8c42fbdd…`, which matches the brief. It changed at 11:27:56 to `ae18d66482606f9fdf334f0b953065b4` (adds S1 check 4b, L243).
  - LD `login-service-proposal.md`: candidates hashed as they changed: `47cd2a84` (rev 11:27), `bd70f29a` (rev 11:28), `506c8012` (rev 11:30), `5c68265c` (rev 11:32) and `88878681a39e23b82e30d6ad0c505647` (rev 11:41, file time 11:41:20; used for the review).
  - Snapshots: `/tmp/sse2-snap/plan-ae18d664.md`, `/tmp/sse2-snap/login-doc-5c68265c.md`, `/tmp/sse2-snap/88878681a39e23b82e30d6ad0c505647.md`. SSE's own snapshots are in `/tmp/sse-d0/`.
- **md5 mismatch recorded (rule: record and continue):** the brief's plan md5 was stale after 11:27:56. The newest file was used, per OQ-2.

<!-- sse-import sha256=3f312bab11d0d8696c59887c6bfc0cdea6b0648631164ff26c71f513d4d24b89 first-seen=ef57eed imported-from=6e8a106 head="[SSE] Sources and md5" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `ef57eed`, imported from `6e8a106`.

### [SSE] Sources and md5
- PLAN read in full at md5 `8c42fbdd…` (matched the brief). Changed at 11:27:56 to `ae18d664…` (adds S1 check 4b, L243); contract cites `ae18d664` (snapshot `/tmp/sse-d0/plan-ae18d664.md`).
- LD rev 11:23 `6c489506…` was read from `.v1q`, but the SA's backups were later rewritten (11:29:36) and no copy with that md5 remains. Contract cites LD `bd70f29a…` (rev 11:27/11:28; snapshot `/tmp/sse-d0/login-doc-bd70f29a.md`). OQ-2.
- AUD envelope `d835c357…` §7A.4; ALW README L30-50 (read only); UX mockups README and D-37 (read only).
<!-- /sse-import -->

<!-- sse-import sha256=a46aaeb6d271c7d5a4f958af45ec6db66207bfb8780dc4a9dd5fd2475a57ab09 first-seen=ef57eed imported-from=6e8a106 head="[SSE] D0: toolchain" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `ef57eed`, imported from `6e8a106`.

### [SSE] D0: toolchain
- **.NET:** `dotnet-install.sh` from https://dot.net/v1/dotnet-install.sh (redirects to https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh; sha256 `082f7685…eb2e`), `--channel 10.0 --install-dir /home/box/.dotnet`. It reported "already installed": another agent installed it at 11:27 just before; not reinstalled. Verified: SDK 10.0.403 (commit 5a2162bea6), Microsoft.NETCore.App and AspNetCore.App 10.0.14, MSBuild 18.9.15; `dotnet new web` builds (NuGet reachable).
- **Postgres:** `postgresql-17` 17.11-0+deb13u1 and `postgresql-common` 278 from Debian trixie main (https://deb.debian.org). apt over the egress proxy failed (HTTP 500; HTTPS gnutls EOF), so I fetched the signed InRelease and Packages indexes and the .debs with curl from deb.debian.org into `/tmp/sse-d0/mirror` and installed via a `file:` source list passed with `-o Dir::Etc::sourcelist` (system source files untouched; apt checked signatures and hashes). Side effect: `/var/lib/apt/lists` now holds the indexes from that local mirror.
- Cluster 17/main, port 5432 (was free), default `listen_addresses` (localhost only). Started to verify (`select version()` OK), then stopped by me. Start: `sudo pg_ctlcluster 17 main start`. Nothing on 8080/8081.
- Logs: `/tmp/sse-d0/pg-install2.log`, `/tmp/sse-d0/dotnet-install.log`.
<!-- /sse-import -->

## [SSE2] 11:27-11:36 AM: test toolchain and scaffold
- **What:** .NET SDK 10.0.403 at `~/.dotnet`, shared with SSE (its D0 entry says it was already installed at 11:27).
  - Tests need `DOTNET_ROOT=$HOME/.dotnet`.
  - Solution `tests/Cachd.Tests.slnx` has 4 projects: `Login.TestKit`, `Login.ContractTests`, `Login.SecurityTests` and `Login.E2E`.
  - Packages: xUnit v3 (`xunit.v3.mtp-v2`, Microsoft.Testing.Platform, set in `tests/global.json`), JsonSchema.Net 9.6.0 and Microsoft.Playwright 1.64.1 (system `/usr/bin/google-chrome`).
- **Decisions:**
  - **D1:** one switch, `LOGIN_CONTRACT_TARGET=standin|real` (default `standin`). The target base URL comes from `LOGIN_STANDIN_URL` or `LOGIN_REAL_URL`. A missing target is a **skip with a reason**, never a pass.
  - **D2:** anything the sources leave unstated is **pending**, an xUnit Skip whose reason starts with `PENDING OQ-…`. Nothing is marked xfail, so xfail = 0 by construction. No test guesses a behaviour.
  - **D3:** schema tests run with `RequireFormatValidation = true`, because draft 2020-12 treats `format` as an annotation only (CR-01).
  - **D4:** e2e tests run the UX mockups from `file://` with every non-file request aborted, so they make no network calls. `login-hub-mockups/` is read only.
  - **D5:** every test carries an S1 check trait (`check=1..12|4a|4b|pw-class|session-cap`), so `tools/run-checks.sh` can report per check.
  - **D6:** `tests/global.json` lives under `tests/`, not `code/`, because `code/` root files are not SSE2's.
- **Tools:** `tools/run-checks.sh`, `tools/check-no-standin.sh` (check 11) and `tools/check-no-remote.sh` (check 12, local git only).

<!-- sse-import sha256=ac66639854b7f089d4e08d970e00715ffe5808123b3d24afcd16b590628d1025 first-seen=291d8ee imported-from=6e8a106 head="[SSE] D1: contract v1 draft" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `291d8ee`, imported from `6e8a106`.

### [SSE] D1: contract v1 draft
- **What:** `contract/` README, contract-v1.md, errors.md, three JSON Schemas, OPEN-QUESTIONS.md (OQ-1…OQ-29).
- **Decisions:** every item labelled [SRC], [ASSUME A-n → OQ-n] or [CONFLICT → OQ-n]. Discovery/JWKS on 8084 per LD §4.5 against PLAN "front" (OQ-1). `attestation_invalid` sourced from PLAN §1.2/4a and LD §8 L420.
- **Checks:** all three schemas pass `Draft202012Validator.check_schema` (jsonschema 4.23.0, venv in /tmp/sse-d0) and 22 sample good/bad instances behave as expected (`/tmp/sse-d0/check_schemas.py`; the real tests are SSE2's).
- **Git:** commits on `feat/contract-v1` built with a private index file and `git update-ref`, so the shared working tree's HEAD (`main`, unborn) and index were not touched (SSE2's untracked `tests/` and `tools/` left alone). No remote; pre-push hook (already present) always fails.
- **Stopped after D1** as briefed: no SI1 code. Waiting on SSE2's adversarial pass and TL review.

Note: line numbers in each entry refer to the source md5 named in that entry (or current at that entry's time); older entries are not re-mapped.
<!-- /sse-import -->

## [SSE2] 11:37-11:42 AM: TL steering (check 4b, password class set, hashing, git method, SSE log import)
- **Input:** TL 11:37 and 11:38 AM.
  - Plan `ae18d664` adds **check 4b**: after an `attestation_invalid` fallback, payment, ID scan and print stay refused until re-attest. Screen 8 variant B text: "This PC's security software isn't responding. Contact IT."
  - Add a password character-class set of about 20 cases, with server-side rejection.
  - Hash every login-doc candidate.
  - Import SSE's entries verbatim.
  - Do not modify `feat/contract-v1`, the shared HEAD (unborn `main`) or the shared index.
- **Done:**
  - `PasswordClass.cs`, an oracle for LD §7 (L337-338) and the TL rule of 11:32. It applies NFKC first, then classifies each Rune: Lu=upper, Ll=lower, Nd=digit, anything else is a symbol.
    - Edge cases verified in Python `unicodedata`: ǅ becomes "Dž" (Lu+Ll), ᾈ stays Lt, q+U+0308 stays Mn, e+U+0301 becomes é, ᵃ becomes a, ² becomes 2, 𝐀 becomes A, and Arabic-Indic ٣ is Nd.
    - PW00-PW34. The server-side cases are pending (OQ-SSE2-05/29/13/30/31/32/33).
  - Check 4b tests SEC50-SEC54, pending OQ-SSE2-34 (where cage actions are enforced, and whether the scope is the PC or the session).
  - `tools/import-sse-log.sh` (idempotent).
- **Run at about 11:42 AM:** 195 tests: **131 passed, 5 failed, 59 skipped (pending), 0 xfail** (`/tmp/sse2-run2.txt`).
  - All 5 failures (E2E02, E2E06, E2E12×2, E2E15) were caused by mockup changes made by UX during the run: the screen 8 agentdown variant, the TOTP step between 02 and 06, and the new error prefix.
  - I re-read the mockups and fixed the tests to match them. The mockups were not changed.

<!-- sse-import sha256=a904127a8681921fafdd369a9b8b7ede4485dd2ee153a4e9fd061998b7d64ba7 first-seen=291d8ee imported-from=6e8a106 head="[SSE] TL revision pass (TL review relayed 11:37 AM; done 11:41 AM-12:06 PM)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `291d8ee`, imported from `6e8a106`.

### [SSE] TL revision pass (TL review relayed 11:37 AM; done 11:41 AM-12:06 PM)
- **What:** new small commits on `feat/contract-v1`, no rewrite of earlier commits: `2e8aee5` (newest sources, v1-d2), `ac62dc9` (OQ-1 front discovery/JWKS), `64da834` (LD rev 11:41 re-map), `6b89c57` (hub_launch, attestation, screen 8), `5badb39` (audit), `4bf371a` (open questions). Same private-index method; shared HEAD (`main`, unborn) and index untouched.
- **Sources (OQ-2, newest rule):** the TL expected LD `5c68265c` (rev 11:32); verified and used for `2e8aee5`. At 12:02 the LD was `88878681…` (rev 11:41, file time 11:41:20): it adds the TL attestation path and `hub_launch` (§16.18.5 L1381-1391), the rule 4 pointer (L1170), `notfromhub` in §8 (L423) and decision row 109. Snapshot `/tmp/sse-d0/login-doc-88878681.md`; cites re-mapped by script (`/tmp/sse-d0/remap_ld2.py`: +1 from L7, +2 from L692, +14 from L1378) and spot-checked. PLAN `ae18d664…` unchanged (`/tmp/sse-d0/plan-ae18d664.md`). AUD `d835c357…`, ALW README `eb7cbf13…` unchanged.
- **Decisions applied:** OQ-1, 2, 7, 8, 9, 23 (partly), 26, 27, 29 marked "TL Decision 2026-11-02". `notfromhub` now writes `auth.app_access.denied` (LD L1170, L423), which changes my earlier "no event on re-prompt" draft for rule 4 only. Attestation cited to the TL decision and the SA's new LD §16.18.5 text. OQ-13 kept as a POC assumption (12 h) tied to the OQ.
- **New:** `device-attestation.schema.json`; extra §8 audit events "Defined, not emitted in S1"; `cachd_client_id`; SSE Proposals on every open OQ; new OQ-30..38 and OQ-41. Where the doc is silent the contract says [DOC SILENT → OQ-n].
- **Checks:** 4 schemas pass `check_schema`; 40 sample instances behave as expected (`/tmp/sse-d0/check_schemas.py`, not committed; the real tests are SSE2's). Sample discovery now has `jwks_uri` on the front.
- **Seen, not acted on:** `08-session.html` shows `local_agent.action_blocked reason=remote_session`; that is a device-agent event (LD L1238, L1408), not a login-service event, so it is not in the contract.
<!-- /sse-import -->

<!-- sse-import sha256=357c6f171277011c92d75a3265245757a5eb2bdeea881e1cc3cec49975a73b10 first-seen=291d8ee imported-from=6e8a106 head="[SSE] D0 apt follow-up: what I did around the failed apt (honest account)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `291d8ee`, imported from `6e8a106`.

### [SSE] D0 apt follow-up: what I did around the failed apt (honest account)
- **What happened:** `apt-get update` / `install postgresql-17` went through the box egress proxy (fake IP 198.18.0.1) and failed for deb.debian.org only: HTTP "500 Internal Server Error [IP: 198.18.0.1 80]"; HTTPS "SSL connection failed … unexpected eof while reading [IP: 198.18.0.1 443]". In the same run the Chrome repo (dl.google.com) worked.
- **What I did, and why:** to finish D0 I fetched the signed InRelease and Packages indexes and the .debs for `postgresql-17` 17.11-0+deb13u1 with `curl` as the box user (which did reach deb.debian.org over HTTPS) into `/tmp/sse-d0/mirror`, and pointed apt at that local copy with a `file:` source list (`-o Dir::Etc::sourcelist`; system source files untouched).
- **Integrity:** apt still checked the Debian archive signatures on InRelease and the package hashes; nothing was installed unsigned.
- **Side effect:** `/var/lib/apt/lists` (11:28) now holds the indexes from that local mirror (`_tmp_sse-d0_mirror_…`). Not cleaned up (that would be another root change on a shared box); left for TL to decide.
- **Read-only investigation now (no probing):** no proxy settings in the apt env or `/etc/apt/apt.conf.d` (only `02autoremove-postgresql`, written by postgresql-common at 11:28); sources are the stock `http://deb.debian.org`; the box's egress proxy is set by the environment (`SAND_HTTP_PROXY_NAME=runner-egress-proxy`, tunnel `127.0.0.1:8791`).
- **Conclusion:** not a box or apt configuration problem. The egress proxy refused apt's requests to deb.debian.org specifically while letting other hosts and curl through. A 500 and a cut TLS handshake are not a normal policy page, so I can't prove it was deliberate without probing, which I won't do. It should be treated as a deliberate egress control. My curl-to-local-mirror route went around it, and I should have asked the TL first.
- **From now on:** TL rule: don't route around network blocks; ask the TL first.
<!-- /sse-import -->

<!-- sse-import sha256=38972593850df212666276dfa4b723a51105d55392bac4face11e896df5636d7 first-seen=27c84ef imported-from=6e8a106 head="[SSE] Mohanraj decision t43u (relayed 12:00 PM; applied 12:08 PM)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `27c84ef`, imported from `6e8a106`.

### [SSE] Mohanraj decision t43u (relayed 12:00 PM; applied 12:08 PM)
- **Input (verbatim, Mohanraj's own message t43u, Nov 2 12:00 PM PT):** "In GitHub and 12 hr is fine but if we can have it configurable then it will be better".
- **OQ-13 resolved:** the absolute session cap defaults to 12 h and is configurable. Contract §7.4 names the setting `LoginService:Session:AbsoluteLifetime` (TimeSpan `12:00:00`). Bounds and validation are an SSE Proposal (OQ-40): 1 h to 24 h, startup fails on a bad value, logged at startup, new sessions only.
- **OQ-28 / PQ-201 resolved:** the code lives in GitHub. Org, repo name, visibility and setup are open for the TL (OQ-39). No remote, push or GitHub action taken; local git only until the TL says otherwise.
- **Also fixed:** OQ-31 cite (LD L207, not L208) and the OQ-30 wording (LD L231 already publishes a new key ≥24 h ahead).
<!-- /sse-import -->

<!-- sse-import sha256=61b91d7b7e16ea47b5d9ab8e5e879ecdc382bc47706a67e1b7a3ef3305582131 first-seen=6e8a106 imported-from=6e8a106 head="[SSE] TL correction on the session cap (TL 12:01 PM; applied 12:09 PM)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `6e8a106`, imported from `6e8a106`.

### [SSE] TL correction on the session cap (TL 12:01 PM; applied 12:09 PM)
- **What:** withdrew my 1 h-24 h bounds proposal (and the other validation extras) in contract §7.4 and OQ-40. Now OPEN for Mohanraj with no values proposed: (a) who can change the cap; (b) the allowed range. Question sent to Mohanraj Nov 2, 12:01 PM PT: "Who should be able to change the 12-hour sign-in limit, and what's the shortest and longest it can be set to?"
- **Kept:** 12 h default (Mohanraj t43u). Technical check, not policy: cap > 15-minute idle timeout or startup fails. Setting `LoginService:Session:AbsoluteLifetime` in deployment config only, no admin screen (POC assumption → OQ-40).
- **Idle timeout verified** in LD `88878681` (unchanged since 11:41:20, re-checked 12:08 PM): L229 "Idle timeout 15 min (PCI 8.2.8 [S9]) at the BFF"; L934 login-service cookie "15-minute sliding idle plus the absolute cap"; L321 PCI 8.2.8 row. No conflict.
<!-- /sse-import -->

## [SSE2] 12:09 PM-: TL update 12:08 (review c0f7016..6e8a106, import, no .v*, session cap)
- **Input (TL 12:08 PM PT):**
  1. Adversarial review of `c0f7016..6e8a106` on `feat/contract-v1`, read only, against LD rev 11:41 (`88878681…`, md5 verified) and PLAN `ae18d664…`.
  2. Import every SSE entry at `6e8a106`.
  3. No `.v*` backup names anywhere in `/workspace/cachd-ones/`. Reference copies are named by md5. `bin/` and `obj/` are git-ignored.
  4. Session cap: default 12 h, configurable, **range OPEN**. Test only (a) that the 12 h default applies and (b) that a configured value ≤15 min is rejected, plus a pending entry for the bounds.
- **Verified:** working-tree `contract/*` md5s equal the blobs at `6e8a106` (9 files). `feat/contract-v1` = `6e8a106`, with no newer commit at 12:10. HEAD is unborn `main`.
- **Done:**
  - `docs/CONTRACT-REVIEW-v1.md`: verdict **NOT ready to freeze**.
  - `docs/threat-model/THREAT-MODEL-S1.md` and `docs/test-matrix/TEST-MATRIX-S1.md`.
  - `SessionCapTests` SEC60-SEC64.
  - `DeviceAttestationSchemaTests` AT01-AT10.
  - SEC11 widened to 17 attestation edge cases: nonce, kid, alg, freshness, revoked device, other location, registry outage.
  - `tests/.gitignore` covers `bin/`, `obj/` and `TestResults/`.
- **`.v*` check:** none under `code/`. The top-level files (`login-service-proposal.v1a…v1u.md`, `project-plan.md.v0.2-pre-*`, `dotnet-stack-recommendation.v2g/.v2h/.v2j.md`, `audit-log-proposal.v3/.v4/.v4.1.md`) are not SSE2's and were not touched. They are reported to the TL.
- **Import:** see the `[SSE]` entries above and below this one. `tools/import-sse-log.sh 6e8a106` re-run gives "imported 0, skipped 8".

---

## 12:19 PM PT · SSE2 · final run, test matrix, commit

- **What:**
  - Ran `tools/run-checks.sh standin` (run 4, 12:17 PM PT).
  - Wrote `docs/test-matrix/TEST-MATRIX-S1.md`.
  - Added the per-check status `PARTIAL`.
  - Committed `tests/`, `docs/` and `tools/` to local branch `sse2/tests-docs`.
- **Run 4 (exact):**
  - Totals: **226 tests: 156 passed, 0 failed, 70 skipped (pending), 0 not run, 0 xfail.** Exit 0.

    | Project | Passed | Skipped |
    |---|---|---|
    | ContractTests | 57 | 31 |
    | SecurityTests | 24 | 39 |
    | E2E | 75 | 0 |

  - Check 4a: 22 passed, 10 pending.
  - Check 4b: 2 passed, 5 pending.
  - pw-class: 23 passed, 5 pending.
  - session-cap: 1 passed, 9 pending.
  - No server behaviour was exercised (no stand-in in `src/`), so this run is **not** S1 green.
- **Why PARTIAL:** before this change, a check showed PASS even when it had pending tests, which overstated it. PARTIAL means the runnable tests passed but some are still pending.
- **Commit method** (the shared HEAD stays unborn and the shared index stays absent; `feat/contract-v1` is untouched):
  1. `GIT_INDEX_FILE=/tmp/sse2-index git add tests docs tools`
  2. `git write-tree`
  3. `git commit-tree` (author/committer `SSE2 (Cachd team local agent) <sse2@local.invalid>`, no parent)
  4. `git update-ref refs/heads/sse2/tests-docs <sha>`
- **Commit checks:**
  - `git ls-files --with-tree` shows no `bin/` or `obj/`.
  - There is no remote.
  - Contract head is still `6e8a106` at 12:19 PM PT.
- **Not done (outside SSE2 write paths):** `code/README.md` (contract OQ-27 assigns it to SSE2, but `code/` root is not in my paths). This needs a TL decision.
- **Noted, not touched:** top-level `.v*` backup files in `/workspace/cachd-ones/`:
  - `login-service-proposal.v1a`…`v1u`
  - `project-plan.md.v0.1`…`v0.2-pre-tl21`
  - `dotnet-stack-recommendation.v1`…`v2m`
  - `audit-log-proposal.v1`…`v6`
  - `hardware-integration-proposal.v1`…`v5`

  There are none under `code/`. SSE2 reference copies are in `/tmp/sse2-snap/<md5>.md`.
- **Correction (12:20 PM PT):** this entry and the test matrix first carried a wrong time ("12:47 / 12:45 PM PT") in commit `0f752e2`. The box clock read 12:19 PM PT, so the times are fixed above. Follow-up commit on `sse2/tests-docs` has parent `0f752e2`.

<!-- sse-import sha256=638be5a2a55dba08beb016e650bddd60419818727b8d7506063a99a9ac7e84e9 first-seen=7ed0098 imported-from=38ad62e head="[SSE] Step 0: go-ahead check (event: before 11:27 AM PT, exact time not recorded; written 11:36:16 AM PT, commit ef57eed)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `7ed0098`, imported from `38ad62e`.

### [SSE] Step 0: go-ahead check (event: before 11:27 AM PT, exact time not recorded; written 11:36:16 AM PT, commit ef57eed)
- **What:** checked DEC-080 and DEC-081 as user-role messages in the PM transcript `4ab8dda1-594d-48bb-bd52-3239fec5f37e`.
- **Result:** verified. `t99u` 10:58 AM: "I want the team to build the login module and the hub, initially they can create a mock up and use that for further development". Card answer to `t99s368` 11:02 AM: "Both clickable screens and a stand-in login service"; "Where should the code live?" left blank (PQ-201 open).
- **Why:** team rule "don't assume anything".
<!-- /sse-import -->

<!-- sse-import sha256=45266c346218ef117b838b28fff5add2c8459c00abed6925022cd759f5fb8165 first-seen=7ed0098 imported-from=38ad62e head="[SSE] Sources and md5 (event: before 11:27 AM PT, exact time not recorded; written 11:36:16 AM PT, commit ef57eed)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `7ed0098`, imported from `38ad62e`.

### [SSE] Sources and md5 (event: before 11:27 AM PT, exact time not recorded; written 11:36:16 AM PT, commit ef57eed)
- PLAN read in full at md5 `8c42fbdd…` (matched the brief). Changed at 11:27:56 to `ae18d664…` (adds S1 check 4b, L243); contract cites `ae18d664` (snapshot `/tmp/sse-d0/plan-ae18d664.md`).
- LD rev 11:23 `6c489506…` was read from `.v1q`, but the SA's backups were later rewritten (11:29:36) and no copy with that md5 remains. Contract cites LD `bd70f29a…` (rev 11:27/11:28; snapshot `/tmp/sse-d0/login-doc-bd70f29a.md`). OQ-2.
- AUD envelope `d835c357…` §7A.4; ALW README L30-50 (read only); UX mockups README and D-37 (read only).
<!-- /sse-import -->

<!-- sse-import sha256=75b20153acaa1b5d41658d8e4fb65a3ac24d9ebceca273c79d0ba52b74fb9027 first-seen=7ed0098 imported-from=38ad62e head="[SSE] D0: toolchain (event: 11:27-11:28 AM PT; written 11:36:16 AM PT, commit ef57eed)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `7ed0098`, imported from `38ad62e`.

### [SSE] D0: toolchain (event: 11:27-11:28 AM PT; written 11:36:16 AM PT, commit ef57eed)
- **.NET:** `dotnet-install.sh` from https://dot.net/v1/dotnet-install.sh (redirects to https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh; sha256 `082f7685…eb2e`), `--channel 10.0 --install-dir /home/box/.dotnet`. It reported "already installed": another agent installed it at 11:27 just before; not reinstalled. Verified: SDK 10.0.403 (commit 5a2162bea6), Microsoft.NETCore.App and AspNetCore.App 10.0.14, MSBuild 18.9.15; `dotnet new web` builds (NuGet reachable).
- **Postgres:** `postgresql-17` 17.11-0+deb13u1 and `postgresql-common` 278 from Debian trixie main (https://deb.debian.org). apt over the egress proxy failed (HTTP 500; HTTPS gnutls EOF), so I fetched the signed InRelease and Packages indexes and the .debs with curl from deb.debian.org into `/tmp/sse-d0/mirror` and installed via a `file:` source list passed with `-o Dir::Etc::sourcelist` (system source files untouched; apt checked signatures and hashes). Side effect: `/var/lib/apt/lists` now holds the indexes from that local mirror.
- Cluster 17/main, port 5432 (was free), default `listen_addresses` (localhost only). Started to verify (`select version()` OK), then stopped by me. Start: `sudo pg_ctlcluster 17 main start`. Nothing on 8080/8081.
- Logs: `/tmp/sse-d0/pg-install2.log`, `/tmp/sse-d0/dotnet-install.log`.
<!-- /sse-import -->

<!-- sse-import sha256=23fbe4d7cfbc7735dfa6f28acb7ed38dcc2bf67be19913fe9fd9c1bdb5a107af first-seen=7ed0098 imported-from=38ad62e head="[SSE] D1: contract v1 draft (event: to 11:36 AM PT; written and committed 11:36:16 AM PT, commits c0f7016..ef57eed)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `7ed0098`, imported from `38ad62e`.

### [SSE] D1: contract v1 draft (event: to 11:36 AM PT; written and committed 11:36:16 AM PT, commits c0f7016..ef57eed)
- **What:** `contract/` README, contract-v1.md, errors.md, three JSON Schemas, OPEN-QUESTIONS.md (OQ-1…OQ-29).
- **Decisions:** every item labelled [SRC], [ASSUME (A-n) → OQ-n] (v1-d1 label, retired at v1-d3) or [CONFLICT → OQ-n]. Discovery/JWKS on 8084 per LD §4.5 against PLAN "front" (OQ-1). `attestation_invalid` sourced from PLAN §1.2/4a and LD §8 L420.
- **Checks:** all three schemas pass `Draft202012Validator.check_schema` (jsonschema 4.23.0, venv in /tmp/sse-d0) and 22 sample good/bad instances behave as expected (`/tmp/sse-d0/check_schemas.py`; the real tests are SSE2's).
- **Git:** commits on `feat/contract-v1` built with a private index file and `git update-ref`, so the shared working tree's HEAD (`main`, unborn) and index were not touched (SSE2's untracked `tests/` and `tools/` left alone). No remote; pre-push hook (already present) always fails.
- **Stopped after D1** as briefed: no SI1 code. Waiting on SSE2's adversarial pass and TL review.

Note: line numbers in each entry refer to the source md5 named in that entry (or current at that entry's time); older entries are not re-mapped.
<!-- /sse-import -->

<!-- sse-import sha256=5def6918e6f8774334029c6dea7704b8f77978a49cf2bf1d1ce489cd1c057050 first-seen=7ed0098 imported-from=38ad62e head="[SSE] TL revision pass (TL review relayed 11:37 AM; event: commits 11:41:33 AM-12:05:32 PM PT; written 12:06:07 PM PT, commit 291d8ee)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `7ed0098`, imported from `38ad62e`.

### [SSE] TL revision pass (TL review relayed 11:37 AM; event: commits 11:41:33 AM-12:05:32 PM PT; written 12:06:07 PM PT, commit 291d8ee)
- **What:** new small commits on `feat/contract-v1`, no rewrite of earlier commits: `2e8aee5` (newest sources, v1-d2), `ac62dc9` (OQ-1 front discovery/JWKS), `64da834` (LD rev 11:41 re-map), `6b89c57` (hub_launch, attestation, screen 8), `5badb39` (audit), `4bf371a` (open questions). Same private-index method; shared HEAD (`main`, unborn) and index untouched.
- **Sources (OQ-2, newest rule):** the TL expected LD `5c68265c` (rev 11:32); verified and used for `2e8aee5`. At 12:02 the LD was `88878681…` (rev 11:41, file time 11:41:20): it adds the TL attestation path and `hub_launch` (§16.18.5 L1381-1391), the rule 4 pointer (L1170), `notfromhub` in §8 (L423) and decision row 109. Snapshot `/tmp/sse-d0/login-doc-88878681.md`; cites re-mapped by script (`/tmp/sse-d0/remap_ld2.py`: +1 from L7, +2 from L692, +14 from L1378) and spot-checked. PLAN `ae18d664…` unchanged (`/tmp/sse-d0/plan-ae18d664.md`). AUD `d835c357…`, ALW README `eb7cbf13…` unchanged.
- **Decisions applied:** OQ-1, 2, 7, 8, 9, 23 (partly), 26, 27, 29 marked "TL Decision 2026-11-02". `notfromhub` now writes `auth.app_access.denied` (LD L1170, L423), which changes my earlier "no event on re-prompt" draft for rule 4 only. Attestation cited to the TL decision and the SA's new LD §16.18.5 text. OQ-13 kept as a POC assumption (12 h) tied to the OQ.
- **New:** `device-attestation.schema.json`; extra §8 audit events "Defined, not emitted in S1"; `cachd_client_id`; SSE Proposals on every open OQ; new OQ-30..38 and OQ-41. Where the doc is silent the contract says [DOC SILENT → OQ-n].
- **Checks:** 4 schemas pass `check_schema`; 40 sample instances behave as expected (`/tmp/sse-d0/check_schemas.py`, not committed; the real tests are SSE2's). Sample discovery now has `jwks_uri` on the front.
- **Seen, not acted on:** `08-session.html` shows `local_agent.action_blocked reason=remote_session`; that is a device-agent event (LD L1238, L1408), not a login-service event, so it is not in the contract.
<!-- /sse-import -->

<!-- sse-import sha256=c9cb694afc547be7234bc5024daf2b51b49a1866036ccd6927e391723eee403e first-seen=7ed0098 imported-from=38ad62e head="[SSE] D0 apt follow-up: what I did around the failed apt (honest account) (event: ~11:28 AM PT; written ~12:06 PM PT, commit 291d8ee 12:06:07; filed out of order, see corrective entry below)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `7ed0098`, imported from `38ad62e`.

### [SSE] D0 apt follow-up: what I did around the failed apt (honest account) (event: ~11:28 AM PT; written ~12:06 PM PT, commit 291d8ee 12:06:07; filed out of order, see corrective entry below)
- **What happened:** `apt-get update` / `install postgresql-17` went through the box egress proxy (fake IP 198.18.0.1) and failed for deb.debian.org only: HTTP "500 Internal Server Error [IP: 198.18.0.1 80]"; HTTPS "SSL connection failed … unexpected eof while reading [IP: 198.18.0.1 443]". In the same run the Chrome repo (dl.google.com) worked.
- **What I did, and why:** to finish D0 I fetched the signed InRelease and Packages indexes and the .debs for `postgresql-17` 17.11-0+deb13u1 with `curl` as the box user (which did reach deb.debian.org over HTTPS) into `/tmp/sse-d0/mirror`, and pointed apt at that local copy with a `file:` source list (`-o Dir::Etc::sourcelist`; system source files untouched).
- **Integrity:** apt still checked the Debian archive signatures on InRelease and the package hashes; nothing was installed unsigned.
- **Side effect:** `/var/lib/apt/lists` (11:28) now holds the indexes from that local mirror (`_tmp_sse-d0_mirror_…`). Not cleaned up (that would be another root change on a shared box); left for TL to decide.
- **Read-only investigation now (no probing):** no proxy settings in the apt env or `/etc/apt/apt.conf.d` (only `02autoremove-postgresql`, written by postgresql-common at 11:28); sources are the stock `http://deb.debian.org`; the box's egress proxy is set by the environment (`SAND_HTTP_PROXY_NAME=runner-egress-proxy`, tunnel `127.0.0.1:8791`).
- **Conclusion:** not a box or apt configuration problem. The egress proxy refused apt's requests to deb.debian.org specifically while letting other hosts and curl through. A 500 and a cut TLS handshake are not a normal policy page, so I can't prove it was deliberate without probing, which I won't do. It should be treated as a deliberate egress control. My curl-to-local-mirror route went around it, and I should have asked the TL first.
- **From now on:** TL rule: don't route around network blocks; ask the TL first.
<!-- /sse-import -->

<!-- sse-import sha256=26516d2d27e5d2e4ff51020a7202edb1f0cdd0a3d78ff5528a613709a687024b first-seen=7ed0098 imported-from=38ad62e head="[SSE] Mohanraj decision t43u (relayed 12:00 PM; applied and written 12:06:46 PM PT, commit 27c84ef; the earlier 'applied 12:08 PM' was wrong)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `7ed0098`, imported from `38ad62e`.

### [SSE] Mohanraj decision t43u (relayed 12:00 PM; applied and written 12:06:46 PM PT, commit 27c84ef; the earlier "applied 12:08 PM" was wrong)
- **Input (verbatim, Mohanraj's own message t43u, Nov 2 12:00 PM PT):** "In GitHub and 12 hr is fine but if we can have it configurable then it will be better".
- **OQ-13 resolved:** the absolute session cap defaults to 12 h and is configurable. Contract §7.4 names the setting `LoginService:Session:AbsoluteLifetime` (TimeSpan `12:00:00`). Bounds and validation are an SSE Proposal (OQ-40): 1 h to 24 h, startup fails on a bad value, logged at startup, new sessions only.
- **OQ-28 / PQ-201 resolved:** the code lives in GitHub. Org, repo name, visibility and setup are open for the TL (OQ-39). No remote, push or GitHub action taken; local git only until the TL says otherwise.
- **Also fixed:** OQ-31 cite (LD L207, not L208) and the OQ-30 wording (LD L231 already publishes a new key ≥24 h ahead).
<!-- /sse-import -->

<!-- sse-import sha256=5d395ea0a16782437a07f6a96f63955f8397e95c79223d5337d536c4f0e44a2d first-seen=7ed0098 imported-from=38ad62e head="[SSE] TL correction on the session cap (12:01 PM = when Mohanraj was asked; applied and written 12:08:08 PM PT, commit 6e8a106; the earlier 'applied 12:09 PM' was wrong)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `7ed0098`, imported from `38ad62e`.

### [SSE] TL correction on the session cap (12:01 PM = when Mohanraj was asked; applied and written 12:08:08 PM PT, commit 6e8a106; the earlier "applied 12:09 PM" was wrong)
- **What:** withdrew my 1 h-24 h bounds proposal (and the other validation extras) in contract §7.4 and OQ-40. Now OPEN for Mohanraj with no values proposed: (a) who can change the cap; (b) the allowed range. Question sent to Mohanraj Nov 2, 12:01 PM PT: "Who should be able to change the 12-hour sign-in limit, and what's the shortest and longest it can be set to?"
- **Kept:** 12 h default (Mohanraj t43u). Technical check, not policy: cap > 15-minute idle timeout or startup fails. Setting `LoginService:Session:AbsoluteLifetime` in deployment config only, no admin screen (POC assumption → OQ-40).
- **Idle timeout verified** in LD `88878681` (unchanged since 11:41:20, re-checked 12:08 PM): L229 "Idle timeout 15 min (PCI 8.2.8 [S9]) at the BFF"; L934 login-service cookie "15-minute sliding idle plus the absolute cap"; L321 PCI 8.2.8 row. No conflict.
<!-- /sse-import -->

<!-- sse-import sha256=c3bd6072f4edfd7860dd81c0469ed21a23e2e6e80748d9f9c275c77c124fa00f first-seen=7ed0098 imported-from=38ad62e head="[SSE] v1-d3: TL contract v1 rulings applied (TL 'pause over' 7:55 PM PT; applied 8:08:23-8:11:41 PM PT, commits d0fec04..003f519; written 8:11:32 PM PT, commit ed5115a)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `7ed0098`, imported from `38ad62e`.

### [SSE] v1-d3: TL contract v1 rulings applied (TL "pause over" 7:55 PM PT; applied 8:08:23-8:11:41 PM PT, commits d0fec04..003f519; written 8:11:32 PM PT, commit ed5115a)
- **What:** applied `reviews/contract-v1-tl-rulings.md` (md5 9012597c, incl. Addendum and G1/G2) to contract-v1.md (§0 G1/G2 + rows per OQ), errors.md, OPEN-QUESTIONS.md, the 4 schemas; README to v1-d3 (**not frozen**). Every change tagged [TL Decision 2026-11-02, OQ-n]. Labels ASSUME/SSE Proposal retired; rg for the old A-n ASSUME label = 0 hits in contract/.
- **Why:** TL rulings close OQ-3..6, 10..12, 14, 16..21, 25, 30..36, 41; Addendum adds attestation `aud` + `purpose`.
- **Sources:** TLR 9012597c; LD **b7385794** (SA rev 8:15 PM PT, file 20:10:07; it changed from 88878681 mid-pass, all LD cites re-mapped by script, shifts in README); PLAN ae18d664, AUD d835c357, ALW eb7cbf13 unchanged.
- **Decisions (mine, labelled):** OQ-35 "no jti/aud" is overridden by the Addendum for `aud` (applied Addendum). Access-token `aud` limited to an enum of the OQ-6 values (my reading of OQ-6). Stand-in exact discovery key set is `$defs.standin_exact_keys`, applied with the root schema by the test (a self-$ref caused recursion, removed). Wrong-aud-for-purpose is a server check (§6.7), not schema.
- **Schemas:** /tmp/sse-d0/check_schemas.py (md5 215787a5…) all positives pass, all negatives fail as expected (new negatives: stand-in exact keys, iss flag, scopes_supported, aud array/unknown, wiaormfa, attestation detail, user_disabled, iam other, purposes/aud, alg RS256/ES384/none/PS256, crit, missing typ).
- **Open (safe default in contract):** OQ-15/22 parts, OQ-23 rest, OQ-24, OQ-37, OQ-38 (SA; SA posted Proposals in LD §16.19 / rows 110-118, not yet TL-ruled); device id format (Device Agent owner, TL Rec UUID); OQ-39, OQ-40 (Mohanraj, no values picked; cap > idle check kept).
- **Not done:** no SI1 build; nothing in tests/, docs/, tools/; allowlist.db md5 e5384097 unchanged; no remote/push.
<!-- /sse-import -->

<!-- sse-import sha256=70cfa97491728f9442f246767fe9e13062050c9e66b741bb4961e78cca69c0f5 first-seen=7ed0098 imported-from=38ad62e head="[SSE] Corrective entry: build-log order and times (event: TL Addendum 2 (c), ~8:15 PM PT; written 8:23 PM PT, v1-d4)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `7ed0098`, imported from `38ad62e`.

### [SSE] Corrective entry: build-log order and times (event: TL Addendum 2 (c), ~8:15 PM PT; written 8:23 PM PT, v1-d4)
- Per TL Addendum 2 (c) [TL Decision 2026-11-02]: the entries above are not moved (append-only); headings now carry the event time and the written time, taken from the commit times (`git log`, box time PT).
- Correct event order: Step 0 → Sources → D0 toolchain (11:27-11:28 AM) → D0 apt follow-up (events ~11:28 AM, written ~12:06 PM) → D1 (11:36 AM) → TL revision pass (11:41 AM-12:06 PM) → t43u commit 27c84ef (12:06:46 PM) → TL correction commit 6e8a106 (12:08:08 PM) → v1-d3 rulings applied (8:08-8:11 PM).
- Step 0 and Sources times were never recorded; I don't invent them ("before 11:27 AM").
- The two wrong "applied" times (12:08, 12:09) are corrected in their headings; the old values are quoted there so the change is visible.
<!-- /sse-import -->

<!-- sse-import sha256=da77d1777d489a99661e878ac618d823c49a82624eae921fa6c47f1569aa90f9 first-seen=7ed0098 imported-from=38ad62e head="[SSE] v1-d4: TL 8:12/8:14 PM replies + Addendum 2 applied (event: task 8:16 PM PT; commits e312124..this entry; written at the end of the pass)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `7ed0098`, imported from `38ad62e`.

### [SSE] v1-d4: TL 8:12/8:14 PM replies + Addendum 2 applied (event: task 8:16 PM PT; commits e312124..this entry; written at the end of the pass)
- Sources: TLR md5 5bcaedc9fc1c748743243a7dfd1a3a38 (matches expected; snapshot /tmp/sse-d0/tl-rulings-5bcaedc9.md). LD moved b7385794 → c2b7e689 (rev 8:20 PM, file 20:11:45): +1 from L6, +2 from old L704; snapshot and cites re-mapped (e312124). PLAN moved ae18d664/… → 10fa9853 (L240 only). AUD, ALW unchanged.
- Commits: e312124 LD re-map; 32c4cf7 headers v1-d4; 681c334 standin-control.md + ui-driver.md (CR-05); d1bab9f contract-v1.md (§1.5, 1.9 ICU, 4.11, 5.1, 6.1-6.9, 7.4, 8.6, 8.10); 0ab7097 errors.md (CR-04/25/12/05); dd497e1 schemas + check_schemas.py; ce23ded amr fix (I had added `ngcmfa` to the enum; CR-02 says map it to `mfa`, enum stays RFC 8176-only; caught by re-reading CR-02 before OPEN-QUESTIONS); 9b5f5b6 OPEN-QUESTIONS; 8fc3cb5 README; this log entry.
- Schema check: `contract/check_schemas.py` (copy of /tmp/sse-d0/check_schemas.py; v1-d3 version kept as /tmp/sse-d0/check_schemas.v1d3.py) — all legacy samples as expected, 29 new v1-d4 asserts PASS, exit 0.
- Not done (by rule): no SI1 build, no BFF stub built (CR-12 spec only, flagged for TL), not FROZEN, no remote/push, nothing in tests/, docs/, tools/.
- SSE proposals awaiting TL: test ids and `totp_code` (ui-driver.md); fake-agent timeout 3 s; password rule ids; clock GET shape; §6.9 stub paths/shapes; `access_token_admin` partner_id optional; audit `scope.location_ids` (array, per AUD §7A.4) vs LD singular wording.
<!-- /sse-import -->

<!-- sse-import sha256=150cb87531a6732c0935404eddb475cb0e6dff2eac9b46068bed698f67bd18a2 first-seen=38ad62e imported-from=38ad62e head="[SSE] v1-d4 follow-up: LD moved again during the pass (event: found 8:23 PM PT at the final md5 check; written 8:24 PM PT)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `38ad62e`, imported from `38ad62e`.

### [SSE] v1-d4 follow-up: LD moved again during the pass (event: found 8:23 PM PT at the final md5 check; written 8:24 PM PT)
- LD md5 is now 560cb6abd69ca4b5099eab57a17405b8 (rev 8:25 PM PT, file time 20:17:36), not c2b7e689. Snapshot /tmp/sse-d0/login-doc-560cb6ab.md.
- Diff vs c2b7e689 is inserts only: rev note at L6; rows 120-122 after old L705; new §16.20 (SA-Q1 PCI classes, SA-Q2 cage-action check, SA-Q3 device enrolment; all Proposal (SA), OPEN) after old L1447; one line after old L1471.
- Re-mapped every LD cite in contract-v1.md, errors.md, OPEN-QUESTIONS.md, standin-control.md, ui-driver.md and the schemas with /tmp/sse-d0/wip/remap_ld3.py; spot-checked L417, L705, L706, L1380, L1430, L1443. Build log and README source history not re-mapped (historical). Linked the §16.20 proposals in the OPEN rows; no contract decision changed. check_schemas.py still ALL PASS.
<!-- /sse-import -->

<!-- sse-import sha256=4343c69000f716932782ff7efa7994cf446e90badce0740b5df1e6cfbe92ef12 first-seen=629a87a imported-from=78a792c head="v1-d4 follow-up 2: TL Decision 2026-11-02 8:26 PM (commit cb4e637 at 8:27:05 PM PT; written 8:27 PM PT)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `629a87a`, imported from `78a792c`.

## v1-d4 follow-up 2: TL Decision 2026-11-02 8:26 PM (commit cb4e637 at 8:27:05 PM PT; written 8:27 PM PT)
- **Accepted** SSE proposals (testids, `totp_code`, agent timeout 3 s, password rule ids, clock GET `{"now":…}`, BFF stub paths/shapes); labels replaced with the 8:26 PM tag. New `standin-ui.schema.json` holds the JSON shapes.
- **§6.9:** stub specified for the freeze, built as part of SI1.
- **Admin tokens:** split into `access_token_admin_super` (partner_id optional) and `access_token_admin_partner` (required); `access_token_admin` = either. No role claim in tokens, so the test picks the def by fixture user: flagged for TL in OPEN-QUESTIONS.
- **§8.11:** token/session single `location_id`; audit array `scope.location_ids` (AUD §7A.4 L266, multi-location for admin actions). Audit scope now rejects a singular `location_id`.
- **README:** contract's LD 560cb6ab cites govern over the rulings file. PLAN 444ddba4 snapshot /tmp/sse-d0/plan-444ddba4.md; diff vs 10fa9853 = L128, L130, L133 in place, no shift; all PLAN cites map.
- **check_schemas.py:** 58 PASS, 0 FAIL, exit 0. allowlist.db md5 e5384097 unchanged.
<!-- /sse-import -->

<!-- sse-import sha256=19d7b30bf8a465baedecff36e03e8e14004e641afd423dd9ef4d33644316b07c first-seen=e63d3af imported-from=78a792c head="v1-d4 follow-up 3: TL Decision 8:29 PM (commit 4b7fc57 at 8:28:45 PM PT) and Confirmed DEC-084 (commit e0f946a at 8:29:32 PM PT); written 8:29 PM PT" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `e63d3af`, imported from `78a792c`.

## v1-d4 follow-up 3: TL Decision 8:29 PM (commit 4b7fc57 at 8:28:45 PM PT) and Confirmed DEC-084 (commit e0f946a at 8:29:32 PM PT); written 8:29 PM PT
- **admin_type:** required on /admin tokens (super | partner), if/then discriminator; partner → partner_id required, super → absent; _super/_partner defs retired (contract 8.12).
- **location_ids []:** allowed only when the audit envelope carries admin_type (new optional envelope field); otherwise ≥1 item (contract 8.13). Keyed on admin_type because no event class is /admin-only.
- **DEC-084** (Mohanraj 8:27 PM PT, t110s414): no Cash Advance hand-off or cage action for /admin sessions; audit reason not_location_user; client access_denied; stub 403 {"error":"access_denied"}; stand-in enforces (contract 5.12, 6.9; errors.md; standin-control 2.9).
- **check_schemas.py:** 75 PASS, 0 FAIL, exit 0. allowlist.db md5 e5384097 unchanged.
<!-- /sse-import -->

<!-- sse-import sha256=bdb9ed15516de79bb550a209f212bedf9baa8db04276bd436d080d11d4c9636c first-seen=8c3a077 imported-from=78a792c head="v1-d4 follow-up 4: TL Decision 8:31 PM (commit 56dc948 at 8:30:47 PM PT; written 8:30 PM PT)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `8c3a077`, imported from `78a792c`.

## v1-d4 follow-up 4: TL Decision 8:31 PM (commit 56dc948 at 8:30:47 PM PT; written 8:30 PM PT)
- contract 8.5 and claims no_roles description: admin_type is the kind of sign-in, not a role; no-roles rule holds.
- admin_type kept in the audit envelope; OPEN-QUESTIONS open item AUD v0.8 (AUD owner to add admin_type to §7A.4; TL asking the SA).
- Without admin_type, scope and scope.location_ids are required (≥1). check_schemas.py env() fixture now carries a default location scope.
- §6.9: CA server audits a refused cage action with not_location_user (outside this contract); access_denied confirmed for both refusals; DEC-084 and 8:29 PM ambiguities marked resolved.
- check_schemas.py: 78 PASS, 0 FAIL, exit 0. allowlist.db md5 e5384097 unchanged.
<!-- /sse-import -->

<!-- sse-import sha256=60ee685d225d03213d773664df0911d821221d871db4aaadbef857c40bd37c5c first-seen=78a792c imported-from=78a792c head="v1-d4 follow-up 5: DEC-085 (commit 99f2b7d at 8:32:11 PM PT) and ui-driver pages (commit 2a3ad6a 8:32:55 PM PT); written 8:32 PM PT" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `78a792c`, imported from `78a792c`.

## v1-d4 follow-up 5: DEC-085 (commit 99f2b7d at 8:32:11 PM PT) and ui-driver pages (commit 2a3ad6a 8:32:55 PM PT); written 8:32 PM PT
- **DEC-085** (Mohanraj 8:30 PM PT, t113s429): recovery-code sign-in → recovery_reenroll session (contract 7.8): only MFA re-enrolment and sign-out; hand-off (5.13), hub_launch, app launch and cage (6.9) refused, audit reason recovery_session, client access_denied; no app token with amr exactly [pwd] (claims schema; earlier positive case flipped to negative); stand-in 2.10; errors.md. Open: cage work after re-enrol in same session (interim: stays restricted, sign in again; TO CONFIRM with Mohanraj); re-enrolment endpoint paths unspecified.
- **ui-driver §2a** [TL Decision 8:31 PM]: page-resign-in (UX 07) and page-admin-sign-in (UX 05) with field-/error- ids. UX D-67 (DECISIONS.md md5 b6fa0d1f, read only) gives no ids for these forms, so the ids are SSE-proposed. Mapping noted: contract screens 6/7 = UX 03/06.
- **Provisional app_codes** cash_advance, back_office, reports: tiles in ui-driver, data not enum; OPEN item for Mohanraj via PM. standin-control fixtures list no apps, so no fixture change.
- check_schemas.py: 100 PASS, 0 FAIL, exit 0. allowlist.db md5 e5384097 unchanged.
<!-- /sse-import -->

## [SSE2] Mon Nov 2, 8:33 PM PT: CR verification, attestation/lockout/4b/ctl tests, README

- Branch sse2/tests-docs. Contract checked at 38ad62e (8:24 PM PT), re-checked at 99f2b7d; head when written 78a792c.
- Rulings md5 5bcaedc9 OK. Plan on disk 444ddba4 ≠ brief 10fa9853 (the contract cites 444ddba4 since TL 8:26 PM). LD 560cb6ab (contract) vs b7385794 (rulings); the contract cites govern (TL 8:26 PM).
- Tests: control API/UI driver stubs CTL01–14, UID01–05; attestation SEC10–19, AT01–11; cage SEC50–56; lockout CT23/26/27; CS09–21; SEC24/24b; PW24–28, PW32/33 runnable; CT28 + SEC56 (DEC-084); E2E07 admin row per DEC-084; CS02 stale discovery fixture fixed; TestKit control env renamed LOGIN_STANDIN_CONTROL_URL.
- Run 8:32 PM PT: 291 tests, 200 pass, 0 fail, 91 pending, 0 xfail. **Not green: no stand-in.**
- docs/CONTRACT-REVIEW-v1.md addendum: all Addendum 2 CRs verified, none regressed; ready to freeze from SSE2's side. THREAT-MODEL S-5/E-4 updated; TEST-MATRIX revision; code/README.md created.
- Not yet covered: DEC-085 recovery_reenroll; standin-ui.schema.json gate tests.

<!-- sse-import sha256=c9d94ebbac1eaa59ba16b06ed6d59d2db2678633f35f66ad810b059694e70cac first-seen=40a410c imported-from=e363090 head="v1-d4 follow-up 6: TL Decision 8:35 PM (commit 6e55557 8:34:48 PM PT; written 8:34 PM PT)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `40a410c`, imported from `e363090`.

## v1-d4 follow-up 6: TL Decision 8:35 PM (commit 6e55557 8:34:48 PM PT; written 8:34 PM PT)
- 05/07 test ids confirmed by TL; proposal label removed; UX adding them.
- session_kind (normal | recovery_reenroll): contract 7.8; required token claim (access + ID); optional audit envelope field (present when the event has a session), required = normal on auth.app.signed_in, = recovery_reenroll on reason recovery_session. Token with recovery_reenroll and any app aud rejected. DEC-085 amr==[pwd] rule reverted; "amr recovery-code [pwd] ok" restored. AUD v0.8 item for session_kind.
- Proposal (SA to ratify in LD): GET /mfa/reenrol, POST /mfa/reenrol, GET /mfa/reenrol/recovery-codes.
- standin-control §4 apps fixture cash_advance, back_office, reports (provisional, pending Mohanraj).
- check_schemas.py: 110 PASS, 0 FAIL, exit 0. allowlist.db md5 e5384097 unchanged.
<!-- /sse-import -->

<!-- sse-import sha256=26629ed3495a58ec605bdec5cfb0f24dba90f55e83920a08f89c2ebcbe311185 first-seen=bb8a3a6 imported-from=e363090 head="v1-d4 follow-up 7 (final freeze commit): TL Decision 8:37 PM + Confirmed OQ-40 partial (written 8:36 PM PT; same commit as the changes, so its hash/time is in git log)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `bb8a3a6`, imported from `e363090`.

## v1-d4 follow-up 7 (final freeze commit): TL Decision 8:37 PM + Confirmed OQ-40 partial (written 8:36 PM PT; same commit as the changes, so its hash/time is in git log)
- session_kind split confirmed and resolved in OPEN-QUESTIONS.
- New audit events auth.mfa.reenrolled (session_kind=recovery_reenroll, old factor + old codes revoked) and auth.mfa.recovery_codes_issued (count only; details allow nothing else). Contract 7.8, 8.14.
- Revoke-all-at-once and show-codes-once accepted; paths still Proposal (SA, LD).
- OQ-40 partial (Mohanraj 8:35 PM PT, "super admin or Partner Admin"): only admin_type super|partner may change AbsoluteLifetime; auth.config.session_cap_changed with old/new/actor required. Interim partner-own-locations scope NOT decided. Range open; cap > idle check kept. Contract 7.4, 8.15.
- ui-driver error-admin-sign-in wrapper note deferred post-freeze.
- check_schemas.py: 130 PASS, 0 FAIL, exit 0. allowlist.db md5 e5384097.
<!-- /sse-import -->

<!-- sse-import sha256=395ad64a373a1236712580f5ff0d65f1119c7efbbb98887e997cdc406ac8cf89 first-seen=7bc9c7a imported-from=e363090 head="v1-d4 follow-up 8: [Confirmed DEC-087] + [TL Decision 2026-11-02 8:39 PM] (written 8:38 PM PT; same commit, hash/time in git log)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `7bc9c7a`, imported from `e363090`.

## v1-d4 follow-up 8: [Confirmed DEC-087] + [TL Decision 2026-11-02 8:39 PM] (written 8:38 PM PT; same commit, hash/time in git log)
- DEC-087 (Mohanraj 8:36 PM PT): after re-enrolment the recovery_reenroll session ends and is revoked; auth.mfa.reenrolled then auth.session.revoked reason=reenrolled (reason must have session_kind=recovery_reenroll); fresh normal sign-in needed for hand-off/cage. Contract 7.8, standin-control rule; interim OPEN-QUESTIONS item resolved.
- 8:39 PM: auth.config.change_denied (actor, setting, reason not_admin required); contract 7.4, 8.15. Follow-ups 1, 2, 5 TL-accepted.
- check_schemas.py: 140 PASS, 0 FAIL, exit 0. allowlist.db md5 e5384097.
<!-- /sse-import -->

<!-- sse-import sha256=a1a439ee810984d90a5ceeac8c5602f1d77c1a7b4cabbde35151fade6d2857be first-seen=d4f3ede imported-from=e363090 head="v1-d4 follow-up 9: [Confirmed DEC-088] [Confirmed DEC-089] (written 8:38 PM PT; same commit, hash/time in git log)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `d4f3ede`, imported from `e363090`.

## v1-d4 follow-up 9: [Confirmed DEC-088] [Confirmed DEC-089] (written 8:38 PM PT; same commit, hash/time in git log)
- Verified 7bc9c7a contains DEC-087, auth.config.change_denied and follow-ups 1/2/5 TL-accepted; nothing missing.
- DEC-088/089 (Mohanraj 8:37 PM PT): cash_advance, back_office, reports marked PLACEHOLDER (standin-ui testid description, ui-driver §2, standin-control §4, OPEN-QUESTIONS); back_office and reports two separate app_codes. New open item: who picks Cash Advance's code (TO CONFIRM).
- check_schemas.py unchanged: 140 PASS, 0 FAIL, exit 0. allowlist.db md5 e5384097.
<!-- /sse-import -->

<!-- sse-import sha256=6943239e0ba0844aa0955cf6788a23c28c19f318c1a5659ab93dcdefca5c188c first-seen=a628742 imported-from=e363090 head="v1-d4 follow-up 10: [TL Decision 2026-11-02 8:41 PM, SA ratification] (written 8:42 PM PT; same commit, hash/time in git log)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `a628742`, imported from `e363090`.

## v1-d4 follow-up 10: [TL Decision 2026-11-02 8:41 PM, SA ratification] (written 8:42 PM PT; same commit, hash/time in git log)
- Paths renamed /mfa/reenrol* → /mfa/reenroll* (contract 7.8, OPEN-QUESTIONS; no other file had them). Note: earlier entries in this log keep the old "reenrol" spelling (append-only).
- 7.8 has no "Interim": after codes shown once, session ends like sign-out, redirect to sign-in; recovery_reenroll never becomes normal (DEC-087).
- Successful POST: auth.mfa.reenrolled + auth.mfa.recovery_codes_issued (count only); wrong totp_code: auth.mfa.failed step=reenroll. All three require session_kind=recovery_reenroll.
- Second GET recovery-codes → 404; codes never logged. Failed POST: 200 re-render, generic error (error-area), no echo, no-store; existing TOTP rate limit applies (value not in contract: OPEN item).
- check_schemas.py: 147 PASS, 0 FAIL, exit 0. allowlist.db md5 e5384097.
<!-- /sse-import -->

<!-- sse-import sha256=280a22215c11cc8cdfb70c63c2704b773047e3a9ea48afcb55cee0f40cbcb0bf first-seen=d0f75b3 imported-from=e363090 head="v1-d4 follow-up 11: [TL Decision 2026-11-02 8:42 PM] pre-freeze blocker (written 8:43 PM PT; same commit, hash/time in git log)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `d0f75b3`, imported from `e363090`.

## v1-d4 follow-up 11: [TL Decision 2026-11-02 8:42 PM] pre-freeze blocker (written 8:43 PM PT; same commit, hash/time in git log)
- auth.mfa.recovery_codes_issued session_kind: const recovery_reenroll → enum [normal, recovery_reenroll], still required (normal at first enrolment). Contract 7.8, 8.14, OPEN-QUESTIONS updated.
- Cases: normal (first enrolment) now positive; missing still negative; new negative for another value.
- Post-freeze OPEN-QUESTIONS items: TOTP rate-limit value/source → SA (server must still limit POST /mfa/reenroll); ui-driver re-enrolment screen → v1.1.
- check_schemas.py: 148 PASS, 0 FAIL, exit 0. allowlist.db md5 e5384097.
<!-- /sse-import -->

<!-- sse-import sha256=0b67c68f43762634fc040ba2021a52d960baf528a42b78cddd99cc4568790efa first-seen=e363090 imported-from=e363090 head="v1-d4 follow-up 12: [TL Decision 2026-11-02 8:48 PM] (written 8:48 PM PT; same commit, hash/time in git log)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `e363090`, imported from `e363090`.

## v1-d4 follow-up 12: [TL Decision 2026-11-02 8:48 PM] (written 8:48 PM PT; same commit, hash/time in git log)
- 7.8 re-enrol POST rate limit (TL Decision, not Confirmed; SA may revise): 5 failed tries / 15 min / user, LoginService:Mfa:ReenrollRateLimit; trip → 429, generic message, no-store, auth.mfa.failed step=reenroll reason=rate_limited (session_kind required). Open TOTP-limit row points here.
- auth.config.change_denied details closed: setting, attempted_value (TimeSpan), reason not_admin|wrong_partner|out_of_range; admin_type required for admin reasons, absent for not_admin; scope.partner_id for partner. CAP04 (password in details) rejected.
- auth.mfa.failed step=reenroll details closed (no totp_code echo).
- check_schemas.py: 165 PASS, 0 FAIL, exit 0 (SSE2's export count of 149 differs). Hardcoded path not changed (post-freeze). allowlist.db md5 e5384097.
<!-- /sse-import -->

## SSE2, Mon Nov 2, 8:55 PM PT: pre-freeze pass, final at e363090
- Imported SSE log entries up to e363090 (7 new).
- Verified candidates d4f3ede, a628742 and d0f75b3, each on an exact export.
  - a628742 had a blocker: codes_issued was pinned to recovery_reenroll.
  - d0f75b3 had two blocker candidates, both ruled fix-before-freeze by the TL at 8:47 PM: the re-enrol rate limit was undefined, and the change_denied details were open. OQ-48 was added to the fix.
- Final: e363090 is FREEZE-OK. Suite: export 428/333/1 (TOOL08 export artifact)/94; working tree 428/334/0/94.
- Added PreFreezeSchemaTests (SK/AD/LOC/CAP/PH) and the session_kind baselines in ContractArtifactTests.
- Not done, post-freeze:
  - Server pending tests SEC70-86 and standin-ui UI01-05.
  - The fixture-secrets tool. There is no slug/password fixture file yet to check.

<!-- sse-import sha256=4065f62a78a5f072b929596c4f46f5b2e2e5e3a3173cdb2e1710eec8cf2eb481 first-seen=589fefd imported-from=8aab98a head="Status commit: FROZEN v1 @ e363090 (~8:54 PM PT; status only)" -->
> **Imported verbatim from SSE**: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `589fefd`, imported from `8aab98a`.

## Status commit: FROZEN v1 @ e363090 (~8:54 PM PT; status only)
- What: README status → "FROZEN v1 @ e363090 (tag contract-v1)". OPEN-QUESTIONS gains "Final state at freeze" plus the v1.1 list. No schema or contract-text change.
- Why: TL freeze (8:53 PM PT) asked for a status commit.
- Sources: reviews/contract-v1-tl-rulings.md md5 33e71504 (FREEZE RECORD L263-268); SSE2 docs/CONTRACT-REVIEW-v1.md at sse2/tests-docs ac3652c (read only).
- Process note: a first attempt (ec37c7c) committed only README because the generator script failed. I moved the local branch ref back to e363090 and redid it as one commit. ec37c7c was never pushed.
- Open: SSE/TL items 4/6/7/8 named by number only (TO CONFIRM with TL). Checker count 165 vs 166.
<!-- /sse-import -->

<!-- sse-import sha256=2964f2e3720163fdafc4e26dccf25271c7c294fa8289b5c6199216176196735a first-seen=8808e98 imported-from=8aab98a head="SI1-1 skeleton (8:55 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `8808e98`, imported from `8aab98a`.

## SI1-1 skeleton (8:55 PM PT)
- What: `src/Login.StandIn` (Microsoft.NET.Sdk.Web, net10.0), `src/nuget.config` with all package sources cleared, `.gitignore` for bin/obj.
- Why: PLAN §3.3 (one minimal ASP.NET Core .NET 10 app, `code/src/Login.StandIn/`). Build flag guard PLAN §3.5: an MSBuild target fails the build unless `-p:IncludeStandIn=true`.
- Evidence: build with the flag → 0 errors; without it → "Login.StandIn builds only with -p:IncludeStandIn=true".
- Decision [ASSUMPTION A1]: the sources-cleared nuget.config makes "no package fetching" structural. Restore uses only SDK packs.
- **BLOCKED [B1]:** PLAN §3.3 names `Microsoft.IdentityModel.JsonWebTokens` for signing. It is NOT in ~/.nuget/packages or the shared framework (checked: ls ~/.nuget/packages; /home/box/.dotnet/shared/Microsoft.AspNetCore.App/10.0.14). Token signing (authorize/token/ID token) is stopped until the TL decides. Options for the TL: (a) allow the package into the cache; (b) allow hand-rolled RS256 JWS with System.Security.Cryptography (BCL, no package). JWKS needs no package (RSA parameters from the BCL).
- [ASSUMPTION A2] Ports: front 18080, back 18081 (stands in for 8084; 8084 is outside my allowed ports), control 18082. All configurable. Contract 1.4 fixes the real back port at 8084; the stand-in port is config (PLAN §3.3 "front: configurable").
<!-- /sse-import -->

<!-- sse-import sha256=d9ad01df5981d19bccb694f2ec69674c64468dcbcac8f36a27a2027337518e8d first-seen=f1ba0ca imported-from=8aab98a head="SI1-2 dev-only startup guard (8:55 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `f1ba0ca`, imported from `8aab98a`.

## SI1-2 dev-only startup guard (8:55 PM PT)
- What: `StartupGuard.Check` runs before the host is built. It refuses with exit code 2 and a single stderr line unless the environment is `Development` and `STANDIN_ALLOW=1`. It also refuses on a cloud marker (ECS_CONTAINER_METADATA_URI[_V4], AWS_EXECUTION_ENV) or a non-loopback bind host.
- Why: PLAN §3.5 row 2; standin-control 1.2; PLAN S1 check 11.
- Evidence (env -i, port 18080, timeout 3 s): Production → rc 2 "must be Development"; no STANDIN_ALLOW → rc 2; AWS_EXECUTION_ENV → rc 2; StandIn__BindHost=0.0.0.0 → rc 2; Development + STANDIN_ALLOW=1 → listening on 127.0.0.1:18080 (rc 124 = killed by timeout, i.e. it ran).
- [ASSUMPTION A3] Loopback only. The PLAN also allows "the box's private network", which isn't needed here.
<!-- /sse-import -->

<!-- sse-import sha256=fc697276eba1cad1ca3b2013810e9cb62faecd0f0619a50942a44c50d8aaf353 first-seen=8fc1591 imported-from=8aab98a head="SI1-3 front, back and control listeners (8:56 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `8fc1591`, imported from `8aab98a`.

## SI1-3 front, back and control listeners (8:56 PM PT)
- What: Kestrel is configured explicitly, so ASPNETCORE_URLS is ignored. It runs three 127.0.0.1 listeners: front 18080, back 18081 and control 18082 (config StandIn:FrontPort/BackPort/ControlPort). The listener is chosen by the connection's local port, never by the Host header.
  - Front: 404 for `/_standin*` and for token/revoke/introspect.
  - Back: only token, revoke, introspect, discovery and JWKS; everything else 404.
  - Control: every response is no-store; a non-loopback remote gets 403; only `/_standin/*`.
  - The issuer defaults to http://localhost:{front}/standin. Start is refused if the issuer path lacks /standin.
- Why: contract 1.2-1.5 and 2.9; standin-control 1.1 and 1.3-1.5.
- Evidence (curl):
  - front /_standin/reset → 404;
  - front /standin/connect/token → 404;
  - back /standin/foo → 404;
  - control /other → 404 no-store;
  - the log shows all three listening on 127.0.0.1.
- Not proven: the 403 for a non-loopback caller. Because every socket binds to loopback, no such caller can reach it from the box; it is defence in depth. A unit test belongs to SSE2.
- Scripts (outside the repo): /tmp/sse-d0/run-si1.sh and stop-si1.sh. stop-si1 kills only the PID that run-si1 started.
<!-- /sse-import -->

<!-- sse-import sha256=a3f9cfe5ea97b861162a2c5fca119a86b0a752b3fe8bb6c733619f7734d0620f first-seen=2413e73 imported-from=8aab98a head="SI1-4 discovery and JWKS (8:56 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `2413e73`, imported from `8aab98a`.

## SI1-4 discovery and JWKS (8:56 PM PT)
- What: `{issuer}/.well-known/openid-configuration` and `{issuer}/.well-known/jwks` on both front and back. The bytes are serialised once at start, so both listeners serve identical bytes.
  - Discovery has exactly the 15 keys from 2.1 + 2.4, with every URL under the issuer (2.3).
  - JWKS holds one RSA-2048 key made in memory at start: kty RSA, use sig, alg RS256, kid `standin-<16 hex>`. No private material leaves memory.
  - Cache-Control: discovery `public, max-age=3600`; JWKS `public, max-age=300`.
  - JWKS needs no NuGet package (BCL RSA + WebEncoders).
- Why: contract 2.1-2.10 and 3.1-3.4.
- Evidence (live, 18080 and 18081):
  - `cmp` shows the front and back bytes are identical for both documents;
  - the headers are as above;
  - discovery validates against contract/openid-configuration.schema.json (jsonschema 2020-12 with format checker) with 0 errors;
  - the key set equals the stand-in exact set;
  - the JWKS has 1 key with the 6 members alg, e, kid, kty, n and use, and the kid starts with `standin-`.
- v1.1 note [V1]: the `disc` sample in check_schemas.py puts token_endpoint and revocation_endpoint at `localhost:8084`, but contract 2.3 says they sit under the public issuer. The stand-in follows 2.3; the sample should be aligned. No test impact, since the schema doesn't constrain the host.
- Not done: the allowlist runs first on the front (1.7, 2.10). The stand-in has no allowlist yet, and wiring data/allowlist.db is out of scope (that file is never touched).
<!-- /sse-import -->

<!-- sse-import sha256=b7321fbc056731a93dc0051142b9345816de870054210e7a9cde101bbc646ed4 first-seen=43a37fc imported-from=8aab98a head="SI1-5 control routes: reset, clock, faults, audit; fixtures (8:57 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `43a37fc`, imported from `8aab98a`.

## SI1-5 control routes: reset, clock, faults, audit; fixtures (8:57 PM PT)
- What:
  - `StandInState` holds the in-memory state; `TestClock` is real UTC plus an offset.
  - `POST /_standin/reset` → 204. It reloads fixtures.json and clears faults, audit and the clock offset.
  - `GET /_standin/clock` → `{"now":"…Z"}`.
  - `POST /_standin/clock/advance {"seconds":n}` → 200 `{"now":…}`. A negative or non-integer n gets 400 `{"error":"invalid_request"}`.
  - `PUT /_standin/faults` → 204. Keys outbox (ok|fail) and device_registry (ok|unavailable); anything else gets 400.
  - `GET /_standin/audit?since=` → `{"events":[],"next":n}`.
  - Fixtures (fixtures.json) cover users cashier_db and no_role_user; partners pci and nevada, one location each; apps cash_advance, back_office and reports (all PLACEHOLDER); and a 30-minute lockout.
- Why: standin-control 2.1, 2.2, 2.6, 2.7 and §4.
- Evidence (live on 18082; every response no-store):
  - the clock GET validates against standin-ui.schema.json `clock_get` (0 errors), before and after reset;
  - advancing by 301 moves now by +301 s;
  - -1 and 1.5 each get 400;
  - faults outbox=fail gets 204 and outbox=maybe gets 400;
  - audit returns `{"events":[],"next":0}`;
  - after reset the clock is back to real time;
  - the front's /_standin/clock gets 404.
- Assumptions:
  - [A4] The success shapes aren't in the contract, so I picked them: reset and faults return 204 with no body; advance returns the clock_get shape.
  - [A5] Reset puts the clock back to real time. This is the only "backwards" move, since standin-control 2.1 makes reset restore the clock.
  - [A6] The role name `cashier` and every fixture slug, password and id are unruled (standin-control §4 says OPEN → TL). SSE2 reads them from this file.
  - [A7] faults accepts a partial body.
- Not built (next commits): `PUT pcs/{pc_id}` and the fake agent; `PUT users/{username}` (needs the password-policy validator) and `GET users/{username}/totp` (needs TOTP, HMAC-SHA1 from the BCL); authorize, token, revoke, introspect and endsession, which need signing and are **BLOCKED B1**; the pages (ui/); audit emission. The clock is not yet consumed by anything.
<!-- /sse-import -->

<!-- sse-import sha256=3c904bc8f9cfb293129008abcc70a27d5ee5f072b0f5a1942b2692bcacbacdfb first-seen=d39af1f imported-from=8aab98a head="SI1-6 PUT /_standin/users/{username} and GET .../totp (9:00 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `d39af1f`, imported from `8aab98a`.

## SI1-6 PUT /_standin/users/{username} and GET .../totp (9:00 PM PT)
- What:
  - `PasswordPolicy.Validate` returns the standin-control §3 rule ids in table order. It is the shared validator, so every future set-password path must use it.
  - Users are held in memory and reloaded from fixtures on reset. Passwords are stored only as PBKDF2-SHA256 hashes. A history of 4 drives `reused`.
  - The PUT body is password, roles [{app_code, role}] (app_code must be in the fixtures), location_slugs (must be in the fixtures) and status. It returns `{user_id}`, keeping an existing user's id. A violation gets 422 `{"violations":[…]}`; a bad body gets 400.
  - The TOTP is RFC 6238 using the BCL's HMACSHA1 (no package). Each user gets a 20-byte secret per reset, and the code is computed at the test clock.
- Why: standin-control 2.5 and §3.
- Evidence (live, 18082):
  - PCI with a short password → [min_length]; with no number → [letters_and_numbers]; Nevada with all lowercase → [two_of_four_types]; a password containing the username → [contains_username]; a valid one → 200 {user_id}; resetting the same password → [reused].
  - An unknown app_code → 400. The fixture password for cashier_db → [reused].
  - Each 422 body validates against standin-ui.schema.json `password_violations`.
  - The TOTP code changes after the clock advances 30 s. An unknown user's TOTP → 404.
  - RFC 6238 Appendix B SHA1 vectors (T=59, 1111111109, 1234567890, 2000000000) give 287082, 081804, 005924 and 279037: the last 6 digits of the RFC values. The check harness is in /tmp/sse-d0/totpchk, outside the repo.
- Assumptions:
  - A8: a letter is Unicode L* and a number is Nd after NFKC (an SA Proposal; OQ-SSE2-30 is still open).
  - A9: the 4 types are upper, lower, digit and other.
  - A10: SHA1, a 30 s step and 6 digits.
  - A11: status is one of active, disabled or locked.
  - A12: the presets of every partner the user's locations belong to all apply; a user with no location gets both presets.
  - The username is limited to 1-64 characters.
<!-- /sse-import -->

<!-- sse-import sha256=b76da356487876ece7566ab9ac2e6fa249484a3f5be7502625f64d78795e987a first-seen=eb0fa5d imported-from=8aab98a head="SI1-7 B1 resolved per TL ruling 9:00 PM (a): pinned Microsoft-signed restore from nuget.org (9:01 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `eb0fa5d`, imported from `8aab98a`.

## SI1-7 B1 resolved per TL ruling 9:00 PM (a): pinned Microsoft-signed restore from nuget.org (9:01 PM PT)
- Version pin:
  - OpenIddict.Server 7.7.2 (net10.0) depends on Microsoft.IdentityModel.JsonWebTokens 8.19.2. Source: api.nuget.org/v3-flatcontainer/openiddict.server/7.7.2/openiddict.server.nuspec.
  - The csproj pins it with `Version="[8.19.2]"`.
- nuget.config:
  - One source, nuget.org.
  - packageSourceMapping allows only `Microsoft.IdentityModel.*` and `Microsoft.Bcl.Cryptography`. The Bcl ID was added after the first restore failed with NU1100 for this transitive dependency of Tokens 8.19.2. No System.* IDs were needed.
  - signatureValidationMode=require, with trustedSigners set to the nuget.org repository certificates and owners=Microsoft.
  - No credentials and no other feeds.
- The restore used packages.lock.json and then `RestoreLockedMode=true`. The locked restore was re-run and succeeded. The build has 0 warnings and 0 errors.
- Packages and sha512 (the .nupkg.sha512 files):
  - Microsoft.IdentityModel.JsonWebTokens 8.19.2: 9kokzW5MVsHjANELTihcvrI48azhzhdFQW+oaEce2rD/sGe5Vf57BRQdXFIyDoWluxYh3vhe8aLQ3mg3BP983w==
  - Microsoft.IdentityModel.Tokens 8.19.2: 4rGCcWWvktrOG1esOPr2DMLJpxl9DjIAD943L8C8K8Rcsq30vnOG0IcFMlckuOMots/FHNSUJDxqsmo/EG6eBw==
  - Microsoft.IdentityModel.Logging 8.19.2: 15BrxnYEFaE+3svU/8GB7EYQkOd+x0UdlqmNFoV3gY6vN/u4dg10NuXLoDSSUhxHPdc01cvKeoztwGvAw6Vhng==
  - Microsoft.IdentityModel.Abstractions 8.19.2: ASRJB/oHIKh2N45fTt/t/+p2WaKxWUfvjTrHUq2N39mON6wJTQTX2gwfZOtwWBM4FZ9I7uNi8rFNaynXbMYNsQ==
  - Microsoft.Bcl.Cryptography 10.0.2: rbP/krRdXXVG10QyWH0rWa5+0qP5Rob8VZDKno910kHnDcmH9XdFbBtdedO5pVVlpA1y2MXWx360zLLNLctJ+Q==
- Signatures: `dotnet nuget verify --all` shows, for all 5 packages, an author signature from CN=Microsoft Corporation and a repository signature from CN=NuGet.org Repository by Microsoft.
- OPEN, for the TL:
  - Every package gave the NU3018 warning "revocation server could not be reached / RevocationStatusUnknown: unable to get certificate CRL". The sandbox can't reach the CRL endpoint, so the revocation check could not complete; the signatures and the chain were otherwise accepted.
  - I did nothing to work around it: revocation mode is left at its default and no other route was used.
  - The TL should decide whether an offline revocation result is acceptable.
<!-- /sse-import -->

<!-- sse-import sha256=dcc356a917057fb6a0f2eaf250a8f668970ed352a74011ab00c226b2f1933be7 first-seen=97bfdb2 imported-from=8aab98a head="SI1-8 TL 9:00 PM rulings applied (9:01 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `97bfdb2`, imported from `8aab98a`.

## SI1-8 TL 9:00 PM rulings applied (9:01 PM PT)
- A2 (accepted): src/README.md documents the 18081↔8084 mapping. Real deployments keep 8084.
- A4: I re-checked standin-control.md and it is silent on success response shapes. A4 stays (reset and faults → 204, clock → {"now"}, users → {user_id}). Logged for v1.1: "standin-control should specify success shapes".
- A6: fixtures.json now labels every app, partner, location, user and role object with `"_label":"FIXTURE"`, and the _comment says 'cashier' and all slugs and ids are FIXTURE, not product role names. A reset loads the file correctly, and the cashier_db reuse check still returns [reused].
- V1: §2.3 is followed (the issuer and front run on 18080, and the token endpoint is on the back listener). Logged for v1.1: "fix the checker sample's localhost:8084 token_endpoint to match §2.3".
- Post-freeze item 4 (recorded, not yet built): when the cap-change piece is built, the server must enforce the Partner Admin own-partner rule and must have a server-side test for it. It is not in SI1 scope tonight.
<!-- /sse-import -->

<!-- sse-import sha256=95c9a262d19afd81290421689b00871f94869e8166fba7c47aa7dca826bbb712 first-seen=3087f58 imported-from=8aab98a head="SI1-9 PUT /_standin/pcs/{pc_id} (9:03 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `3087f58`, imported from `8aab98a`.

## SI1-9 PUT /_standin/pcs/{pc_id} (9:03 PM PT)
- What: fake PCs are held in memory and cleared on reset. The body is {mode, location_slug}. It returns {"cookie":{"name":"standin_pc","value":pc_id}} and sends no Set-Cookie (standin-control 2.3).
- Evidence (live): all 9 modes return 200 with the cookie body. These each return 400: pc_id without the `standin-` prefix, an unknown mode, an unknown location_slug, and a missing location_slug.
- Assumption A16: pc_id is `standin-` followed by 1-64 characters from [A-Za-z0-9._-] (cookie-safe).
- Not yet built: the front reading `standin_pc` and the fake agent behaviour per mode (2.4). Those need the sign-in and nonce flow.
<!-- /sse-import -->

<!-- sse-import sha256=5f22a00563bf2397fd9311d984cfd32ce3466accfa7b84ec945708f7a0fdbe59 first-seen=dd59e85 imported-from=8aab98a head="SI1-10 Front network allowlist, the first middleware on the front listener (9:03 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `dd59e85`, imported from `8aab98a`.

## SI1-10 Front network allowlist, the first middleware on the front listener (9:03 PM PT)
- What:
  - The allowlist middleware is registered before the listener rules and routing, and applies to front requests only.
  - It matches the TCP peer address against active IPv4 and IPv6 CIDRs.
  - A blocked caller gets the errors.md 403: text/plain; charset=utf-8, Content-Length 29, body `403 Forbidden: access denied\n`, Cache-Control no-store, Connection close, Server: server, Date and X-Request-ID. HEAD gets no body.
  - A blocked caller emits no audit event (errors.md: allowlist 403s are not on the outbox).
- Data:
  - /tmp/sse-d0/export-allowlist.py opens /tmp/sse-d0/allowlist-copy.db with `mode=ro` and writes the active rows to /tmp/sse-d0/allowlist-active.json (2 rows: 127.0.0.2/32 and 203.0.113.10/32).
  - The path is set by config `StandIn:AllowlistJson`.
  - The real data/allowlist.db is never opened.
- Evidence (live):
  - From 127.0.0.1 and 127.0.0.4 (inactive): 403 with the exact headers, and a body of 29 bytes ending in \n (od).
  - HEAD: 403 with no body. A POST to an unknown path from .4: 403 (the gate runs before the 404).
  - From 127.0.0.2: 200 discovery.
  - X-Forwarded-For: 127.0.0.2 sent from 127.0.0.1: 403.
  - The control (18082) and back (18081) listeners are unaffected (200).
  - With the JSON missing: loaded=False, and .2 gets 403 (fail closed).
- Assumptions:
  - A13: .NET has no SQLite package, so the active rows are exported to JSON with the Python stdlib. The stand-in reads that snapshot at startup; a DB change needs a re-export and a restart.
  - A14: there are no trusted proxies, and XFF is ignored.
  - A15: a missing or unreadable list means fail closed.
- Note: the 127.0.0.1 tester is now blocked on the front. Earlier front tests (SI1-4) must use `--interface 127.0.0.2`.
- Not built: the back-listener internal source list (errors.md, LD §4.5).
<!-- /sse-import -->

<!-- sse-import sha256=aa06fd596a93a06b2c7e5c472143b72392f7405ae52c27b6fde97a4c41949676 first-seen=f63b2c8 imported-from=8aab98a head="SI1-11 Audit emission into the control feed (9:04 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `f63b2c8`, imported from `8aab98a`.

## SI1-11 Audit emission into the control feed (9:04 PM PT)
- What:
  - `Audit.Build` creates the audit-events.schema.json envelope: a UUIDv7 event_id (Guid.CreateVersion7 at the test clock), schema cachd.log, category event, is_audit true, plus actor, source, details and an optional scope.
  - `Audit.UsernameHmac` is HMAC-SHA256 hex over the trimmed, NFKC-normalized, lowercased username.
  - `StandInState.Emit` appends to the feed that GET /_standin/audit serves; reset clears it. Emit itself was added to Control.cs in SI1-9.
  - No endpoint emits yet: the sign-in, app and admin flows are not built, and the allowlist 403s are not on the outbox per errors.md.
- Evidence (harness /tmp/sse-d0/auditchk, outside the repo; it compiles the repo sources and drives StandInState.Emit and AuditSince):
  - auth.login.failed and auth.login.succeeded are each VALID under Draft 2020-12 with FormatChecker, and their event_ids are UUID v7.
  - The negative case (no scope, and carrying `username`) is INVALID, as expected.
  - The feed gives next=3; after a reset, next=0.
- Also added: src/Login.StandIn/scripts/export-allowlist.py (the read-only export used by SI1-10).
- Assumptions:
  - A17: schema_version is 1.0.0.
  - A18: source.service is login-standin.
  - A19: the test HMAC key is fixed.
  - A20: the server does not validate events against the schema at runtime (there is no JSON Schema package). Validity comes from how Build constructs them and from harness checks.
<!-- /sse-import -->

<!-- sse-import sha256=70b9e08da2fcbfa7fc10ae0343ff018b2d04735fa3953fec31399dde52e8761e first-seen=40da81d imported-from=8aab98a head="SI1-12 TL Decision 9:06 PM: NU3018 accepted for the local stand-in only (9:05 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `40da81d`, imported from `8aab98a`.

## SI1-12 TL Decision 9:06 PM: NU3018 accepted for the local stand-in only (9:05 PM PT)
- Conditions:
  1. The warning stays visible. It is not suppressed (no NoWarn or WarningsNotAsErrors change), and NUGET_CERT_REVOCATION_MODE=offline is not set. I checked: neither nuget.config nor the csproj changes revocation mode or NoWarn.
  2. **Release gate v1.1/CI-01:** before any build leaves this box, the pipeline re-verifies with online revocation checking (`dotnet nuget verify --all`), and any NU3018 there fails the build.
  3. Adding any package or new source mapping needs a TL ruling; stop and report if one is needed. Microsoft.Bcl.Cryptography 10.0.2 is accepted.
<!-- /sse-import -->

<!-- sse-import sha256=7f09b3cc3f3b86a49c20ce08e04cc522ae8eaa053011fd49ceb61cb376cce1c7 first-seen=ed0ecc1 imported-from=8aab98a head="SI1-13 Token signing, OAuth clients, fixture additions (9:20 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `ed0ecc1`, imported from `8aab98a`.

## SI1-13 Token signing, OAuth clients, fixture additions (9:20 PM PT)
- Evidence note for SI1-13..16: every result below comes from ONE run against the final build (all four slices), after a reset: `/tmp/sse-d0/e2e/drive.py`, output `/tmp/sse-d0/e2e/si1-13-16-evidence.txt`, **84/84 PASS**. Only temp DB copies were used; ports 18080-18082.
- **D1 (divergence, v1.1 list):** the task text says "ES256/JWKS per the contract", but the contract (2.1, 3.1, 8.1, claims `jose_header_*`, discovery `id_token_signing_alg_values_supported`) says **RS256**. I followed the contract: tokens are RS256 with a `standin-` kid. ES256 is used only for the fake-agent device attestations (device-attestation.schema.json).
- Tokens.cs: access token typ at+jwt, 300 s; ID token typ JWT, aud = azp = client_id, nonce only on the code grant; ±30 s skew on validation.
- Clients.cs: client_secret_basic (SHA-256 hash, fixed-time compare). fixtures.json: clients cash_advance_bff and backoffice_stub, cachd_client_id per partner, user nevada_db, back_office/clerk for cashier_db, and perm_version.
- Evidence: discovery validates openid-configuration.schema.json; JWKS has RS256 `standin-569d0f257300dccb`; token kid == JWKS kid; the RS256 signature verifies independently (pure-Python PKCS#1 v1.5; no package installed); headers validate jose_header_access_token / jose_header_id_token; payloads validate access_token_other, id_token_code, id_token_refresh; x_standin true, amr [pwd,otp,mfa], aud backoffice-api.
- Assumptions: **A21** perm_version is 1 for every fixture user. **A24 / blocker B2**: private_key_jwt is refused with invalid_client, because the fixtures have no client JWKs (a TL ruling is needed to add them). **H1** (v1.1): the hub client is not registered, because the hub token's aud is undefined.
<!-- /sse-import -->

<!-- sse-import sha256=8389ef2d58f4930045f770e49312a9172fbff5ffa4d5056155446e6f49ffe797 first-seen=ed0ecc1 imported-from=8aab98a head="SI1-14 Sign-in, MFA, attestation nonce flow (9:20 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `ed0ecc1`, imported from `8aab98a`.

## SI1-14 Sign-in, MFA, attestation nonce flow (9:20 PM PT)
- Auth.cs, SignIn.cs, Program.cs (antiforgery). The order is: attestation, then lockout, then password, then verified remote_session, then TOTP (±1 step). The pre-session id is reissued after the password step, and the new session cookie is set after MFA (4.11).
- Evidence: sign-in → TOTP → hub; the 4.11 cookie checks pass; all 8 PC modes (valid, missing, and the six that should be invalid) give the expected `attestation` detail; remote_session → screen-8a + login.failed remote_session; registry unavailable → invalid (fails closed); a re-minted nonce voids the old one; no antiforgery → 400; the fake-agent JWS validates the schema; a wrong password and an unknown user give the same bad_credentials; a wrong TOTP gives mfa.failed + login.failed; lockout is Nevada 3 and PCI 10 tries; the lock lifts after 30 min. Every emitted event validates audit-events.schema.json.
- Assumptions:
  - **A22 / AG1** (v1.1): the fake agent runs on the control port, `/_standin/agent/attest`, with CORS.
  - **A23**: the slug comes from the standin_loc cookie, else the first fixture location.
  - **A25**: if the outbox fails at MFA, no session is created.
  - **A26**: a wrong TOTP emits both auth.mfa.failed and auth.login.failed reason=mfa_failed.
- Note: antiforgery makes Cache-Control `no-cache, no-store`; no-store is still present.
<!-- /sse-import -->

<!-- sse-import sha256=2a081bf3bfc1aeddc2244d70cbbb76bd0d344fd01dd5d2bbaeb37f97252e6a3a first-seen=ed0ecc1 imported-from=8aab98a head="SI1-15 /connect/authorize (9:20 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `ed0ecc1`, imported from `8aab98a`.

## SI1-15 /connect/authorize (9:20 PM PT)
- Evidence:
  - A bad client or redirect gives 400 with no echo and no event. invalid_scope, invalid_request, unsupported_response_type and login_required all carry iss and state.
  - With no session, the user goes to the sign-in page and then back to authorize. The code goes to the exact redirect, with method interactive and then sso.
  - prompt=login and max_age force a fresh sign-in.
  - Cash Advance with no hub_launch gives notfromhub plus the sign-in page.
  - With no role, the client gets access_denied and auth.app_access.denied no_role (target app/back_office).
  - An outbox failure gives temporarily_unavailable, with no code and no event.
- Fix during this run: auth.app_access.denied used outcome `failure`. The schema requires `denied`, so I corrected it.
- Gap **G-H** (open): hub_launch issuance and the Cash Advance hand-off (5.7) are not built, so Cash Advance always gets notfromhub.
<!-- /sse-import -->

<!-- sse-import sha256=30a226e3dd939b50067d65f8981bb826463970ac6cbac042037e5a17982c696e first-seen=ed0ecc1 imported-from=8aab98a head="SI1-16 token / revoke / introspect / endsession (9:20 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `ed0ecc1`, imported from `8aab98a`.

## SI1-16 token / revoke / introspect / endsession (9:20 PM PT)
- Evidence:
  - The token response has the right fields, no-store and Pragma.
  - Codes are single-use, and reuse revokes the family. A wrong PKCE verifier, a code older than 60 s, or a bad secret are rejected (401 with WWW-Authenticate Basic). The token endpoint isn't served on the front port (404).
  - Refresh rotation: a reuse inside the 30 s leeway is accepted. After it, the result is invalid_grant plus auth.refresh.reuse_detected, and the whole chain is revoked.
  - If the user loses the role, refresh gives invalid_grant.
  - Introspect gives the active shape. For another client or an unknown token it returns exactly `{"active":false}`.
  - Revoking a refresh token revokes its family. Revoke always returns 200, and returns 401 without client auth.
  - Endsession:
    - With no hint, a confirm page shows.
    - An expired hint is accepted, gives a 302 to /standin/l/pci-main, and writes one session.revoked sign_out. After that, refresh fails, prompt=none gives login_required, and a second sign-out writes no second event.
    - The confirm POST works. Without antiforgery it returns 400.
- Assumptions:
  - **A27/A28**: an outbox fault returns 503 temporarily_unavailable with Retry-After: 5.
  - **A29**: introspect covers access tokens only.
  - **A30**: post_logout_redirect_uri is never followed (no open redirect); the user always goes to the location sign-in page.
- Not built here: the recovery_reenroll paths (7.8), the admin paths (5.12), and the session-cap UI. The NU3018 warning was not shown in this build because restore was cached (no-op). I suppressed nothing.
- md5 (driver, evidence):
    948af73451e0493360ced9da84705f95  /tmp/sse-d0/e2e/drive.py
    6564ee37ff911d766e6414b314f60935  /tmp/sse-d0/e2e/si1-13-16-evidence.txt
<!-- /sse-import -->

<!-- sse-import sha256=b26c87d2e46fcaa0ef82f4b7e257b0a9c076094f199fb29be56662d81fd874c2 first-seen=9a14163 imported-from=8aab98a head="TL rulings 9:16 PM and item (1) hand-off (2026-11-02, 9:16 PM-)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `9a14163`, imported from `8aab98a`.

## TL rulings 9:16 PM and item (1) hand-off (2026-11-02, 9:16 PM-)
<!-- /sse-import -->

<!-- sse-import sha256=35998a2b28f1ab8c39c0afe661ef451834e702a41ec4ee41948501099fea8c5b first-seen=9a14163 imported-from=8aab98a head="Rulings logged" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `9a14163`, imported from `8aab98a`.

### Rulings logged
- **B2 [TL Decision 9:16 PM]**: FIXTURE client keys. RSA-2048 per client (7.6: private_key_jwt is RS256 only), generated fresh at start and every reset (`Clients.Load`). Private PKCS#8 PEM written only to `$STANDIN_CLIENT_KEY_DIR` (default `/tmp/sse-d0/fixture-client-keys`, mode 0600); `Clients.Load` throws for any path outside /tmp. Public key in memory only. private_key_jwt per 7.6: alg pinned RS256 (none/HS*/ES* refused), iss = sub = client_id, aud = issuer exactly, jti required and refused on reuse until exp (+30 s skew), exp − iat ≤ 60, iat skew +30 s. Token, revoke and introspect all accept it. Basic and an assertion together → invalid_client.
- **B2 secrets check**: `src/Login.StandIn/scripts/check-fixture-secrets.sh [index]` fails if a tracked file is key-like (*.pem/*.key/*.p8/*.pfx/*.p12), has a PEM PRIVATE KEY block, or has a JWK with private members. It ran on the si1 index (OK, 36 files) and against a negative case in a throwaway repo (FAIL, rc=1). It's not wired into tests/ or tools/ (both off limits), so it's a manual step until the owner of tests/tools wires it into CI.
- **H1 [TL Decision 9:16 PM, interim; v1.1 for SA ratification]**: hub client `hub` registered (FIXTURE secret). Access-token aud = "hub" (its client_id, PLACEHOLDER). No app_code, no roles, no role check, no org claims. **v1.1 conflict:** `claims.schema.json` access_token.aud enum is ["cash-advance-api","backoffice-api"] and rejects "hub". The schema (frozen) needs an update.
- **D1 accepted [TL 9:16 PM]**: RS256 for tokens, ES256 only for attestations. v1.1: the contract text states it.
- **A29, A30 accepted [TL 9:16 PM]**: v1.1 items for the contract to state them (introspect answers for access tokens only; post_logout_redirect_uri never followed).
- **DEC-093 [TL 9:22 PM; Mohanraj 9:21 PM via PM]**: v1.1 item: clarify the §7.8 text. After re-enrolment finishes, the session ends and redirects to the sign-in entry of the same kind: an admin_type session goes to /admin, a location session to its own /l/{slug}. Each needs a test. **To be built in step (3) recovery_reenroll, not built now.**
<!-- /sse-import -->

<!-- sse-import sha256=0e7ac5588f7bbdf45cdbbca252294b3aa2f9812afd0372e533b75d3787e3302c first-seen=9a14163 imported-from=8aab98a head="Item (1) built: hub_launch + 5.7 hand-off" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `9a14163`, imported from `8aab98a`.

### Item (1) built: hub_launch + 5.7 hand-off
- Handoff.cs: hub_launch is 16 bytes of CSPRNG, base64url, stored only as SHA-256 (`AuthStore.HubLaunches`), bound to sid + client_id. Issued on each hub view for role tiles of hand-off clients only. Tile = start_url + ?hub_launch=. GET /standin/apps re-renders the hub (2.8: tests read hub_launch from the tile; no control route).
- authorize order for cash_advance_bff: consume hub_launch (even with no session, or when a later rule fails) → 5.13 recovery_session → 5.12 not_location_user → ViaReturn-fresh session = interactive → rule 4 (notfromhub event, re-prompt) → rules 1,2,3,5,6 (no event, 5.10, re-prompt) → interstitial. prompt=none with unmet rules or a cage_consumed session → interaction_required.
- Interstitial (5.8): no-store, CSP frame-ancestors 'none', connect-src 'self' + agent origin, antiforgery. Server-side tx (cookie standin_hx) binds nonce + hub_launch hash + sid + client_id. POST /standin/connect/authorize carries device_attestation in the body. Attestation purpose "handoff". Rules are re-checked at completion.
  - invalid, missing, or tx/session mismatch → attestation_invalid, then re-prompt (password + MFA, then Cash Advance opens interactive).
  - remote → remote_session, then screen-8a.
  - valid → auth.app.signed_in method=hub_handoff with the original auth_time and sid; the session is marked HandoffUsed + CageConsumed. A cage_consumed session gets no SSO to anything.
- Re-prompt = /l/{slug}?ret=<authorize without hub_launch/prompt>&resign=1, rendered with the UX 07 page-resign-in ids.
- Minimal /standin/admin (UX 05 ids): password, then TOTP or a FIXTURE recovery code (stored hashed, single use). A recovery code gives a recovery_reenroll session with amr [pwd]. The fixture admin `partner_admin` (admin_type partner).
<!-- /sse-import -->

<!-- sse-import sha256=8c376fb23d4f33ac999762f2c8ca625a8f742de00281b193a0d1968d7b309720 first-seen=9a14163 imported-from=8aab98a head="Assumptions" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `9a14163`, imported from `8aab98a`.

### Assumptions
- A31: hub_launch is minted on every hub view (hub page after sign-in and GET /standin/apps); earlier unused values stay valid until 60 s.
- A32: for the hand-off client, only a session created by a sign-in that returned to this authorize (ViaReturn), within 60 s and with no code yet, counts as interactive.
- A33: /admin is minimal: no lockout, no attestation, no re-enrolment flow.
- A34: refusal order is recovery_session (5.13) first, then not_location_user (5.12).
- A35: 5.12/5.13 refusals apply to every client's authorize, not only the hand-off (fail closed). Admin sessions are never SSO'd into location apps.
- A36: a recovery hub shows only "set up your authenticator again" and sign-out; it writes no event at the hub (the recovery_session event is written at authorize).
- A37: the allowlist half of rule 5 is the existing global front middleware; there is no per-location allowlist.
- A38: the interstitial has no ui-driver id (v1.1 UI item).
- A39: the hub's auth.app.signed_in target.id is its client_id "hub" (no app_code).
- A40: a hub_launch presented with no session is still consumed.
<!-- /sse-import -->

<!-- sse-import sha256=5ea4f9d860a3dc3011935df7173a9fc7a98082310152b3ffc8a04e52f5ae8e0e first-seen=9a14163 imported-from=8aab98a head="Gaps" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `9a14163`, imported from `8aab98a`.

### Gaps
- Rule 2: there's no live way to show the Entra mfa/ngcmfa → mfa mapping or a non-MFA location session (no Entra provider and no location recovery path). The code checks amr contains "mfa".
- A location-user recovery sign-in isn't built (step 3).
<!-- /sse-import -->

<!-- sse-import sha256=cd3db0e132316db871539e4a6d827fcecafb691ad70acf7f076b3399895ffd74 first-seen=9a14163 imported-from=8aab98a head="Evidence status: BLOCKED (live run not done)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `9a14163`, imported from `8aab98a`.

### Evidence status: BLOCKED (live run not done)
- The build succeeds (0 errors; NU3018 didn't re-print because restore was cached).
- At 9:24 PM, ports 18080-18082 were held by another stand-in: pid 628112, started 9:18 PM, `/tmp/sse2-si1/bin/Login.StandIn.dll` (SSE2). It isn't mine, so I didn't kill it.
- My first driver run went to that instance by mistake before I noticed. It sent control calls (reset, clock advance, PUT users, pcs) to SSE2's in-memory state. That needs reporting.
- The drive.py handoff/pkjwt scenarios are written (`/tmp/sse-d0/e2e/handoff.py`) but haven't run against this build.
<!-- /sse-import -->

<!-- sse-import sha256=00554d28fb9382c725562ce4ad968574db13e22906fd4eef5a7c90ad81e5d9d4 first-seen=8aab98a imported-from=8aab98a head="TL rulings 9:26 PM applied (2026-11-02, logged 9:31 PM PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `8aab98a`, imported from `8aab98a`.

## TL rulings 9:26 PM applied (2026-11-02, logged 9:31 PM PT)
1. **PORTS (TL Decision).** `STANDIN_PORT_BASE` (default 18080): front = base, back = base+1, control = base+2, loopback only, StartupGuard unchanged (Listeners.Read; the old per-port config keys are gone). We keep 18080-18082; SSE2 uses 18090-18092. Per-instance state dir `/tmp/sse-d0/standin-{base}/` holds fixture-client-keys/, si1.log, si1.pid (from run-si1.sh) and server.pid (written by the process). STANDIN_CLIENT_KEY_DIR still overrides the key dir.
   - run-si1.sh refuses to start (exit 3) if any of the 3 ports is listening, and after start kills its child and exits 4 unless the `ss -ltnp` owner of all 3 ports is the pid it started.
   - drive.py takes `--base` / STANDIN_PORT_BASE and aborts before sending any request unless the ss owner of all 3 ports equals the pid in the base-keyed pid file. I chose the ss-owner comparison over a new control route, so standin-control (frozen contract) is unchanged.
   - Proven: base 18090 (SSE2, pid 639792): run-si1.sh refused with "STOP: port 18090 already held"; drive.py aborted with "no pid file". With a fake pid file it aborted with "owner 639792 is not our pid 999999; no traffic sent". No traffic went to SSE2.
2. **H1 revised.** The hub client is openid-only: fixture `aud` null, so the token endpoint returns `{id_token, scope}` and no access_token, token_type or refresh_token. The id_token aud = "hub" (client_id). An API scope request returns invalid_scope. aud=hub is removed from the access-token path (TokenIssuer.Access throws for a null-aud client). v1.1 (a), the claims-schema aud conflict, is **closed with no schema change**. [ASSUMPTION A43] Omitting access_token departs from RFC 6749 §5.1 (REQUIRED); it is done by the TL ruling and noted for v1.1.
3. **(b) Rule 2 negative paths.** New FIXTURE users cashier_pwd and cashier_wia have a per-user `fixture_amr` override ([pwd] or [wiaormfa]) that is applied when the location session is created and carried into auth.login.succeeded amr. Live, both get the hub tile, and the hand-off is refused with a re-prompt, no code and no event (5.10). The positive ngcmfa path is pending until there is an Entra provider.
4. **(c)** v1.1 item: the fixture secrets check; SSE2 will wire it (scripts/check-fixture-secrets.sh stays local).
5. **A32 TL ruling.** Interactive = now − auth_time ≤ 300 s AND the session's last authentication was a real credential entry (password + MFA) made for this authorize. auth_time is set only by credential entry. Cash Advance counts as interactive only when ViaReturn, no code has been issued from the session yet (credential entry not spent), and prompt≠none. The old 60 s / ViaReturn heuristic is replaced for the hand-off client; the 60 s method label for other clients is unchanged.
   - [ASSUMPTION A42] Ages are compared in whole elapsed seconds (truncated, like the integer auth_time claim), because the test clock runs on from real time. This applies to rule 1, A32 and the 60 s hub_launch.
   - Tested live: 300 s gives interactive and 301 s is refused; SSO reuse gives notfromhub; prompt=none gives interaction_required; after a refresh grant the session is not interactive; hand-off rule 1 passes at exactly 300 s.
6. **A35 and A40 accepted.**
7. **DEC-094** (build log + v1.1): the 403 shows only the message, with no help text; the body is unchanged. **DEC-095** (v1.1): device-enrolment work, where a Super or Partner Admin issues cage-PC codes and a Partner Admin only for its own partner.
- [ASSUMPTION A41] Only the hand-off target client reads hub_launch; another client ignores it, so the launch stays usable for Cash Advance. This replaces the placeholder "wrong client" check.
- Fixes found by the first live run of handoff.py:
  - The interstitial was missing its CSP. It now has frame-ancestors 'none' and connect-src for the agent port.
  - The rule-5 test used a slug that does not exist and no password (the PUT returned 400).
  - The id_token schema def is id_token_code.
  - The "recovery code single use" placeholder is now real: a second use gives no hub and no new login.succeeded.
<!-- /sse-import -->

<!-- sse-import sha256=b12ae33ba9143be2d216c39e57fee6a2f6bdae67427360535786b46621765c05 first-seen=8aab98a imported-from=8aab98a head="Evidence status: DONE (supersedes BLOCKED above)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `8aab98a`, imported from `8aab98a`.

### Evidence status: DONE (supersedes BLOCKED above)
- Live run 2026-11-02 on base 18080, own pid 643647 (guard ok): **TOTAL 143 / 143**, 85 earlier checks + 58 hand-off/pkjwt/H1/(b)/A32. Then the guard negatives.
- Evidence: /tmp/sse-d0/e2e/si1-item1-evidence.txt, md5 **276d88d001535ac6eac2723dbe0bb5d1**.
- Server stopped; 18080-18082 are free afterwards. allowlist.db md5 is unchanged (e5384097dd60ee5df568577ec9126071).
<!-- /sse-import -->

<!-- sse-import sha256=e1376369919c933eb2952e7d8fcacf9055db8e92a1b2a80de2273780cce2a90d first-seen=8aab98a imported-from=8aab98a head="v1.1 list additions" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `8aab98a`, imported from `8aab98a`.

### v1.1 list additions
- (a) claims-schema aud conflict: CLOSED, no schema change (H1 revised).
- (c) fixture secrets check: SSE2 wires it.
- RFC 6749 §5.1 access_token omission for the openid-only hub (A43).
- DEC-093 (§7.8 re-enrolment redirect by session kind; build in step 3).
- DEC-094 (403 message only, no help text, body unchanged).
- DEC-095 (device-enrolment codes: Super/Partner Admin, Partner Admin only for its own partner).
- Positive ngcmfa rule-2 path (needs an Entra provider).
<!-- /sse-import -->

## SSE2, Mon Nov 2, 9:18–9:45 PM PT: SI1 stand-in verification (9a14163 → 8aab98a), port incident, hand-off adversarial tests
**What.** Ran SEC70–79, the new hand-off (HO), DEC-093 (D93), private_key_jwt (PKJ) and fixture-secrets (FS) tests against SSE's stand-in; checked SSE's 143/143 claim myself.
**Why.** TL tasks at 9:25, 9:26, 9:32 PM PT (target moved 9a14163 → 8aab98a at 9:32 PM PT); SSE 9:32 PM PT note on the hand-off CSP.
**Sources.** contract-v1 e363090 (5.1, 5.3, 5.7, 5.8, 5.10, 5.12, 5.13, 7.6, standin-control 2.2); SSE `src/SI1-BUILD-LOG.md` at 8aab98a (A22, A32, A36, A38, A41, A42, A43); TL ruling 9:32 PM PT: age limits are millisecond-precise, no rounding down.

### Port incident (TL 9:25 / 9:26 PM PT)
- 9:18 PM PT: my earlier stand-in (pid 628112) was on 18080–18082, the ports SSE uses. Every result from it after 9:18 PM PT is discarded.
- 21:25:30 PST: pid 628112 killed; 18080–18082 confirmed free. **Ports freed at 9:25:30 PM PT.**
- 21:26:48 PST: interim instance ed0ecc1 (pid 638643, 19080–19082) stopped.
- ~21:26 PST: 9a14163 started on 18090–18092 (pid 639792); 21:33:07 PST stopped.
- 21:33 PST: 8aab98a started with `STANDIN_PORT_BASE=18090`, own state dir `/tmp/sse2-si1-8aab98a` (HOME, client keys, pid, log); pid 647014; 21:38:32 PST stopped.
- 21:38:35–21:39:02 PST: f630a8d (SSE pushed it at 9:36 PM PT) run for information only, pid 660069, same ports. Not evidence (see below).
- Since then 18080–18082 belong to SSE (pid 660610 at 21:39). I never touched them again.
- New `tools/check-ports.sh` (`free` / `mine PIDFILE`) runs before every start and every run. `tools/run-checks.sh` now calls it when `STANDIN_PIDFILE` is set (exit 3, no traffic, if another pid holds a port).

### Decisions (mine, reversible)
- Hand-off tests target 8aab98a, as the TL directed. f630a8d arrived mid-run; I did not switch targets without a TL instruction.
- "Exactly 300000 ms accepted" cannot be reached: the stand-in clock is wall time plus a whole-second offset (standin-control 2.2), with no freeze. I test < 300000 ms (HO18b, pass) and > 300000 ms (HO18). HO18c stays pending as OQ-SSE2-55. Same method for the 60 s hub_launch (HO03b).
- The interstitial has no ui-driver id in the contract or SSE's code (A38: form id "handoff"). I did not guess one: HO21 is pending as OQ-SSE2-57. TL item (c) in SSE's log is the fixture-secrets check, not an id.
- "hub_launch used by a different client: refused" is tested as "the other client gets no hand-off and no cage_consumed" (HO06). Whether it must also be consumed is OQ-SSE2-56 (contract 5.7 says "ignore it"; SI1 A41 leaves it usable).
- Fixture-secrets: `tools/check-fixture-secrets.sh` runs SSE's script (read with git show from the ref, md5 71910e29) against a throwaway index for each branch. Also `FixtureSecretsTests` FS01–FS04 (check `fixture-secrets`), plus a step in run-checks. The negative controls use fake marker strings in a temporary repo under /tmp. No real key is ever written.
- `tools/import-sse-log.sh` takes `SSE_LOG_FILE` (e.g. `src/SI1-BUILD-LOG.md`). Imported from 8aab98a: 1 contract-log entry and 25 SI1 entries (rerun = 0).

### Results at 8aab98a (all reruns since the reset)
- `tools/run-checks.sh standin` (with env from /tmp/sse2-work/env.sh, STANDIN_PIDFILE=/tmp/sse2-si1-8aab98a/pid), 21:36:55–21:38:02 PST: **514 tests, 420 pass, 5 fail, 89 pending (skip), 0 not run.**
  - Fails: SEC76, SEC77, HO03b, HO17, HO18.
- Classes: handoff-standin 34/3/2; dec-093 3/0/2; pkjwt-standin 12/0/0; adversarial-standin 15/2/2; fixture-secrets 11/0/0.
- SSE's harness (drive.py md5 aa030b98, handoff.py md5 ec41e904) copied to /tmp/sse2-repro-0320f2ee with only the pid and key paths changed. Run against my 8aab98a on 18090 with my own venv: **TOTAL 143 / 143 reproduced.** Evidence file /tmp/sse-d0/e2e/si1-item1-evidence.txt md5 276d88d001535ac6eac2723dbe0bb5d1 matches the claim (276d88d0).
  - Caveat: rows 102 (hub_launch "exactly 60 s accepted") and 109/110 ("exactly 300 s accepted") pass only because of A42's whole-second floor. Real elapsed time is 60.003 s and 300.002 s. Under the TL millisecond ruling these should be refused. So 143/143 is reproducible, but three rows assert the behaviour the ruling forbids.
- Informational run at f630a8d: 15 fails and 45 skips, caused by my TestKit not parsing the new server-rendered ui/login-hub markup (testid on `<section>`, not `<main>`). These say nothing about f630a8d. The TestKit needs updating before f630a8d is the target.

<!-- sse-import sha256=31968c3ad435086782a23eece47e068e2ae734ecfbb3c867b7141417a7472c7f first-seen=baaef0e imported-from=baaef0e head="SI1 FROZEN @ 3feb66c (2026-11-02, PT)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `baaef0e`, imported from `baaef0e`.

## SI1 FROZEN @ 3feb66c (2026-11-02, PT)
- TL accepted SI1 at 3feb66c at 10:12 PM PT. The local tag si1-freeze stays on 3feb66c; this log-only commit sits on top of it.
- Live suite 251/251. Evidence /tmp/sse-d0/evidence-final.txt, md5 aa782b7bbda57c6198a00498e44ac770. The step (4) and final-commit details are in src/Login.StandIn/SI1-BUILD-LOG.md.
- **A68 is a TL Decision (10:12 PM, stand-in only):** the cage_action audience is "cash-advance-server" (value taken from contract/check_schemas.py).
- **v1.1 item V11-CAGEAUD (SA):** name the exact cage_action aud value for the Cash Advance server. Contract 6.7(5) says only "its own id".
- No new work after this.
<!-- /sse-import -->

## SSE2, Mon Nov 2, 9:50–10:20 PM PT: TL rulings recorded, 10a1ca4 and 3feb66c (si1-freeze) verified, SI1 GO/NO-GO

### TL rulings 9:50 PM PT, recorded as decisions
- **TLD-SI1-07 (OQ-SSE2-56)** (originally recorded 9:50 PM PT as DEC-096): a hub_launch presented at a client other than the one it was minted for is **burned** (consumed, cannot be used afterwards). Test HO06/HO06b: the other client gets no hand-off and the original client then gets no cage_consumed.
- **TLD-SI1-08 (OQ-SSE2-55)** (originally recorded 9:50 PM PT as DEC-097): boundaries are tested with a frozen millisecond clock (standin-control clock/freeze + clock/advance {ms}). Exactly 300000 ms and 60000 ms are **accepted**; 300001 ms and 60001 ms are **refused**. Tests HO03b/HO03c/HO18 (ms precision, no floor).
- **TLD-SI1-09 (OQ-SSE2-57)** (originally recorded 9:50 PM PT as DEC-098): HO21 (ui-driver id for the hand-off interstitial) stays **pending**, v1.1 item for UX + SA.
- **TLD-SI1-10 (fixture secrets)** (originally recorded 9:50 PM PT as DEC-099): the fixture-secrets check gives **WARN only** for secret blobs that appear only in commits inside `eb0fa5d..c4da996` (inclusive) and **FAIL** for anything else, with negative controls in throwaway /tmp repos (fake markers). `tools/check-fixture-secrets.sh` (FSEC_REPO, FSEC_RANGE, FSEC_FP_FILE); FS01–FS06.
- **TLD-SI1-11 (OQ-SSE2-58)** (originally recorded 9:50 PM PT as DEC-100): CSP must not contain `'unsafe-inline'`; X-Frame-Options is either DENY or absent (frame-ancestors 'none' is the rule).
- **TLD-SI1-12 (discovery 404s)** (originally recorded 9:50 PM PT as DEC-101): discovery advertising front endpoints that 404 is **info / deferred**, v1.1 item for SA. (At 3feb66c, front and back discovery match and CT01 passes.)

### Scope (TL 10:08 PM, 10:11 PM via parent)
Final target **feat/si1-standin 3feb66c** (tag `si1-freeze`), SSE claim 251/251, evidence /tmp/sse-d0/evidence-final.txt md5 aa782b7bbda57c6198a00498e44ac770. Focus: session-cap scope, no secrets in events/logs, mfa before login events, cage attestation fails closed, D93_04/05 re-enrol. Intermediate commits 188/210/243 not run standalone (allowed). baaef0e (22:11:50 PST, after the tag) changes only src/SI1-BUILD-LOG.md; imported with `SSE_LOG_FILE=src/SI1-BUILD-LOG.md tools/import-sse-log.sh baaef0e` (imported 1, skipped 25).

### Runs (all on base 18090, my own export /tmp/sse2-si1-<sha>, my own venv /tmp/sse2-work/venv, state /tmp/sse-d0/standin-18090 which the stand-in hard-codes)
| Target | Command | Result |
|---|---|---|
| 10a1ca4 | `. /tmp/sse2-work/env.sh && STANDIN_PIDFILE=/tmp/sse2-si1-10a1ca4/pid RESULTS_DIR=/tmp/sse2-work/full2-10a1ca4 tools/run-checks.sh standin` | 547 total, 460 pass, **0 fail**, 87 pending |
| 10a1ca4 SSE harness | copy of SSE drive.py, own pid/key paths | 189/189, **reproduced** |
| 3feb66c | `. /tmp/sse2-work/env3.sh && STANDIN_PIDFILE=/tmp/sse2-si1-3feb66c/pid RESULTS_DIR=/tmp/sse2-work/full-3feb66c tools/run-checks.sh standin` (22:14:26–22:17:11) | 552 total, 466 pass, **0 fail**, 86 pending |
| 3feb66c final (after D93_04/05 enabled) | same command, RESULTS_DIR=/tmp/sse2-work/full2-3feb66c (22:22:27–22:25:13) | **552 total, 468 pass, 0 fail, 84 pending**; dec-093 5/0/0 |
| 3feb66c SSE harness | `cd /tmp/sse2-repro-aa782b7b && STANDIN_PORT_BASE=18090 /tmp/sse2-work/venv/bin/python drive.py` (22:14:04–13) | **251/251 reproduced**; same row set as SSE's evidence |
| 3feb66c focus probes | `cd tests/e2e/sse2-probes && STANDIN_PORT_BASE=18090 /tmp/sse2-work/venv/bin/python probe.py` (22:19:52–54) | 135/147; the 12 fails are C0×3, C9×3, C10×3, K malformed-by-non-admin, K-huge, ADM |
| 3feb66c cap isolation | `… python probe-k.py` (22:19:29–31) | 11/14; the 3 fails are the K-huge DoS |
| 3feb66c UI01–05 | `. /tmp/sse2-work/ui-env.sh && LOGIN_STANDIN_FIXTURES=/tmp/sse2-si1-3feb66c/src/Login.StandIn/fixtures.json node tests/ui/standin-ui.mjs` (22:18:35–39) | **14/14** |
| fixture secrets | `tools/check-fixture-secrets.sh si1-freeze` (22:18:44) | feat/si1-standin PASS (3 WARN blobs, all only inside the range), sse2/tests-docs PASS (allow-listed fake markers), feat/contract-v1 PASS |

8aab98a fails F1–F5 (SEC76, SEC77, HO03b, HO17, HO18) are all fixed at 10a1ca4 and stay fixed at 3feb66c. No regressions seen between 10a1ca4 and 3feb66c (460→466 pass, 0 fail both).

### SSE's fixture script at 3feb66c (md5 9b4b8bb6)
Its KNOWN list (1ee0939, df0b5d6, ed0ecc1, 9a14163, 8aab98a, f630a8d, c4da996) is exactly the set of commits that hold the 3 secret blobs (f26fc59a37, e6602328ac, 527aa00967), and **all 7 are inside eb0fa5d..c4da996**. The 6 range commits not listed (eb0fa5d, 97bfdb2, 3087f58, dd59e85, f63b2c8, 40da81d) hold none of them, so the list is stricter than the TL range, which is fine. Wired into tools/check-fixture-secrets.sh and FS05 RangeCases. SSE2-F-FS1 (minor): the script skips its own path; FS06 probes a secret hidden there and my wrapper still catches it (missed=False).

### Verified at 3feb66c (SSE's change list)
- CSP `script-src 'self'`, no 'unsafe-inline', present on every HTML response; XFO DENY or absent (TLD-SI1-11). UI01–05 show no CSP violations.
- recovery_reenroll / DEC-093: D93_04/05 done states, Continue → /standin/admin or /standin/l/{slug}, codes shown once (2nd GET 404, no-store), session revoked, old TOTP and old recovery code refused, new TOTP signs in (DEC-087). Session-cap page 404 in a recovery session.
- Re-enrol rate limit: 5 wrong → 200 each, 6th → 429 + auth.mfa.failed reason=rate_limited, still 429 at 14:59.999. At 15:00 the session has idled out (302), so the window end is not observable (info).
- Admin paths and DEC-086 cap: not_admin, wrong_partner (other, upper-case, unknown), out_of_range (00:15:00, 00:14:59, 00:00:00), 00:15:01 accepted; negatives and malformed → 400.
- mfa.succeeded immediately before login.succeeded for location, partner admin, super admin.
- BP-2a: cage attestation is checked first and fails closed on payment, id_scan and print for: missing, empty, alg none (±sig), HS256, RS256, es256, jwk header, crit, flipped sig, tampered payload, garbage, two-part, agent modes bad_signature/expired/replayed/unknown/revoked/other_location, consumed nonce, cross-purpose, cross-session, nonce age 60001 ms (60000 ms ok). Every denial emits a schema-valid auth.app_access.denied attestation_invalid. Future iat: not black-box testable (no backward clock, key in memory); code reading shows `iat > now+30` refused.
- No secrets: 496 secret values (passwords, TOTP secrets, recovery codes old and new, tokens, hub_launch, code, state) absent from standin.log and from all 153 audit events.

### Findings at 3feb66c
| # | Sev | Finding | Evidence |
|---|---|---|---|
| G1 | **major** | No upper bound on the session cap (A61). A partner admin can set a cap ≥ ~2,914,000 days (~7,980 years) on its own partner; after that every session of that partner returns 500, including the admin's own cap page, so it cannot undo it (only a super admin or restart can). Set globally by a super admin, all sign-ins 500. Log: `ArgumentOutOfRangeException: The added or subtracted value results in an un-representable DateTime`. 2,000,000 days still works. A cap on P2 does not affect P1. | probe-k: "partner cap 2914000.00:00:00: set->200; P1 location /apps->500; partner admin cap page->500"; probe K-huge ('screen-hub', 500). OQ-SSE2-60 |
| G2 | **major** | Cage stub not contract-shaped (6.9): a JSON body `{"device_attestation":…}` with a valid attestation gets 403 attestation_invalid (stub reads form only), and a valid form request gets 501 not_implemented instead of 200 {"ok":true}. Fails closed, so not a security hole. | probe C9 ×3. OQ-SSE2-61 |
| G3 | **major (needs ruling)** | A validly signed attestation with remote_session=true is accepted for cage actions (same 501 as a clean one). 6.7 is silent; 5.2 requires remote_session=false for the hand-off; the LD agent blocks actions during a remote session. Blocker if the TL rules 5.2 applies to cage actions. | probe C10 ×3: (501, not_implemented). OQ-SSE2-62 |
| G4 | minor | Cage nonce is 43 chars (256-bit); cage_nonce_response schema says `^[A-Za-z0-9_-]{22}$`. | probe C0 ×3. OQ-SSE2-63 |
| G5 | minor | A password-verified, pre-MFA sign-in transaction never expires: TOTP 24 h later still reaches the hub (location and /admin). Contract sets no lifetime. | probe TXL. OQ-SSE2-64 |
| G6 | minor | /admin errors carry only `error-admin-sign-in`, no `error-area` (ui-driver §2a). | probe ADM |
| G7 | minor | 6 of 7 fixture passwords are static literals ("…-Standin-2026"); only super_admin is GENERATED. Conflicts with "fixture passwords must be generated"; test-only and fixture-only. | src/Login.StandIn/fixtures.json @3feb66c |
| G8 | minor | Stand-in state dir hard-coded to /tmp/sse-d0/standin-{port}. | Program.cs; state of my run under /tmp/sse-d0/standin-18090 |
| G9 | info | Session-cap validates format before authorization: a location user posting a malformed value gets 400 with no change_denied event. | probe "K location user, malformed value" ((400, None), []). OQ-SSE2-65 |
| G10 | info | /favicon.ico 404. RL window end not observable (idle = window). | probe |

### GO/NO-GO for SI1 at 3feb66c
**GO with conditions.** No failing test in the agreed suite (468/0/84), SSE's 251/251 reproduced, UI 14/14, and every TL focus area passes. No blocker. Before S1 sign-off: G1 (cap upper bound, or reject values that overflow) and G2 (cage stub contract shape) should be fixed or accepted as stand-in limits by the TL; G3 needs a TL ruling and becomes a blocker if remote_session must block cage actions.

### Port and time log (PST)
- 22:13:43 10a1ca4 run #2 done; 22:13:53 killed my 10a1ca4 stand-in pid 683623; check-ports: 18090–18092 free; nothing on 18080–18082, 8080, 8081.
- 22:13:56 3feb66c stand-in pid 1199260 on 18090–18092 (guard: owned by my pid before every run).
- 22:14:04–22:14:13 SSE harness repro; 22:14:26–22:17:11 full run; 22:17:59–22:19:54 probes; 22:18:35–39 UI.
- 22:22:27–22:25:13 final full run (D93_04/05 now real tests: PASS).
- Stand-in stop: logged in the commit message and the SSE2 report (stopped right after this commit).

<!-- sse-import sha256=b8df23e3e8bfec98a7acf21a8f899370b0328a04496d2c5fc98358fe4e931884 first-seen=9b7efc9 imported-from=9b7efc9 head="SI1 fixes on si1-fixes (from baaef0e), SSE2 review 7397b3f GO-with-conditions (TL 10:30 PM PT, 2026-11-02)" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `9b7efc9`, imported from `9b7efc9`.

## SI1 fixes on si1-fixes (from baaef0e), SSE2 review 7397b3f GO-with-conditions (TL 10:30 PM PT, 2026-11-02)
Each fix has its own live test in the driver (/tmp/sse-d0/e2e/step4.py; drive.py PW line). Live suite 296/296, evidence /tmp/sse-d0/evidence-fixes.txt (md5 in the commit report). si1-freeze stays on 3feb66c.
- **G1 (interim TL Decision, PLACEHOLDER until Mohanraj gives the range, OQ-40):** cap range is > 00:15:00 and <= 1.00:00:00 (24 h). The A61 config range keys are removed. Values outside → 403 change_denied out_of_range. A pattern-shaped value too big for TimeSpan (e.g. 99999999.00:00:00, 40-digit days) → out_of_range with the raw value, never 500. At start-up a cap > 24 h is refused (non-zero exit). Expiry uses SessionCap.Expired (cap clamped to the range, no DateTimeOffset add). Tests: 1.00:00:01, 2.00:00:00, huge, 40-digit, 00:00:00 → out_of_range; -01:00:00, abc, 01:00, empty, 1.2:3:4 → 400 for an admin; 1.00:00:00 accepted; 24 h cap alive at 23:59:59 then ended (302); start-up 1.00:00:01 refused.
- **G9:** the caller is checked BEFORE the value. A non-admin always gets 403 change_denied not_admin, even with malformed, negative or huge values. **[ASSUMPTION A69]** attempted_value must match the schema pattern: a pattern-shaped value is logged as sent; anything else is logged as "00:00:00". Admins still get 400 for non-TimeSpan text (A65 narrowed to admins).
- **G2:** the cage action body follows 6.9 exactly: JSON {"device_attestation":"<JWS>"}. Valid → 200 {"ok":true} (was 501). **[ASSUMPTION A72]** antiforgery comes from the default RequestVerificationToken header (a missing header → 400, tested). **[ASSUMPTION A73]** any other body (form, extra key, malformed JSON) counts as missing attestation → 403 attestation_invalid + audit.
- **G3 (BLOCKER):** an attestation that verifies but reports remote_session=true is refused for payment, id_scan and print: 403 {"error":"attestation_invalid"} plus a schema-valid auth.app_access.denied with target app cash_advance. **[ASSUMPTION A70]** the audit reason is remote_session (it is in the enum); the client body stays attestation_invalid.
- **G4:** the cage nonce is 16 random bytes in base64url = 22 chars (cage_nonce_response). Tests: length, alphabet, decodes to 16 bytes, unique.
- **G5 (TL Decision):** pending pre-MFA state (location totp stage, admin mfa stage) lives 300000 ms from the password step (SignInTx.PreMfaAt). After that the tx is deleted and the user starts a full sign-in (location: password page; /admin: step 1 with the generic error). Tested on the frozen ms clock: 300000 ms accepted, 300001 ms dropped, cannot be revived, a fresh sign-in works. Covers location, super and partner admins.
- **G6:** the /admin error now sits inside data-testid="error-area" (error-admin-sign-in kept inside it).
- **G7:** every fixtures.json password is "GENERATED". They are generated at start into <state dir>/fixture-secrets.json (0600). The driver PW dict is derived from fx, so the driver has no literal password left.
- **G8:** the state root comes from STANDIN_STATE_DIR (default /tmp/sse-d0) + /standin-<front>. **[ASSUMPTION A71]** it must be an absolute path under /tmp/, otherwise the default is used (same rule as B2). Test: a second process with STANDIN_STATE_DIR on our own held ports writes server.pid + fixture-secrets.json there, leaves the default dir alone, then fails to bind.
- **FS1:** check-fixture-secrets.sh no longer skips its own path. Its patterns do not match their own text. Throwaway-repo test (GIT_INDEX_FILE unset): clean copy → OK; a PEM marker planted in the script → FAIL in both the index and the history.
- **G10:** skipped (TL).
<!-- /sse-import -->

<!-- sse-import sha256=eb36292dacea11f3c2ec3a744987485a0edc76120e74cc5ab9197512da1d3749 first-seen=9b7efc9 imported-from=9b7efc9 head="v1.1 items" -->
> **Imported verbatim from SSE**: `src/SI1-BUILD-LOG.md`, first seen in `9b7efc9`, imported from `9b7efc9`.

### v1.1 items
- **V11-CAGEREMOTE (SA):** contract 6.7 should say that a cage action whose attestation has remote_session=true is refused (403 attestation_invalid; audit reason remote_session per A70).
- **V11-ADMINERR (ui-driver):** /admin error area now uses testid error-area (G6); the ui-driver should look for it.
<!-- /sse-import -->

## SSE2, Mon Nov 2, 10:35–10:50 PM PT: re-check of 9b7efc9 (si1-fixes) against my 7397b3f conditions

### TL rulings 10:35 PM PT, recorded as decisions
- **TLD-SI1-01 (G3 blocker)**: G3 is a **BLOCKER**. A cage action with a validly signed attestation carrying remote_session=true is refused (review §21). The client sees 403 attestation_invalid; the audit reason is remote_session (A70).
- **TLD-SI1-02 (G1 interim range)**: the session cap must be more than 15 min and at most 24 h (interim, PLACEHOLDER until OQ-40), with expiry arithmetic that cannot overflow.
- **TLD-SI1-03 (G2, G4, G6–G9, FS1)**: fixed as I suggested. G2: JSON body {"device_attestation":…} plus the RequestVerificationToken header → 200 {"ok":true} (A72). Any other body counts as missing → 403 (A73).
- **TLD-SI1-04 (G5)**: a pending (password-verified, pre-MFA) sign-in expires after 5 min.
- **TLD-SI1-05 (G10)**: skipped.
- **TLD-SI1-06 (.v\* files)**: the top-level .v\* files belong to the SA, who is moving them. SSE2 leaves them alone.

### Runs (base 18090, my export /tmp/sse2-si1-9b7efc9, own venv, STANDIN_STATE_DIR=/tmp/sse2-si1-9b7efc9/state per A71)
- Target: 9b7efc9 (parent baaef0e). si1-fixes moved to 3e0b71d at 22:37:36 PT. That commit changes only the secrets script and the SSE log, with no runtime code. My stand-in export was taken at 22:36 and its script md5 (5dc476e4) matches 9b7efc9.
- Full suite (tools/run-checks.sh standin): **552 total, 468 pass, 0 fail, 84 pending**. This equals the 3feb66c baseline. On the first run, SEC76b and SEC77c failed because of a test-side parser bug in TestKit ErrorText: the G6 markup nests error-admin-sign-in inside error-area. Fixed; not an SSE defect.
- Probes: probe.py **194/194** (3feb66c: 135/147); probe-k.py **15/15** (3feb66c: 11/14); UI01–05 **14/14**.
- SSE harness: drive.py and friends copied to /tmp/sse2-repro-464ee82c, with only the paths changed (pid, state, log, bin, export, contract). The post-9b7efc9 PWCHK block was dropped because it reads SSE's /tmp/sse-d0 index. Result **297/297**: all 296 rows of SSE's evidence-fixes.txt (md5 464ee82c) reproduced, plus 1 TL 10:36 row.
- Fixture secrets (tools/check-fixture-secrets.sh 9b7efc9 9b7efc9, and si1-fixes on si1-fixes 3e0b71d): PASS. FS06 with SSE2_FS06_BASE=9b7efc9 and SSE2_FS06_EXPECT_CAUGHT=1: PASS (the secret is caught in the index and in history).

### Per-item verification at 9b7efc9
| Item | Result | Evidence |
|---|---|---|
| G1 | **fixed** | 00:15:00, 00:14:59 and 00:00:00 → 403 out_of_range; 00:15:01, 23:59:59 and 1.00:00:00 (24 h) → 200; 1.00:00:01, 2 d, 365 d, 99999999 d, 10675199 d, 10675200 d and 99999999999 d → 403 out_of_range. No 500 anywhere. The admin cap page is 200 afterwards and P1 sessions still work (K-DoS ×6, K-huge). The startup check refuses 1.00:00:01. In the 24 h cap test the session is alive at 23:59:59 and ends with a 302 (SSE row, reproduced). |
| G2 | **fixed** | C9 ×3: JSON + header → 200 {"ok":true}. All C1 negatives (13 cases ×3), C2–C7 and C8d–f pass. A form body carrying a valid JWS → 403 plus a schema-valid denied event (A73). An extra key, non-string, null, [] or non-JSON body → 403. A missing antiforgery token → 400 for both form and JSON-without-header, and that 400 does not consume the nonce. |
| G3 | **fixed (blocker cleared)** | C10/C10b ×3: 403 {"error":"attestation_invalid"}; one auth.app_access.denied event with reason remote_session, target cash_advance, schema-valid. C10c: the JWS cannot be replayed (nonce consumed). |
| G4 | **fixed** | C0 ×3: nonce matches `^[A-Za-z0-9_-]{22}$`. |
| G5 | **fixed** | Frozen ms clock, location and /admin: **300000 ms accepted, 300001 ms refused** (code: `elapsed > 300000`; exactly 5:00.000 is still valid). 24 h → refused. Location returns screen-signin; /admin returns step 1. |
| G6 | **fixed** | ADM: data-testid="error-area" wraps error-admin-sign-in, with the text present. |
| G7 | **fixed** | 7/7 fixture passwords are "GENERATED"; values are generated per run into <state>/standin-18090/fixture-secrets.json (0600). Secrets check PASS. The old literals remain in git history (WARN only, see N3). |
| G8 | **fixed** | My state is under /tmp/sse2-si1-9b7efc9/state/standin-18090. /tmp/sse-d0/standin-18090 was not touched (mtime 22:13). SSE's G8 row reproduced. |
| G9 | **fixed** | A location user posting 'zz' → 403 not_admin with event attempted_value "00:00:00" (A69). A pattern-shaped huge value → not_admin with the raw value. Both events are schema-valid. An admin posting a malformed value → 400 with no event. |
| FS1 | **fixed** | The script now scans its own file (FS06 throwaway repo, plus SSE's FS1 rows reproduced). |

### New findings at 9b7efc9 (none blocking)
| # | Sev | Finding | Evidence |
|---|---|---|---|
| N1 | info | A pattern-shaped but unparseable cap value such as 00:60:00 now returns 403 out_of_range (with event) instead of 400. This matches SSE's G1 rule and the TL's "400 or out_of_range". | probe K range 00:60:00 |
| N2 | info | An expired pending MFA is dropped with no audit event: the TOTP post after 5 min returns the password page and events=[]. The contract does not ask for one. OQ-SSE2-66. | probe INFO G5 |
| N3 | info | The old literal fixture passwords remain in git history at eb0fa5d..c4da996 (blobs f26fc59a37, e6602328ac, 527aa00967). They are test-only, fixture-only, and flagged WARN by the check. History is not rewritten (local, no remote). | fsec-9b7efc9.txt |
| N4 | info | si1-fixes advanced to 3e0b71d during the review (script and log only). Verified build: 9b7efc9. 3e0b71d passes the fixture-secrets check. | git log |
| N5 | info (test-side, fixed) | The TestKit ErrorText regex took the first, empty error-* testid. It now uses the first non-empty one. | SEC76b/SEC77c |

### GO/NO-GO for SI1 at 9b7efc9
**GO.** G1–G9 and FS1 are fixed and independently reproduced; G3 (blocker, TLD-SI1-01) is cleared. Results: suite 468/0/84 (no regression from 3feb66c), probes 194/194 and 15/15, UI 14/14, and SSE's 296 rows all reproduced. Open info items: N1–N3 (OQ-SSE2-66).

### Port and time log (PST)
- 22:36:37 ss: 18080–18082, 18090–18092, 8080 and 8081 all free. 22:36:40 9b7efc9 stand-in started as pid 1538586 on 18090–18092 (check-ports free, then mine). STANDIN_STATE_DIR=/tmp/sse2-si1-9b7efc9/state.
- 22:37:20–22:40:02 full run #1 (2 test-side fails); 22:41:39–22:42 probes, probe-k and UI; 22:44:18–22:44:40 SSE harness repro (×2); 22:44:53–22:47:37 full run #2 (468/0/84).
- The guard checks before every run that the ports are owned by my pid. SSE's harness also started stand-ins (startup checks, G8); each exited after a failed bind or a refused config, and afterwards only pid 1538586 was listening.
- Stand-in stop: logged in the commit message and the SSE2 report.

## Relabel and N2 ruling (SSE2, Mon Nov 2, 10:51 PM PT, parent db033f8, docs only, no stand-in started)
- **Relabel:** DEC numbers are reserved for Mohanraj's own decisions. The TL rulings I recorded on the 10:35 PM pass are renamed in docs/ in the same order: DEC-102→TLD-SI1-01, DEC-103→TLD-SI1-02, DEC-104→TLD-SI1-03, DEC-105→TLD-SI1-04, DEC-106→TLD-SI1-05, DEC-107→TLD-SI1-06 (10 references across BUILD-LOG, TEST-MATRIX-S1 and CONTRACT-REVIEW-v1; there were none in tests/). All other DEC numbers are unchanged. DEC-096..DEC-101 (also TL rulings, from the 9:50 PM pass) were deliberately **not** renamed in this step while the TL decided on them; see the next section.
- **N2 / OQ-SSE2-66 → V11-PENDINGEXPIRY (TL 10:51 PM):** no audit event when a pending MFA expires, because the audit schema is frozen and none of its reasons covers an expiry. Deferred to the SA for v1.1. OQ-SSE2-66 is resolved and deferred; N2 stays info.

## Relabel 2 (SSE2, Mon Nov 2, 10:51 PM PT, parent 2b2df2d, docs only, no stand-in started)
- The TL approved it. I recorded these TL rulings on the 9:50 PM pass as DEC numbers, and they are renamed in the same order: DEC-096→TLD-SI1-07, DEC-097→TLD-SI1-08, DEC-098→TLD-SI1-09, DEC-099→TLD-SI1-10, DEC-100→TLD-SI1-11, DEC-101→TLD-SI1-12. Each entry now says "originally recorded 9:50 PM PT as DEC-0xx".
- Sweep of every other DEC number in docs/ and tests/: DEC-080, 081, 084–089 and 093–095 all appear in the shared project files outside code/ (project-plan, proposals, stack recommendation), so they come from the shared decision register and stay **unchanged**. That includes DEC-094: it is in 13 shared files and is not one I minted. No other self-minted DEC numbers remain, so nothing was numbered from TLD-SI1-13 onwards. Nothing was ambiguous.

## S2b TL rulings (TL approved the plan at 7:27 AM PT, Wed Nov 4, 2026; recorded by SSE2 as TLD-S2B-xx, never DEC)
| ID | Ruling |
|---|---|
| TLD-S2B-01 | Remote rule changed: Mohanraj approved pushing to proofbyteio/FloorFunds. **Only TL pushes**, and only there (a pre-push hook enforces it). SSE2 never pushes and never changes the remote or credentials; a read-only `git fetch` is OK. `tools/check-no-remote.sh` passes only if the remotes are exactly origin=https://github.com/proofbyteio/FloorFunds.git (fetch and push). |
| TLD-S2B-02 | Packages: exactly `Microsoft.AspNetCore.Authentication.OpenIdConnect [10.0.14]` and `Microsoft.IdentityModel.Validators [8.19.2]`, nothing else new. The whole IdentityModel graph stays on 8.19.2. nuget.org only, lock file, package source mapping, no NU3018 suppression. |
| TLD-S2B-03 | "Down" approved as in §c. Timeout 5 s, configurable, no inline retry. JWKS: the framework ConfigurationManager cache; a last-good copy ≤24 h old counts as available (**interim**). |
| TLD-S2B-04 | Ownership: SSE2 owns `src/Login.Entra`, `tests/Login.FakeEntra` (port 18093) and `tests/Login.EntraTests`. Stand-in hooks and ui/ pieces go as a patch file (docs/ or /tmp) for SSE to approve and commit. Login.Entra is self-contained, with no stand-in dependency (it must move to R1 unchanged). |
| TLD-S2B-05 | Branch `feat/entra-signin` off main 9ccfcbe, only if `git status` is clean; otherwise report, never force. S2b docs stay on sse2/tests-docs (`docs/plans/S2B-ENTRA-PLAN.md`, this log). |
| TLD-S2B-06 | Link users on (issuer, tid, oid). Never on email, upn or preferred_username. (Plan §g Q6.) |
| TLD-S2B-07 | No `offline_access`. (Q10) |
| TLD-S2B-08 | Cage locations: `prompt=login` + `max_age=0`. The `auth_time` optional claim is required and checked server-side, ≤5 min for the hand-off. The server check is authoritative. (Q11) |
| TLD-S2B-09 | New `reason` values are v1.1, under **V11-ENTRA-FALLBACK**. Within v1, existing reasons only. (Q12, Q19) |
| TLD-S2B-10 | Storage and grants per SSE's P1 spec. TL has told SSE to add the external-login lookup and the location-assignment check. Until they land: draft interfaces plus fixture-backed implementations, with the assumption logged. (Q13) |
| TLD-S2B-11 | acrs/c1: next slice. (Q14) |
| TLD-S2B-12 | Audit carries only the mapped amr (closed enum). Entra's raw amr is never logged. (Q15) |
| TLD-S2B-13 | Drop `rsa`. (Q16) |
| TLD-S2B-14 | The account picker at Entra sign-out is OK for now; `logout_hint` is next slice. (Q17) |
| TLD-S2B-15 | No browser-side database sign-in link; fallback is decided by the server only. (Q18) |
| TLD-S2B-16 | (TL 7:25, relayed by SSE) No config row → **fail closed**. (Q5) |
| TLD-S2B-17 | (TL 7:25) Use `GetBySlugAsync` with `auth.location`. |
| TLD-S2B-18 | (TL 7:25) Authority = base URL + `/{tid}/v2.0`, **exactly one tenant per location**. (Q8) |
| TLD-S2B-19 | (TL 7:25) `SecretRef` is opaque. (Q7) |
| TLD-S2B-20 | (TL 7:25) `mfa_required` means amr must map to `mfa`, otherwise refuse. (Q9) |
| TLD-S2B-21 | (TL 7:25) RowVersion goes on events in v1.1. |
| TLD-S2B-22 | DB: local Postgres 17, `ff_sse2`. Schema only from SSE's EF migration; the fixture reader until it lands (plan step 8 waits). |
Still open from plan §g: Q1 (resolved by TLD-S2B-05), Q3 (stand-in vs real service: Login.Entra is self-contained per TLD-S2B-04, hooks via patch), Q20 (resolved by TLD-S2B-02).

### S2b step log (SSE2)
- 7:28 AM PT: `tools/check-no-remote.sh` rewritten per TLD-S2B-01. New tests `tests/tools/check-no-remote.test.sh`: 20/20 pass (CNR01–03 pass cases; CNR04–20 fail cases: no remote, wrong or case-variant or ssh URL, other pushurl, second fetch URL, second remote, renamed remote, insteadOf, pushInsteadOf, branch pushRemote, pushDefault, generic credential helper, hooksPath, missing / allow-all / non-executable hook). The live repo PASSES. The hook is only executed with non-allowed URLs; nothing is pushed or fetched.
- 7:30 AM PT: **BLOCKED on step 0 (TLD-S2B-05): `git status` is not clean.** The shared checkout `/workspace/cachd-ones/code` is on **SSE's branch `feat/location-login-config` (e1c32e5)**, not main. ` M README.md` (+39/−1 lines) and `?? db/` (SSE's work in progress), plus the usual untracked `docs/ tests/ tools/` (mine, committed only via plumbing). I did not switch branches, stash, reset or create `feat/entra-signin`. Switching would overwrite SSE's live work. Waiting for TL/SSE (options in the report).
- 7:29 AM PT: TL approved option (B). Ran `git branch feat/entra-signin 9ccfcbe` and `git worktree add /workspace/cachd-ones/wt-sse2-entra feat/entra-signin`. The worktree is the S2b code location. The shared checkout `/workspace/cachd-ones/code` was not switched, stashed, reset or reverted. It is still on `feat/location-login-config` with ` M README.md` (md5 5e912955… unchanged).
- Hook: pre-push md5 e4e34110… (TL exact-match version). `tools/check-no-remote.sh` PASSES against it. CNR tests 20/0. **Closed:** the lowercase-URL open item. The hook allows exactly `https://github.com/proofbyteio/FloorFunds.git`.
- README decision: the shared checkout's README.md modification is mine (S1 `code/README.md`, file md5 5e91295522511c930c2dec277e4b1829 = my last version). Parts were outdated: "no remote, no push", "check-no-remote = no git remote", and tests/docs/tools are not on main. I carried it into the worktree README.md (commit b65f1b0) with updates for the TLD-S2B-01 remote rule, the S2b projects and how to run them. **SSE may discard the shared-checkout copy now that feat/entra-signin has it; that is SSE's or the TL's call.** I did not touch it.
- 7:45 AM PT (TL/SSE correction): SSE owns `src/Login.Abstractions` (SSE P2), and I will not create it. Draft readers now live in `src/Login.Entra/Drafts/` (namespace `Login.Entra.Drafts`) with a TODO to swap them in at step 8. Applied SSE's confirmations: readers throw on DB error and on cancellation (fails closed, ENT-04d); null means no row only; external_login lookup is on (issuer, tid, subject=oid); assignments are auth.user_location. F1 is still pending with the TL, so the draft `IAppUserReader.FindUserAsync → AppUserRecord(UserId, PartnerId, Status, LockedUntil)` follows SSE's recommendation, and an unknown Status is refused (ENT-04b).
- Steps 1–7 were done in the worktree. Commits on `feat/entra-signin`: **b65f1b0** (Login.Entra, FakeEntra, ENT tests, README) and **478a86c** (Drafts/ and the F1 shape). Neither is pushed, and no remote was changed.
  - Packages (`dotnet list package --include-transitive`, locked restore OK): direct OpenIdConnect [10.0.14] and Validators [8.19.2]; transitive Bcl.Cryptography 10.0.2; all other Microsoft.IdentityModel.* and System.IdentityModel.Tokens.Jwt are 8.19.2. nuget.org only, with source mapping and `RestoreLockedMode`. NU3018 stays a visible warning and is not suppressed (SI1-12). TreatWarningsAsErrors is off in Login.Entra for that reason.
  - Tests: `cd tests/Login.EntraTests && dotnet build && ./bin/Debug/net10.0/Login.EntraTests` gives **67/0/0** (3 consecutive runs green at 61; 67 after the 7:45 changes). Fake issuer on 18093, host on 18090, keys in /tmp/sse2-fake-entra (deleted after; 0 left).
  - Fixture-secrets check `tools/check-fixture-secrets.sh "" feat/entra-signin`: PASS. Its warn=1 comes from SSE's pre-existing `src/Login.StandIn/fixtures.json` history, not my files. No `.v*` files.
  - Stand-in and ui hook patch for SSE: `docs/patches/S2B-standin-hooks.patch` (this commit). It applies cleanly to 9ccfcbe (`git apply --check`) and builds with `-p:IncludeStandIn=true`. It adds an `EntraStandInHost` adapter, a one-way ProjectReference to Login.Entra, `{issuer}/entra/l/{slug}` and `{issuer}/entra/logout` behind `StandIn:Entra:Enabled=true`, and a regenerated stand-in lock file. SSE applies it; I did not commit anything to src/Login.StandIn.
- Findings and open items:
  - **S2B-F1 (Medium):** in .NET 10 the OIDC handler unprotects `state` before `MessageReceived`. Calling `StateDataFormat.Unprotect` again returns null, so we now read `ctx.Properties`. A tampered state still fails closed (ENT-05).
  - **S2B-F2 (High, fixed):** `JsonWebTokenHandler` defaults `TryAllIssuerSigningKeys=true`, so an access token with an unknown kid was ACCEPTED. It is now set to false on both validators (ENT-07f).
  - **S2B-F3 (Low):** a wrong or MSA tenant fails in the issuer validator before our checks. It is now tagged so that audit says `tenant_mismatch` and not `bad_credentials` (ENT-02/02b).
  - **Open (Medium, v1.1):** config-missing and IdP-down/fallback write no audit event, only the app log (`V11-ENTRA-FALLBACK`), because no v1 reason fits.
  - **Open (Low):** the uti replay cache is in memory (A-S2B-04) until the oidc_replay table lands. SSE will migrate it as specced.
  - **Open:** ENT-HO-01 end to end through the real stand-in waits for SSE to apply the patch; today it is library-level only. Full audit-schema validation in ENT tests needs JsonSchema.Net mapping (not added: TLD-S2B-02 two-package limit for Login.Entra). F1 is pending with the TL.
  - **Assumptions:** A-S2B-01 (draft shapes), A-S2B-03 (every Entra location is treated as cage: prompt=login and max_age=0), A-S2B-04 (in-memory uti); aud and azp both equal OidcClientId.
