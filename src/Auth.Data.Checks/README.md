# Auth.Data.Checks — scratch DB and the maintenance-DB exception

Test-only integration checks for `Auth.Data` (the main run must stay 162/0, the k1 pin). This file covers the scratch DB
used by the N3 / N1 fail-closed probes and Q2 (TLD 2026-11-04 11:21 Q2 (b), 11:57 o1–o3, 12:04 F1, **12:33**). Code:
`ScratchDb.cs` (`ScratchDb`, `MaintSql`, `MaintConn`); self-test: `--scratch-selftest` in `Program.cs`.

## The postgres maintenance-DB exception [TLD 2026-11-04 12:33 item 3]

The standing rule "never point kit scripts at `postgres`" is unchanged for target DBs: `postgres`, the template DBs,
`ff_sse` and `ff_sse2` are never migrated, seeded or tested against. The one exception is the **maintenance connection**
(`SCRATCH_MAINT_DB`, default `postgres`, on loopback `127.0.0.1` / `::1` only, as `ff_scratchadm`). It exists only so the
checks can create and drop `ff_scratch_*` DBs and roles. Every statement on it goes through `MaintConn`, which refuses,
**before sending**, anything that is not on this allowlist (`refuse [maint-guard TLD 12:33]: …`, first two words only, no
literals echoed):

1. **Connect** (`ScratchDb.OpenMaint`; loopback checked first).
2. **Reads** — the exact constant queries in `MaintSql.Reads`, no others. They name only these catalogs (checked by the
   self-test against `MaintSql.Catalogs`):
   - `pg_database`, `pg_roles`, `pg_stat_activity` (the TL list);
   - `pg_auth_members`, `pg_shdescription` (needed: memberships for the `ff_scratchadm` check and the o3 snapshot; shared
     comments are read through `shobj_description`, and the snapshot counts `pg_shdescription` rows);
   - `pg_tablespace`, `pg_db_role_setting` (**beyond the TL list — open question**: only the o3 cluster snapshot and the
     `ff_scratchadm` "no role settings" check read them).
   - Functions used in those reads: `shobj_description(oid, 'pg_database' | 'pg_authid')` (the `'pg_authid'` literal is a
     catalog name for the comment lookup; `pg_authid` itself is never read), `pg_get_userbyid`, `pg_backend_pid()`,
     `array_to_string`, the `::regclass::text` cast, and `current_setting('server_version')` / `current_user` /
     `current_database()` (the `--scratch-info` line; also **beyond the TL list — open question**).
3. **DDL on marked `ff_scratch_*` names only**, in exactly these shapes (`MaintSql.Ddl`; DB name
   `ff_scratch_[a-z0-9]{6,24}`, role name `<db>_admin_api` / `<db>_login`; every pattern is anchored `^…\z`, never `$`,
   so a trailing newline is refused [SSE2 review 09e31f68 L2]):
   - `CREATE DATABASE "<db>" TEMPLATE template0 ENCODING 'UTF8'`, `COMMENT ON DATABASE "<db>" IS 'floorfunds-scratch'`;
   - `DROP DATABASE "<db>"` — only if the DB is marked and owned by `ff_scratchadm`;
   - `CREATE ROLE "<role>" NOLOGIN` or `CREATE ROLE "<role>" LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION`
     with an optional `PASSWORD '<SCRAM-SHA-256$4096 verifier>'` — only `SCRAM-SHA-256\$4096:<22 b64>==\$<43 b64>=:<43 b64>=`
     is allowed, cleartext (hex or other) is refused [TLD 13:15, KF-A]; see § "KF-A" below;
   - `COMMENT ON ROLE "<role>" IS 'floorfunds-scratch'`;
   - `DROP ROLE "<role>"` — only if the role carries the marker.
   The name guard (`GuardName`) still refuses `postgres`, templates, `ff_sse`, `ff_sse2` and anything that is not
   `ff_scratch_*`. Everything else (other `SELECT`s, `ALTER`, `GRANT`, `SET`, `DO`, multi-statement text, other comments)
   is refused.

