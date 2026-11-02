# SSE build log entries (pending copy into code/docs/BUILD-LOG.md)

`code/docs/BUILD-LOG.md` does not exist and SSE may not create files in `docs/` (SSE2 owns it).
These entries are SSE's own; SSE2 may copy them in as-is. All times Mon Nov 2, 2026, PT.

### [SSE] Step 0: go-ahead check
- **What:** checked DEC-080 and DEC-081 as user-role messages in the PM transcript `4ab8dda1-594d-48bb-bd52-3239fec5f37e`.
- **Result:** verified. `t99u` 10:58 AM: "I want the team to build the login module and the hub, initially they can create a mock up and use that for further development". Card answer to `t99s368` 11:02 AM: "Both clickable screens and a stand-in login service"; "Where should the code live?" left blank (PQ-201 open).
- **Why:** team rule "don't assume anything".

### [SSE] Sources and md5
- PLAN read in full at md5 `8c42fbdd…` (matched the brief). Changed at 11:27:56 to `ae18d664…` (adds S1 check 4b, L243); contract cites `ae18d664` (snapshot `/tmp/sse-d0/plan-ae18d664.md`).
- LD rev 11:23 `6c489506…` was read from `.v1q`, but the SA's backups were later rewritten (11:29:36) and no copy with that md5 remains. Contract cites LD `bd70f29a…` (rev 11:27/11:28; snapshot `/tmp/sse-d0/login-doc-bd70f29a.md`). OQ-2.
- AUD envelope `d835c357…` §7A.4; ALW README L30-50 (read only); UX mockups README and D-37 (read only).

### [SSE] D0: toolchain
- **.NET:** `dotnet-install.sh` from https://dot.net/v1/dotnet-install.sh (redirects to https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh; sha256 `082f7685…eb2e`), `--channel 10.0 --install-dir /home/box/.dotnet`. It reported "already installed": another agent installed it at 11:27 just before; not reinstalled. Verified: SDK 10.0.403 (commit 5a2162bea6), Microsoft.NETCore.App and AspNetCore.App 10.0.14, MSBuild 18.9.15; `dotnet new web` builds (NuGet reachable).
- **Postgres:** `postgresql-17` 17.11-0+deb13u1 and `postgresql-common` 278 from Debian trixie main (https://deb.debian.org). apt over the egress proxy failed (HTTP 500; HTTPS gnutls EOF), so I fetched the signed InRelease and Packages indexes and the .debs with curl from deb.debian.org into `/tmp/sse-d0/mirror` and installed via a `file:` source list passed with `-o Dir::Etc::sourcelist` (system source files untouched; apt checked signatures and hashes). Side effect: `/var/lib/apt/lists` now holds the indexes from that local mirror.
- Cluster 17/main, port 5432 (was free), default `listen_addresses` (localhost only). Started to verify (`select version()` OK), then stopped by me. Start: `sudo pg_ctlcluster 17 main start`. Nothing on 8080/8081.
- Logs: `/tmp/sse-d0/pg-install2.log`, `/tmp/sse-d0/dotnet-install.log`.

### [SSE] D1: contract v1 draft
- **What:** `contract/` README, contract-v1.md, errors.md, three JSON Schemas, OPEN-QUESTIONS.md (OQ-1…OQ-29).
- **Decisions:** every item labelled [SRC], [ASSUME A-n → OQ-n] or [CONFLICT → OQ-n]. Discovery/JWKS on 8084 per LD §4.5 against PLAN "front" (OQ-1). `attestation_invalid` sourced from PLAN §1.2/4a and LD §8 L420.
- **Checks:** all three schemas pass `Draft202012Validator.check_schema` (jsonschema 4.23.0, venv in /tmp/sse-d0) and 22 sample good/bad instances behave as expected (`/tmp/sse-d0/check_schemas.py`; the real tests are SSE2's).
- **Git:** commits on `feat/contract-v1` built with a private index file and `git update-ref`, so the shared working tree's HEAD (`main`, unborn) and index were not touched (SSE2's untracked `tests/` and `tools/` left alone). No remote; pre-push hook (already present) always fails.
- **Stopped after D1** as briefed: no SI1 code. Waiting on SSE2's adversarial pass and TL review.