Exact-name cleanup of an object made **unmarked on purpose** (the o2 decoy, the look-alike role, or roles leaked by
mutation a) is `CleanupUnmarkedAsync`: the prefix must match, the object must be unmarked (a DB also owned by
`ff_scratchadm`) and have no sessions; it is then marked and dropped through the marked-only path. Under PG16+
`CREATEROLE`, `ff_scratchadm` can comment on / drop only roles it created, so this can never reach someone else's role.

## Per-run roles and the sweep [TLD 2026-11-04 12:33 item 1]

o3 is "net role set identical; per-run roles allowed only if marked and dropped". The frozen migration needs
`<db>_admin_api` and `<db>_login`; they carry the runid in their name, are marked `floorfunds-scratch` right after
`CREATE ROLE` and are dropped in the same finally as the DB. The start-up sweep (`SweepAsync`) drops a stale marked role
under the DB rules: age from the runid's UTC timestamp > 1 h, no `pg_stat_activity` session as that role, not this run's,
and its DB gone (a role whose DB still exists is kept with the DB). An **unmarked** `ff_scratch_*` look-alike role is
reported (`FATAL scratch-sweep: unmarked ff_scratch_* look-alike role(s) …`), never dropped, and the run fails
(`--scratch-sweep` exits 3).

## ff_scratchadm (documented kit infrastructure)

Created once by `Auth.Data/tools/scratch-admin-setup.sh` (not re-run: it would rotate the password). Required state,
checked on every main run (setup fails, so N1 / N3 / Q2 FAIL) and by `--scratch-admincheck` (exit 4):

- `pg_roles` row `ff_scratchadm|false|false|true|true|true|false|false|-1||floorfunds-scratch-admin`
  (name | super | inherit | createrole | createdb | canlogin | replication | bypassrls | connlimit | validuntil | comment):
  marked, NOSUPERUSER, NOINHERIT, NOREPLICATION, NOBYPASSRLS, LOGIN, only CREATEDB and CREATEROLE, no connection limit,
  no VALID UNTIL; no `pg_db_role_setting` rows.
- Memberships: none, except the PG16+/PG17 implicit grant made when `ff_scratchadm` creates a role (with
  `createrole_self_grant` empty): `ff_scratchadm` is a member of each **marked** per-run role `ff_scratch_*_(admin_api|login)`
  with `admin_option = true`, `inherit_option = false`, `set_option = false`, grantor the bootstrap superuser (oid 10).
  Any other membership (either direction), attribute or setting is a violation; an implicit grant on an **unmarked**
  look-alike is also one.
- No password or secret for it is in the repo; the test-only connection string is in `/tmp/ff-db/ff_scratchadm.env`
  (0600, never printed or committed).

`ff_maint_scratch` (the maintenance DB of the first P3 version) was dropped on 2026-11-04 after its comment
(`floorfunds-scratch-maint`) and zero `pg_stat_activity` connections were checked [TLD 12:33 item 2].

## KF-A: role passwords only as a client-side SCRAM verifier [TLD 2026-11-04 13:15]

TLD 1:15 PM accepted the session-role password (item 2) on the kit's KF-A rule: a role password reaches the server
only as a client-side SCRAM-SHA-256 verifier, never as cleartext in `CREATE/ALTER ROLE`; it is random per run and
lives in memory only.

- `ScratchDb.ScramVerifier(password, salt?)` computes `SCRAM-SHA-256$4096:<salt>$<StoredKey>:<ServerKey>` (RFC 5802 /
  RFC 7677: SaltedPassword = PBKDF2-HMAC-SHA256(password, salt, 4096, 32); ClientKey = HMAC(SaltedPassword,
  "Client Key"); StoredKey = SHA-256(ClientKey); ServerKey = HMAC(SaltedPassword, "Server Key"); base64), random
  16-byte salt (`RandomNumberGenerator`). It is the same format and derivation as the kit's `db/setup.sh` `scram()`
  and the shape `db/kit/role.sql` checks (both unchanged). The password must be printable ASCII (space to `~`), as in
  the kit. SaltedPassword is zeroed after use.
- `CreateMarkedRoleAsync` sends `PASSWORD '<verifier>'`; the cleartext never leaves the process. The maint guard's
  create-role pattern (`MaintSql.ScramVerifierRe`) accepts only a `$4096` verifier with the exact base64 lengths
  (16-byte salt, 32-byte keys), so hex cleartext, `md5…`, other iteration counts, truncation, lower-case prefixes and
  quote injection are refused before sending.
- Self-test block "KF-A" (runs first, before anything else is created): RFC 7677 test vector (`pencil`); shape / salt
  per call / refusals; guard accepts the verifier and refuses 7 bad forms; then a `<runid>_login` role is created with
  a random password while every maintenance statement is captured (`MaintConn.Trace`) and `pg_stat_activity` is
  sampled from another maintenance session between `CREATE ROLE` and `COMMENT`:
  (b) exactly one `CREATE ROLE` sent, with a verifier; the cleartext is in none of the statements nor in any sampled
  query; (a) the role signs in with the cleartext over SCRAM only (`Require Auth=ScramSHA256`) and a wrong password
  gets 28P01; then the role is dropped. If the guard refuses the CREATE, the self-test stops at once (rc 1).
- **Server log / `log_statement`:** `log_statement` is a superuser GUC (`ff_scratchadm` has no SET grant and must not
  be changed), so `log_statement=all` is not used; the self-test prints the server's `log_statement` (`none`) and
  `password_encryption`. The allowlist was **not** widened to `SET log_statement`. The verify script greps the server
  log (read-only, as `postgres`) for the run window for cleartext-shaped `PASSWORD '<32 hex>'`; with
  `log_statement=none` only failed statements would be logged, so this is a weaker check than the statement capture.
- **pg_authid (c):** not readable by `ff_scratchadm`. With `SCRATCH_KFA_HOLD_FILE` set, the self-test writes the role
  name into that file while signed in and waits (≤ 60 s) for it to be deleted; the verify script then reads
  `left(rolpassword, 14)` and shape booleans as `postgres` (read-only, prefix only). Limitation: the server stores a
  SCRAM verifier even for cleartext input (`password_encryption=scram-sha-256`), so (c) cannot detect cleartext in
  transit; (b) does.

- **SSE2 review 04dba71d [steering 13:21] (PROVISIONAL GO on 1b9f5bd; snapshot `/tmp/sse-llc1/RANGE-9771ad3-1b9f5bd-REVIEW.04dba71d.snapshot.md`):**
  - KF-A, the same three places SSE2 checks: after every `PASSWORD` only `SCRAM-SHA-256$4096:` may follow
    (`MaintSql.PwNotVerifier`). The self-test captures every maintenance statement of the whole run (2 with `PASSWORD`:
    the KF-A role and the session role; neither test password in any statement), samples `pg_stat_activity` during
    both `CREATE ROLE`s, and the verify script greps the server log. On the shared cluster `log_statement=none`, so the
    p3q script also runs the self-test once on a private throwaway PG17 cluster with `log_statement=all` (as SSE2 did).
  - Low: every `--scratch-*` mode validates the admin target first; a refused connection-string key (Options, Passfile,
    SSL Mode, Target Session Attributes, …), a non-loopback host, a bad port / maintenance DB or an unset variable exits 2
    with `FATAL scratch: refuse: …` (the key is named, never the value), no unhandled exception (was rc 134).
- **Scratch lock per kit DB [TLD 2026-11-04 13:15 #5]:** the single-runner advisory lock is taken on the kit target DB
  (`CONNECTION_STRING`), not cluster-wide, so runs on different kit DBs (e.g. `ff_sse` and an `ff_kit_*` DB) do not
  block each other. Two runs on different kit DBs share the one `ff_scratchadm` but each uses its own runid names.
  TLD 1:54 PM: no cluster-wide lock (ruled); per kit DB stays.
- **Lock target refusal [TLD 2026-11-04 13:54 item 1]:** a lock target DB outside `ff_sse`, `ff_sse2`, `ff_kit_*`
  (`ff_kit_` + 1–20 of `[a-z0-9_]`) in the main run, `--scratch-selftest` or `--scratch-sweep` exits **2** with
  `FATAL scratch-lock: refuse: the scratch single-runner lock is taken on the kit target DB only (ff_sse, ff_sse2 or ff_kit_*)`
  (`ScratchLock.Refusal`, checked before any connection; the name and other connection-string values are not echoed),
  no unhandled exception (was rc 134). The main run can reach it with `AUTH_CHECKS_DB=ff_kit_<21–45 chars>` (its own guard
  allows up to 45).

## SSE2 review 09e31f68 (range 6a71fbf..9771ad3), folded into the TLD 12:33 amend

- **Single runner [M3; covers L3 / L4].** The main run, `--scratch-selftest` and `--scratch-sweep` first take
  `pg_try_advisory_lock(7378711910451082339)` (`ScratchLock.Key` = `0x6666736372617463`, ASCII "ffscratc") as a session
  lock on the **kit target DB** connection (`CONNECTION_STRING`; only `ff_sse`, `ff_sse2` or `ff_kit_*` are accepted), never
  on the maintenance connection, so the allowlist above is unchanged. The lock is held until the process exits. A second
  concurrent run prints `FATAL scratch-lock: another scratch run holds the lock (…)` and exits **5** before it writes
  anything. Helper modes (`--scratch-crash`, `--scratch-hold`, `--scratch-drop`, decoy / look-alike / cleanup) do not take
  it. Limitation: advisory locks are per database, so runs against two **different** kit DBs (for example `ff_sse` and an
  `ff_kit_*` DB) do not exclude each other. Under the lock no other locking run can be inside another run's CREATE → COMMENT
  window or its sweep (L3), and fixture collisions between parallel main runs on the same kit DB cannot happen (L4).
- **Bad runids [L1].** A marked DB or role that `ff_scratchadm` owns, whose runid is missing, invalid (e.g. month 13) or
  more than 5 min in the future (`ScratchDb.FutureSkew`), is reported by name: `scratch-sweep: BAD RUNID …` and
  `FATAL scratch-sweep: marked ff_scratch_* object(s) with a missing, invalid or future runid, NOT dropped: …`. The main
  run's scratch setup then fails (N3 / N1 / Q2 FAIL) and `--scratch-sweep` exits 3. It is never kept silently and never
  dropped automatically; clean it up by exact name (`--scratch-drop <db>`) once you know who made it.
- **`--scratch-drop` [M2, L2].** Refuses with exit 2 and a clear message (no crash) when the marked DB has connections
  (pre-check on `pg_stat_activity`; a race at `DROP DATABASE` that raises SQLSTATE 55006 is caught and refused the same
  way). It prints `nothing to drop` (exit 0) when neither the DB nor a marked role existed, and `dropped <name> (N
  object(s))` only when something was dropped.
- **Admin connection-string keys [Info].** `SCRATCH_ADMIN_CONNECTION_STRING` may contain only Host / Server, Port,
  Database, Username (or a synonym), Password and Pooling. Any other key (`Passfile`, `Options`, `Target Session
  Attributes`, `SSL Mode`, …) is refused before any connection, naming the key only, never a value.
- **No "own DB" concept in the CLI [Info].** `--scratch-drop <name>` drops any **marked**, `ff_scratchadm`-owned,
  unconnected scratch DB (and its marked roles) by name, not only one made by the same process. The per-run finally drops
  only the run's own DB; the CLI modes are verify-script tools.
- **Self-test env overrides [F4].** Every `SCRATCH_PG_HOST` / `SCRATCH_PG_PORT` / `SCRATCH_MAINT_DB` override in the
  self-test restores the previous value (not null). A check compares all three with the start-of-test snapshot after each
  of the 16 blocks, so the self-test also passes when they are set explicitly (e.g. `SCRATCH_MAINT_DB=postgres
  SCRATCH_PG_PORT=5432`).
