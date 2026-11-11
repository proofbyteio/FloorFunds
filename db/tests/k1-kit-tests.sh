#!/usr/bin/env bash
# K1/K5 tests for db/setup.sh AND db/setup.ps1 (SSE2 review of d787f76; TL 8:32). TEST-ONLY, engineers' box.
#  A. stub runs: the superuser psql is db/tests/stub-psql.sh (logs every script, answers the preflight with canned
#     findings), so the real postgres, template*, ff_sse and ff_sse2 are named but never touched. Asserts the exit
#     code, the message, and that nothing but the read-only preflight was sent (or nothing at all).
#  B. live runs on scratch $P* (default ff_kit_kf*) DBs/roles created here (unmarked ones by hand, for the purpose) and all
#     dropped at the end. Asserts refusals change nothing (oid, attributes, password hash, comment).
# usage: db/tests/k1-kit-tests.sh   (needs sudo -u postgres, pwsh 7, and for the DB checks a built src/Auth.Data.Checks)
# FF_PG_LOG: server log of the cluster under test. The KF-A checks read it from a byte
# offset (sudo -u postgres tail); they never write it. Default is the Debian PostgreSQL 17
# main log. Set FF_PG_LOG when the cluster under test is not that file, or the four KF-A
# log checks read the wrong log and fail. Auth.Data inside checks() also needs
# SCRATCH_ADMIN_CONNECTION_STRING (and SCRATCH_PG_PORT when the scratch cluster is not 5432);
# without them the scratch probes fail and the summary is 169 passed, 5 failed.
set -uo pipefail
# Every scratch DB/role name below is built from P, and cleanup matches PL (P with _ escaped for LIKE), so a renamed
# copy only needs P changed and still cleans up everything it made (SSE2 KF5 suggestion).
P="${KIT_TEST_PREFIX:-ff_kit_kf}"
[[ "$P" =~ ^ff_kit_[a-z0-9_]{1,20}$ ]] || { echo "KIT_TEST_PREFIX must match ^ff_kit_[a-z0-9_]{1,20}\$" >&2; exit 2; }
PL="${P//_/\\_}"
NL=$'\n'
DBDIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$DBDIR/../src"
T="$(mktemp -d /tmp/kit-k1.XXXXXX)"; chmod 0711 "$T"
mkdir -p "$T/bin"; cp "$DBDIR/tests/stub-psql.sh" "$T/bin/psql"; chmod 0755 "$T/bin/psql"
export KIT_STUB_LOG="$T/stub.log" KIT_STUB_PREFLIGHT="$T/preflight.out"
pass=0; fail=0
ok() { if [[ "$2" == 1 ]]; then pass=$((pass+1)); echo "PASS $1"; else fail=$((fail+1)); echo "FAIL $1${3:+ :: $3}"; fi; }
t() { "$@" >/dev/null 2>&1 && echo 1 || echo 0; }
# a well-formed (unusable) SCRAM verifier for calling kit/role.sql directly (KF-A: it accepts only verifiers)
DUMMYV='SCRAM-SHA-256$4096:AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA='
PW() { od -An -N12 -tx1 /dev/urandom | tr -d ' \n'; }
q() { sudo -u postgres psql -X -tA -v ON_ERROR_STOP=1 -d postgres -c "$1"; }

# sh <-> ps1 argument mapping
ps1args() { local out=(); while [[ $# -gt 0 ]]; do case "$1" in
  --db) out+=(-Db "$2"); shift 2 ;; --role) out+=(-Role "$2"); shift 2 ;; --force) out+=(-Force); shift ;;
  --drop) out+=(-Drop); shift ;; --no-migrate) out+=(-NoMigrate); shift ;; --seed) out+=(-Seed); shift ;;
  *) echo "unmapped $1" >&2; exit 9 ;; esac; done; printf '%s\n' "${out[@]}"; }

# ---------- A. stub runs ----------
# stub_case NAME RC CALLS MSG_REGEX PREFLIGHT_LINES -- sh-args...   (runs setup.sh and setup.ps1)
stub_case() {
  local name="$1" rc_want="$2" calls_want="$3" msg="$4" pre="$5"; shift 6
  printf '%s' "$pre" >"$KIT_STUB_PREFLIGHT"
  for which in sh ps1; do
    : >"$KIT_STUB_LOG"
    if [[ $which == sh ]]; then
      bash "$DBDIR/setup.sh" --superuser-cmd "$T/bin/psql" "$@" >"$T/out" 2>&1; rc=$?
    else
      mapfile -t pa < <(ps1args "$@")
      PATH="$T/bin:$PATH" pwsh -NoProfile -NonInteractive -File "$DBDIR/setup.ps1" -AdminHost '' "${pa[@]}" >"$T/out" 2>&1; rc=$?
    fi
    calls=$(grep -c "^=== call" "$KIT_STUB_LOG")
    ok "[$which] $name -> rc $rc_want, $calls_want superuser psql call(s)${calls_want:+ (preflight only when 1)}, message /$msg/" \
      "$([[ $rc == "$rc_want" && $calls == "$calls_want" ]] && grep -Eq -- "$msg" "$T/out" && echo 1 || echo 0)" "rc=$rc calls=$calls out=$(tr '\n' ' ' <"$T/out" | cut -c1-300)"
    if [[ $calls == 1 ]]; then
      ok "[$which] $name: the one call was the read-only preflight (no CREATE/ALTER/DROP/COMMENT/GRANT/REVOKE)" \
        "$(grep -q 'floorfunds-kit preflight' "$KIT_STUB_LOG" && grep -q 'BEGIN TRANSACTION READ ONLY' "$KIT_STUB_LOG" && ! grep -Eq '\\gexec|^COMMENT|^GRANT|^REVOKE' "$KIT_STUB_LOG" && echo 1 || echo 0)"
    fi
  done
}
export FF_DB_PASSWORD="$(PW)" FF_ADMIN_API_PASSWORD="$(PW)" FF_LOGIN_PASSWORD="$(PW)" FF_ISSUER_PASSWORD="$(PW)"
echo "=== A. stub runs (no database is touched) ==="
stub_case "--db postgres --force" 5 0 "postgres is a system database" "" -- --db postgres --role ${P}z --force --no-migrate
stub_case "--db postgres --role postgres --force --drop" 5 0 "system database" "" -- --db postgres --role postgres --force --drop --no-migrate
stub_case "--db template0 --force --drop" 5 0 "template0 is a system database" "" -- --db template0 --role ${P}z --force --drop --no-migrate
stub_case "--db template1 --force" 5 0 "template1 is a system database" "" -- --db template1 --role ${P}z --force --no-migrate
stub_case "--role postgres --force" 5 0 "role postgres is the bootstrap superuser" "" -- --db ${P}z --role postgres --force --no-migrate
stub_case "--role pg_x (reserved prefix)" 5 0 "pg_ prefix is reserved" "" -- --db ${P}z --role pg_x --force --no-migrate
stub_case "--drop without --force" 2 0 "drop needs .?-?-?[Ff]orce|Drop needs -Force" "" -- --db ${P}z --role ${P}z --drop --no-migrate
# What the real catalog would report for the engineers' DBs (they were made by ff-db.sh, not the kit: no marker).
FF_SSE_PRE=$'REFUSE role ff_sse exists but was not created by the kit (no floorfunds-kit marker)\nREFUSE role ff_sse_admin_api exists but was not created by the kit (no floorfunds-kit marker)\nREFUSE role ff_sse_login exists but was not created by the kit (no floorfunds-kit marker)\nREFUSE database ff_sse exists but was not created by the kit (no floorfunds-kit marker)\nEXISTS role ff_sse\nEXISTS role ff_sse_admin_api\nEXISTS role ff_sse_login\nEXISTS database ff_sse\n'
stub_case "--db ff_sse --role ff_sse --force (unmarked) still refused" 5 1 "Refused: database ff_sse exists but was not created by the kit" "$FF_SSE_PRE" -- --db ff_sse --role ff_sse --force --no-migrate
stub_case "--db ff_sse --role ff_sse --force --drop (unmarked) still refused" 5 1 "never overrides" "$FF_SSE_PRE" -- --db ff_sse --role ff_sse --force --drop --no-migrate
stub_case "--db ff_sse2 --role ff_sse2 --force --drop (unmarked) still refused" 5 1 "Refused: role ff_sse2 exists but was not created" "${FF_SSE_PRE//ff_sse/ff_sse2}" -- --db ff_sse2 --role ff_sse2 --force --drop --no-migrate
stub_case "superuser role, even if marked, --force" 5 1 "Refused: role ${P}z is a superuser" "REFUSE role ${P}z is a superuser${NL}EXISTS role ${P}z${NL}" -- --db ${P}z --role ${P}z --force --no-migrate
stub_case "existing kit objects without --force" 5 1 "re-run with .?-?-?[Ff]orce" "EXISTS role ${P}z${NL}EXISTS database ${P}z${NL}" -- --db ${P}z --role ${P}z --no-migrate
# --force on the kit's own objects proceeds: 1 preflight + 4 roles + 1 DB, every mutating script is the guarded kit SQL.
stub_case "existing kit objects with --force proceeds (6 calls)" 0 6 "Done" "EXISTS role ${P}z${NL}EXISTS database ${P}z${NL}" -- --db ${P}z --role ${P}z --force --no-migrate
ok "stub: the --force run sent only guarded kit SQL (role.sql x4 marker guard, database.sql drop + create guards)" \
  "$([[ $(grep -c 'refusing to alter a role the kit did not create' "$KIT_STUB_LOG") == 4 && $(grep -c 'refusing to touch a database the kit did not create' "$KIT_STUB_LOG") == 2 ]] && echo 1 || echo 0)"
# KF3: no end marker = fail closed, even with --force and nothing else in the output.
export KIT_STUB_NO_END=1
stub_case "KF3 empty preflight output (no end marker) with --force -> refused" 5 1 "preflight returned no end marker" "" -- --db ${P}z --role ${P}z --force --no-migrate
stub_case "KF3 truncated preflight output (EXISTS lines, no end marker) with --force -> refused" 5 1 "preflight returned no end marker" "EXISTS role ${P}z${NL}EXISTS database ${P}z${NL}" -- --db ${P}z --role ${P}z --force --no-migrate
# TLD 9:19: a non-superuser admin connection is refused by the preflight (script handling, stub).
stub_case "TLD 9:19 non-superuser admin connection -> refused" 5 1 "Refused: the admin connection must be a PostgreSQL superuser .e.g. postgres.; ${P}adm is not." "REFUSE the admin connection must be a PostgreSQL superuser (e.g. postgres); ${P}adm is not.${NL}END floorfunds-kit-preflight${NL}" -- --db ${P}z --role ${P}z --force --no-migrate
# KF-E: an END line ending in CR is not the end marker, in both scripts.
CR=$'\r'
stub_case "KF-E end marker ending in CR (CRLF) -> refused" 5 1 "preflight returned no end marker" "END floorfunds-kit-preflight${CR}${NL}" -- --db ${P}z --role ${P}z --force --no-migrate
stub_case "KF-E end marker ending in CR (no LF) -> refused" 5 1 "preflight returned no end marker" "END floorfunds-kit-preflight${CR}" -- --db ${P}z --role ${P}z --force --no-migrate
# KF-A: only printable ASCII passwords (SASLprep is then the identity for the client-side SCRAM verifier).
FF_DB_PASSWORD="caf$(printf '\xc3\xa9')-$(PW)" stub_case "KF-A non-ASCII owner password refused" 2 0 "must be printable ASCII" "" -- --db ${P}z --role ${P}z --no-migrate
FF_LOGIN_PASSWORD="tab$(printf '\t')-$(PW)" stub_case "KF-A control-character login password refused" 2 0 "must be printable ASCII" "" -- --db ${P}z --role ${P}z --no-migrate
unset KIT_STUB_NO_END
stub_case "KF1 preflight membership finding with --force -> refused" 5 1 "Refused: role ${P}z is a member of role pg_read_all_data" "REFUSE role ${P}z is a member of role pg_read_all_data (a kit role may belong to no role outside the kit)${NL}EXISTS role ${P}z${NL}" -- --db ${P}z --role ${P}z --force --no-migrate
OLDPW="$FF_DB_PASSWORD"
for bad in $'nl\nbreak' $'cr\rbreak'; do
  FF_DB_PASSWORD="$bad" stub_case "K5 owner password with ${bad:0:2} refused" 2 0 "contains a newline or carriage return" "" -- --db ${P}z --role ${P}z --no-migrate
  FF_LOGIN_PASSWORD="$bad" stub_case "K5 login password with ${bad:0:2} refused" 2 0 "contains a newline or carriage return" "" -- --db ${P}z --role ${P}z --no-migrate
done

# ---------- B. live runs on scratch ${P}* objects ----------
echo; echo "=== B. live runs (scratch ${P}* only) ==="
snap() { # fingerprint of everything a refusal must leave alone
  q "SELECT md5(coalesce(string_agg(x, '|' ORDER BY x), '')) FROM (
       SELECT 'r:'||a.rolname||':'||a.oid||':'||a.rolsuper||a.rolcanlogin||a.rolcreatedb||a.rolbypassrls||':'||md5(coalesce(a.rolpassword,''))||':'||coalesce(shobj_description(a.oid,'pg_authid'),'-') x
         FROM pg_authid a WHERE a.rolname LIKE '$PL%'
       UNION ALL SELECT 'd:'||datname||':'||oid||':'||pg_get_userbyid(datdba)||':'||coalesce(datacl::text,'')||':'||coalesce(shobj_description(oid,'pg_database'),'-')
         FROM pg_database WHERE datname LIKE '$PL%'
       UNION ALL SELECT 'm:'||m.member::regrole::text||'>'||m.roleid::regrole::text FROM pg_auth_members m
         JOIN pg_roles a ON a.oid = m.member WHERE a.rolname LIKE '$PL%'
       UNION ALL SELECT 'a:'||rolname||':'||rolcreaterole||rolreplication FROM pg_roles WHERE rolname LIKE '$PL%') s"; }
marked() { q "SELECT count(*) FROM (SELECT oid FROM pg_roles WHERE rolname = ANY(string_to_array('$1', ',')) AND shobj_description(oid,'pg_authid')='floorfunds-kit'
                UNION ALL SELECT oid FROM pg_database WHERE datname='$2' AND shobj_description(oid,'pg_database')='floorfunds-kit') s"; }
dboid() { q "SELECT coalesce((SELECT oid::text FROM pg_database WHERE datname='$1'), 'none')"; }
pwhash() { q "SELECT md5(coalesce(rolpassword,'')) FROM pg_authid WHERE rolname='$1'"; }
SH() { bash "$DBDIR/setup.sh" "$@" >"$T/out" 2>&1; echo $?; }
PS() { sudo -u postgres --preserve-env=FF_DB_PASSWORD,FF_ADMIN_API_PASSWORD,FF_LOGIN_PASSWORD,FF_ISSUER_PASSWORD pwsh -NoProfile -NonInteractive -File "$DBDIR/setup.ps1" -AdminHost '' "$@" >"$T/out" 2>&1; echo $?; }
checks() { # checks DB OWNER -> last line of the DB checks (passwords from the current env)
  # Auth.Data's scratch probes read SCRATCH_ADMIN_CONNECTION_STRING from the process environment.
  # A shell assignment that was not exported never reaches dotnet, and the run then reports
  # 169 passed, 5 failed (N3 twice, N1 twice, Q2) at Auth.Data.Checks/Program.cs scratch setup.
  if [[ -z "${SCRATCH_ADMIN_CONNECTION_STRING:-}" ]]; then
    echo "scratch env missing: export SCRATCH_ADMIN_CONNECTION_STRING before k1 (else N3 x2, N1 x2, Q2 fail: 169 passed, 5 failed)"
    return 1
  fi
  export SCRATCH_ADMIN_CONNECTION_STRING
  ( cd "$SRC" && export PATH=/home/box/.dotnet:$PATH DOTNET_ROOT=/home/box/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
      CONNECTION_STRING="Host=127.0.0.1;Port=5432;Database=$1;Username=$2;Password=$FF_DB_PASSWORD" \
      ADMIN_API_CONNECTION_STRING="Host=127.0.0.1;Port=5432;Database=$1;Username=$1_admin_api;Password=$FF_ADMIN_API_PASSWORD" \
      LOGIN_CONNECTION_STRING="Host=127.0.0.1;Port=5432;Database=$1;Username=$1_login;Password=$FF_LOGIN_PASSWORD" \
      ISSUER_CONNECTION_STRING="Host=127.0.0.1;Port=5432;Database=$1;Username=$1_issuer;Password=$FF_ISSUER_PASSWORD" \
      AUTH_CHECKS_DB="$1" && dotnet run --no-build --project Auth.Data.Checks -- Login.StandIn/fixtures.json 2>&1 | tail -1 ); }
cleanup() {
  for d in $(q "SELECT datname FROM pg_database WHERE datname LIKE '$PL%'"); do q "DROP DATABASE \"$d\" WITH (FORCE)" >/dev/null; done
  for r in $(q "SELECT rolname FROM pg_roles WHERE rolname LIKE '$PL%'"); do q "DROP ROLE \"$r\"" >/dev/null; done
}
cleanup
export FF_DB_PASSWORD="$(PW)" FF_ADMIN_API_PASSWORD="$(PW)" FF_LOGIN_PASSWORD="$(PW)" FF_ISSUER_PASSWORD="$(PW)"

# B1 setup.sh fresh, --role != --db (K2)
rc=$(SH --db ${P}a --role ${P}a_owner --seed)
ok "[sh] fresh ${P}a (--role ${P}a_owner) -> rc 0; DB + 4 roles carry the kit marker" "$([[ $rc == 0 && $(marked ${P}a_owner,${P}a_admin_api,${P}a_login,${P}a_issuer ${P}a) == 5 ]] && echo 1 || echo 0)" "rc=$rc $(tail -3 "$T/out" | tr '\n' ' ')"
ok "[sh] marked roles are exactly owner, admin_api, login, issuer" "$([[ $(q "SELECT string_agg(rolname, ',' ORDER BY rolname) FROM pg_roles WHERE rolname LIKE '${P}a%' AND shobj_description(oid,'pg_authid')='floorfunds-kit'") == "${P}a_admin_api,${P}a_issuer,${P}a_login,${P}a_owner" ]] && echo 1 || echo 0)"
r=$(checks ${P}a ${P}a_owner); ok "[sh] DB checks on ${P}a with --role != --db (K2) -> $r" "$([[ $r == '181 passed, 0 failed' ]] && echo 1 || echo 0)"
s0=$(snap); rc=$(SH --db ${P}a --role ${P}a_owner --seed)
ok "[sh] re-run without --force -> rc 5, nothing changed" "$([[ $rc == 5 && $(snap) == "$s0" ]] && grep -q 're-run with --force' "$T/out" && echo 1 || echo 0)" "rc=$rc"
rc=$(SH --db ${P}a --role ${P}a_owner --drop)
ok "[sh] --drop without --force -> rc 2, nothing changed" "$([[ $rc == 2 && $(snap) == "$s0" ]] && echo 1 || echo 0)" "rc=$rc"
oid0=$(dboid ${P}a); h0=$(pwhash ${P}a_owner)
TRICKY="k1'q\\b-$(PW)"; rc=$(FF_DB_PASSWORD="$TRICKY" SH --db ${P}a --role ${P}a_owner --force)
ok "[sh] --force on the kit's own objects -> rc 0, same DB oid, owner password re-set (quote+backslash password logs in)" \
  "$([[ $rc == 0 && $(dboid ${P}a) == "$oid0" && $(pwhash ${P}a_owner) != "$h0" ]] && PGPASSWORD="$TRICKY" psql -X -tA -h 127.0.0.1 -U ${P}a_owner -d ${P}a -c 'select 1' >/dev/null 2>&1 && echo 1 || echo 0)" "rc=$rc"
rc=$(SH --db ${P}a --role ${P}a_owner --force --drop --seed)
ok "[sh] --force --drop -> rc 0, DB recreated (new oid), still marked" "$([[ $rc == 0 && $(dboid ${P}a) != "$oid0" && $(marked x ${P}a) == 1 ]] && echo 1 || echo 0)" "rc=$rc"
r=$(checks ${P}a ${P}a_owner); ok "[sh] DB checks after --force --drop -> $r" "$([[ $r == '181 passed, 0 failed' ]] && echo 1 || echo 0)"

# B2 setup.ps1 fresh, --role == --db
rc=$(PS -Db ${P}b -Role ${P}b -Seed)
ok "[ps1] fresh ${P}b -> rc 0; DB + 4 roles carry the kit marker" "$([[ $rc == 0 && $(marked ${P}b,${P}b_admin_api,${P}b_login,${P}b_issuer ${P}b) == 5 ]] && echo 1 || echo 0)" "rc=$rc $(tail -3 "$T/out" | tr '\n' ' ')"
ok "[ps1] marked roles are exactly owner, admin_api, login, issuer" "$([[ $(q "SELECT string_agg(rolname, ',' ORDER BY rolname) FROM pg_roles WHERE rolname LIKE '${P}b%' AND shobj_description(oid,'pg_authid')='floorfunds-kit'") == "${P}b,${P}b_admin_api,${P}b_issuer,${P}b_login" ]] && echo 1 || echo 0)"
r=$(checks ${P}b ${P}b); ok "[ps1] DB checks on ${P}b -> $r" "$([[ $r == '181 passed, 0 failed' ]] && echo 1 || echo 0)"
s0=$(snap); rc=$(PS -Db ${P}b -Role ${P}b)
ok "[ps1] re-run without -Force -> rc 5, nothing changed" "$([[ $rc == 5 && $(snap) == "$s0" ]] && grep -q 're-run with -Force' "$T/out" && echo 1 || echo 0)" "rc=$rc"
rc=$(PS -Db ${P}b -Role ${P}b -Drop)
ok "[ps1] -Drop without -Force -> rc 2, nothing changed" "$([[ $rc == 2 && $(snap) == "$s0" ]] && echo 1 || echo 0)" "rc=$rc"
oid0=$(dboid ${P}b); rc=$(PS -Db ${P}b -Role ${P}b -Force -Drop -Seed)
ok "[ps1] -Force -Drop -> rc 0, DB recreated (new oid), still marked" "$([[ $rc == 0 && $(dboid ${P}b) != "$oid0" && $(marked x ${P}b) == 1 ]] && echo 1 || echo 0)" "rc=$rc"
r=$(checks ${P}b ${P}b); ok "[ps1] DB checks after -Force -Drop -> $r" "$([[ $r == '181 passed, 0 failed' ]] && echo 1 || echo 0)"

# B3 hand-made UNMARKED scratch objects (stand-ins for "someone else's DB/role") + a marked superuser
q "CREATE ROLE ${P}u NOLOGIN" >/dev/null; q "CREATE DATABASE ${P}u OWNER ${P}u" >/dev/null
q "CREATE ROLE ${P}r NOLOGIN" >/dev/null
q "CREATE ROLE ${P}d_login NOLOGIN" >/dev/null
q "CREATE ROLE ${P}s NOLOGIN SUPERUSER" >/dev/null; q "COMMENT ON ROLE ${P}s IS 'floorfunds-kit'" >/dev/null
live_refuse() { # NAME MSG -- sh-args
  local name="$1" msg="$2"; shift 3
  for which in sh ps1; do
    s0=$(snap)
    if [[ $which == sh ]]; then rc=$(SH "$@"); else mapfile -t pa < <(ps1args "$@"); rc=$(PS "${pa[@]}"); fi
    ok "[$which] $name -> rc 5, /$msg/, nothing changed" "$([[ $rc == 5 && $(snap) == "$s0" ]] && grep -Eq -- "$msg" "$T/out" && echo 1 || echo 0)" "rc=$rc out=$(tr '\n' ' ' <"$T/out" | cut -c1-300)"
  done
}
live_refuse "unmarked DB+role ${P}u with --force --drop" "database ${P}u exists but was not created by the kit" -- --db ${P}u --role ${P}u --force --drop --no-migrate
live_refuse "unmarked --role ${P}r with --force (new DB not created)" "role ${P}r exists but was not created by the kit" -- --db ${P}n --role ${P}r --force --no-migrate
live_refuse "unmarked app role ${P}d_login with --force" "role ${P}d_login exists but was not created by the kit" -- --db ${P}d --role ${P}d_o --force --no-migrate
live_refuse "marked SUPERUSER ${P}s with --force" "role ${P}s is a superuser" -- --db ${P}m --role ${P}s --force --no-migrate
live_refuse "kit role ${P}a_owner owning another DB with --force" "role ${P}a_owner owns database ${P}a" -- --db ${P}c --role ${P}a_owner --force --no-migrate
live_refuse "kit DB ${P}a owned by another kit role with --force --drop" "database ${P}a is owned by ${P}a_owner, not by --role ${P}b" -- --db ${P}a --role ${P}b --force --drop --no-migrate
ok "live: no DB ${P}n/k1d/k1m/k1c and no role ${P}d_o were created by the refused runs" \
  "$([[ $(q "SELECT count(*) FROM pg_database WHERE datname IN ('${P}n','${P}d','${P}m','${P}c')") == 0 && $(q "SELECT count(*) FROM pg_roles WHERE rolname='${P}d_o'") == 0 ]] && echo 1 || echo 0)"
ok "live: ${P}s is still SUPERUSER NOLOGIN; ${P}u/${P}r/${P}d_login still NOLOGIN and unmarked" \
  "$([[ $(q "SELECT count(*) FROM pg_roles WHERE (rolname='${P}s' AND rolsuper AND NOT rolcanlogin) OR (rolname IN ('${P}u','${P}r','${P}d_login') AND NOT rolcanlogin AND shobj_description(oid,'pg_authid') IS NULL)") == 4 ]] && echo 1 || echo 0)"

# B5 KF1: privileged memberships / attributes on the kit's own (marked) roles; --force never overrides.
q "CREATE ROLE ${P}byp NOLOGIN BYPASSRLS" >/dev/null; q "CREATE ROLE ${P}cr NOLOGIN CREATEROLE" >/dev/null
q "CREATE ROLE ${P}cdb NOLOGIN CREATEDB" >/dev/null; q "CREATE ROLE ${P}plain NOLOGIN" >/dev/null
q "CREATE ROLE ${P}msu NOLOGIN SUPERUSER" >/dev/null; q "COMMENT ON ROLE ${P}msu IS 'floorfunds-kit'" >/dev/null
q "CREATE ROLE ${P}mk NOLOGIN" >/dev/null; q "COMMENT ON ROLE ${P}mk IS 'floorfunds-kit'" >/dev/null   # marked, but not this run's
A="--db ${P}a --role ${P}a_owner --force --no-migrate"
kf1_member() { # GRANTED MEMBER MSG
  q "GRANT \"$1\" TO \"$2\"" >/dev/null
  # shellcheck disable=SC2086
  live_refuse "KF1 $2 member of $1 with --force" "$3" -- $A
  ok "KF1 membership $2 -> $1 still present after the refused runs (refusal changed nothing)" "$([[ $(q "SELECT count(*) FROM pg_auth_members WHERE member='$2'::regrole AND roleid='$1'::regrole") == 1 ]] && echo 1 || echo 0)"
  q "REVOKE \"$1\" FROM \"$2\"" >/dev/null
}
kf1_member pg_read_all_data ${P}a_owner "role ${P}a_owner is a member of role pg_read_all_data"
kf1_member pg_write_server_files ${P}a_admin_api "role ${P}a_admin_api is a member of role pg_write_server_files"
kf1_member pg_monitor ${P}a_login "role ${P}a_login is a member of role pg_monitor"
kf1_member ${P}byp ${P}a_login "role ${P}a_login is a member of role ${P}byp"
kf1_member ${P}cr ${P}a_owner "role ${P}a_owner is a member of role ${P}cr"
kf1_member ${P}cdb ${P}a_owner "role ${P}a_owner is a member of role ${P}cdb"
kf1_member ${P}msu ${P}a_owner "role ${P}a_owner is a member of role ${P}msu"
kf1_member ${P}plain ${P}a_admin_api "role ${P}a_admin_api is a member of role ${P}plain"
kf1_member ${P}mk ${P}a_owner "role ${P}a_owner is a member of role ${P}mk"
# transitive: owner -> (this run's kit role) admin_api -> pg_read_all_data
q "GRANT ${P}a_admin_api TO ${P}a_owner" >/dev/null
kf1_member pg_read_all_data ${P}a_admin_api "role ${P}a_owner is a member of role pg_read_all_data"
q "REVOKE ${P}a_admin_api FROM ${P}a_owner" >/dev/null
kf1_attr() { # ATTR ROLE
  q "ALTER ROLE \"$2\" $1" >/dev/null
  # shellcheck disable=SC2086
  live_refuse "KF1 kit role $2 with $1 and --force" "role $2 has $1" -- $A
  q "ALTER ROLE \"$2\" NO$1" >/dev/null
}
kf1_attr BYPASSRLS ${P}a_owner; kf1_attr CREATEROLE ${P}a_admin_api; kf1_attr REPLICATION ${P}a_login; kf1_attr CREATEDB ${P}a_owner
oid0=$(dboid ${P}a); rc=$(SH $A)
ok "[sh] KF1 after every grant/attribute was reverted: --force on ${P}a -> rc 0, same DB oid" "$([[ $rc == 0 && $(dboid ${P}a) == "$oid0" ]] && echo 1 || echo 0)" "rc=$rc $(tail -3 "$T/out" | tr '\n' ' ')"
mapfile -t pa < <(ps1args $A); rc=$(PS "${pa[@]}")
ok "[ps1] KF1 after every grant/attribute was reverted: -Force on ${P}a -> rc 0, same DB oid" "$([[ $rc == 0 && $(dboid ${P}a) == "$oid0" ]] && echo 1 || echo 0)" "rc=$rc $(tail -3 "$T/out" | tr '\n' ' ')"

# B4 defence in depth: the mutating kit SQL itself refuses unmarked objects (if the preflight were bypassed / raced)
s0=$(snap)
out=$( { printf "\\\\set r '${P}u'\n\\\\set p '$DUMMYV'\n"; cat "$DBDIR/kit/role.sql"; } | sudo -u postgres psql -X -q -v ON_ERROR_STOP=1 -d postgres -v db=${P}a -v role=${P}a_owner -f - 2>&1); rc=$?
ok "kit/role.sql directly on unmarked ${P}u -> error, role unchanged" "$([[ $rc != 0 && $out == *'refusing to alter a role the kit did not create'* && $(snap) == "$s0" ]] && echo 1 || echo 0)" "rc=$rc"
out=$( { printf "\\\\set r '${P}s'\n\\\\set p '$DUMMYV'\n"; cat "$DBDIR/kit/role.sql"; } | sudo -u postgres psql -X -q -v ON_ERROR_STOP=1 -d postgres -v db=${P}a -v role=${P}a_owner -f - 2>&1); rc=$?
ok "kit/role.sql directly on marked superuser ${P}s -> error, still superuser" "$([[ $rc != 0 && $out == *'refusing to alter'* && $(snap) == "$s0" ]] && echo 1 || echo 0)" "rc=$rc"
out=$(sudo -u postgres psql -X -q -v ON_ERROR_STOP=1 -d postgres -v db=${P}u -v role=${P}u -v drop=1 -f - <"$DBDIR/kit/database.sql" 2>&1); rc=$?
ok "kit/database.sql drop=1 directly on unmarked ${P}u -> error, DB not dropped/regranted" "$([[ $rc != 0 && $out == *'refusing to touch a database the kit did not create'* && $(snap) == "$s0" ]] && echo 1 || echo 0)" "rc=$rc"

# KF2: the DO-block guards themselves (preflight bypassed), on the kit's own marked roles.
roledirect() { { printf "\\\\set r '%s'\n\\\\set p '%s'\n" "$1" "${2:-$DUMMYV}"; cat "$DBDIR/kit/role.sql"; } | sudo -u postgres psql -X -q -v ON_ERROR_STOP=1 -d postgres -v db=${P}a -v role=${P}a_owner -f - 2>&1; }
q "GRANT pg_read_all_data TO ${P}a_login" >/dev/null; s0=$(snap); h0=$(pwhash ${P}a_login)
out=$(roledirect ${P}a_login); rc=$?
ok "kit/role.sql DO block directly on marked ${P}a_login with pg_read_all_data -> error, password and membership unchanged" "$([[ $rc != 0 && $out == *'member of role(s) outside the kit: pg_read_all_data'* && $(snap) == "$s0" && $(pwhash ${P}a_login) == "$h0" ]] && echo 1 || echo 0)" "rc=$rc"
q "REVOKE pg_read_all_data FROM ${P}a_login" >/dev/null
q "ALTER ROLE ${P}a_login BYPASSRLS" >/dev/null; s0=$(snap)
out=$(roledirect ${P}a_login); rc=$?
ok "kit/role.sql DO block directly on marked ${P}a_login with BYPASSRLS -> error, BYPASSRLS not silently stripped" "$([[ $rc != 0 && $out == *'BYPASSRLS, CREATEROLE, CREATEDB or REPLICATION'* && $(snap) == "$s0" ]] && echo 1 || echo 0)" "rc=$rc"
q "ALTER ROLE ${P}a_login NOBYPASSRLS" >/dev/null
s0=$(snap); out=$(roledirect ${P}k2new); rc=$?
ok "kit/role.sql DO block creates a new role and its marker together" "$([[ $rc == 0 && $(marked ${P}k2new x) == 1 ]] && echo 1 || echo 0)" "rc=$rc"
q "CREATE DATABASE ${P}sdb OWNER ${P}s" >/dev/null; q "COMMENT ON DATABASE ${P}sdb IS 'floorfunds-kit'" >/dev/null
s0=$(snap)
for d in 0 1; do
  out=$(sudo -u postgres psql -X -q -v ON_ERROR_STOP=1 -d postgres -v db=${P}sdb -v role=${P}s -v drop=$d -f - <"$DBDIR/kit/database.sql" 2>&1); rc=$?
  ok "kit/database.sql DO block (drop=$d) directly on a MARKED DB owned by marked SUPERUSER ${P}s -> error, not dropped/regranted" "$([[ $rc != 0 && $out == *'wrong or superuser owner'* && $(snap) == "$s0" ]] && echo 1 || echo 0)" "rc=$rc"
done
out=$(sudo -u postgres psql -X -q -v ON_ERROR_STOP=1 -d postgres -v db=${P}u -v role=${P}u -v drop=0 -f - <"$DBDIR/kit/database.sql" 2>&1); rc=$?
ok "kit/database.sql DO block (drop=0) directly on unmarked ${P}u -> error, not marked or regranted" "$([[ $rc != 0 && $out == *'refusing to touch a database the kit did not create'* && $(snap) == "$s0" ]] && echo 1 || echo 0)" "rc=$rc"


# ---------- KF-A / KF-B / KF-C (commit after 81affe1) ----------
LOG="${FF_PG_LOG:-/var/log/postgresql/postgresql-17-main.log}"  # read only (sudo -u postgres tail from a byte offset); never edited
logfrom() { sudo -u postgres tail -c +$(( $1 + 1 )) "$LOG"; }
# KF-A 1: the stored password is a SCRAM verifier made client-side, and the kit's passwords log in.
for r in ${P}a_owner ${P}a_admin_api ${P}a_login ${P}a_issuer ${P}b ${P}b_admin_api ${P}b_login ${P}b_issuer; do
  ok "KF-A $r: rolpassword is SCRAM-SHA-256\$4096" "$([[ $(q "SELECT rolpassword LIKE 'SCRAM-SHA-256\$4096:%' FROM pg_authid WHERE rolname='$r'") == t ]] && echo 1 || echo 0)"
done
for w in sh ps1; do
  pw="kfa-$(PW)"
  if [[ $w == sh ]]; then rc=$(FF_LOGIN_PASSWORD="$pw" SH --db ${P}a --role ${P}a_owner --force); else rc=$(FF_LOGIN_PASSWORD="$pw" PS -Db ${P}a -Role ${P}a_owner -Force); fi
  ok "KF-A [$w] client-side verifier: ${P}a_login logs in with the new password, not with a wrong one" \
    "$([[ $rc == 0 ]] && PGPASSWORD="$pw" psql -X -tA -h 127.0.0.1 -U ${P}a_login -d ${P}a -c 'select 1' >/dev/null 2>&1 && ! PGPASSWORD="x$pw" psql -X -tA -h 127.0.0.1 -U ${P}a_login -d ${P}a -c 'select 1' >/dev/null 2>&1 && echo 1 || echo 0)" "rc=$rc"
done
r=$(checks ${P}b ${P}b); ok "KF-A DB checks on ${P}b after the SCRAM change -> $r" "$([[ $r == '181 passed, 0 failed' ]] && echo 1 || echo 0)"
# KF-A 2: force an error INSIDE the DO block's EXECUTE (CREATE ROLE "none": reserved name, passes the name check, not
# in pg_roles). The unique password must appear neither on stderr/stdout nor in the server log after the offset.
for w in sh ps1; do
  u="kfa-uniq-$(PW)"; off=$(sudo -u postgres stat -c %s "$LOG"); s0=$(snap)
  if [[ $w == sh ]]; then rc=$(FF_DB_PASSWORD="$u" SH --db ${P}n --role none --no-migrate); else rc=$(FF_DB_PASSWORD="$u" PS -Db ${P}n -Role none -NoMigrate); fi
  ok "KF-A [$w] error inside the DO-block EXECUTE -> rc 5, 'CREATE ROLE \"none\" failed (42939)', nothing changed" \
    "$([[ $rc == 5 && $(snap) == "$s0" ]] && grep -q 'CREATE ROLE "none" failed (42939)' "$T/out" && echo 1 || echo 0)" "rc=$rc $(grep -v "$u" "$T/out" | tail -2 | tr '\n' ' ')"
  ok "KF-A [$w] the unique password is not in the script output" "$(grep -qF "$u" "$T/out" && echo 0 || echo 1)"
  ok "KF-A [$w] the unique password is not in the server log after offset $off (and no verifier either)" "$(logfrom $off | grep -qF -e "$u" -e 'SCRAM-SHA-256$' && echo 0 || echo 1)"
  ok "KF-A [$w] the server did log the error (so the log check is live)" "$(logfrom $off | grep -qF 'CREATE ROLE "none" failed (42939)' && echo 1 || echo 0)"
done
out=$(roledirect ${P}a_login 'plain-text-password'); rc=$?
ok "KF-A kit/role.sql refuses a value that is not a SCRAM verifier" "$([[ $rc != 0 && $out == *'must arrive as a SCRAM-SHA-256 verifier'* ]] && echo 1 || echo 0)" "rc=$rc"

# KF-C: a role outside the kit that is a member OF a kit role is refused (preflight in both scripts + DO block).
q "CREATE ROLE ${P}c NOLOGIN" >/dev/null; q "GRANT ${P}a_login TO ${P}c" >/dev/null; s0=$(snap)
rc=$(SH --db ${P}a --role ${P}a_owner --force)
ok "KF-C [sh] ${P}c member of kit role ${P}a_login -> rc 5, nothing changed" "$([[ $rc == 5 && $(snap) == "$s0" ]] && grep -q "${P}c is a member of kit role ${P}a_login" "$T/out" && echo 1 || echo 0)" "rc=$rc"
rc=$(PS -Db ${P}a -Role ${P}a_owner -Force)
ok "KF-C [ps1] same -> rc 5, nothing changed" "$([[ $rc == 5 && $(snap) == "$s0" ]] && grep -q "${P}c is a member of kit role ${P}a_login" "$T/out" && echo 1 || echo 0)" "rc=$rc"
h0=$(pwhash ${P}a_login); out=$(roledirect ${P}a_login); rc=$?
ok "KF-C kit/role.sql DO block directly -> error, password unchanged" "$([[ $rc != 0 && $out == *"outside the kit are members of it: ${P}c"* && $(pwhash ${P}a_login) == "$h0" ]] && echo 1 || echo 0)" "rc=$rc"
q "REVOKE ${P}a_login FROM ${P}c" >/dev/null; q "DROP ROLE ${P}c" >/dev/null

# KF-B race: a concurrent UNCOMMITTED GRANT pg_read_all_data TO the kit role. The kit's SHARE lock must wait for it;
# after the grantor commits, the kit must refuse. Never rc 0 with the membership present.
for w in sh ps1; do
  fifo="$T/race.$w"; rm -f "$fifo"; mkfifo "$fifo"
  ( sudo -u postgres psql -X -q -v ON_ERROR_STOP=1 -d postgres <"$fifo" >"$T/grantor.$w" 2>&1 ) & gp=$!
  exec 7>"$fifo"
  printf 'BEGIN;\nGRANT pg_read_all_data TO %s;\nSELECT pg_sleep(4);\nCOMMIT;\n' "${P}a_login" >&7; exec 7>&-
  sleep 1  # the GRANT is now uncommitted and holds ROW EXCLUSIVE on pg_auth_members
  t0=$(date +%s)
  if [[ $w == sh ]]; then rc=$(SH --db ${P}a --role ${P}a_owner --force); else rc=$(PS -Db ${P}a -Role ${P}a_owner -Force); fi
  el=$(( $(date +%s) - t0 )); wait $gp; grc=$?
  mem=$(q "SELECT count(*) FROM pg_auth_members m JOIN pg_roles g ON g.oid=m.roleid JOIN pg_roles r ON r.oid=m.member WHERE g.rolname='pg_read_all_data' AND r.rolname='${P}a_login'")
  ok "KF-B [$w] race with an uncommitted GRANT pg_read_all_data -> kit refused (rc $rc), membership present ($mem), kit waited for the lock (${el}s), never rc 0" \
    "$([[ $grc == 0 && $mem == 1 && $rc != 0 && $el -ge 2 ]] && grep -q 'pg_read_all_data' "$T/out" && echo 1 || echo 0)" "rc=$rc grc=$grc mem=$mem el=${el}s $(tail -2 "$T/out" | tr '\n' ' ')"
  q "REVOKE pg_read_all_data FROM ${P}a_login" >/dev/null
done


# TLD 9:19: live non-superuser admin (CREATEROLE + CREATEDB is still not enough) -> rc 5 up front, nothing changed.
ADMPW="adm-$(PW)"
q "CREATE ROLE ${P}adm LOGIN CREATEROLE CREATEDB" >/dev/null
# I-4: the password travels on psql's stdin, never on a command line.
printf "ALTER ROLE %s PASSWORD '%s';\n" "${P}adm" "$ADMPW" | sudo -u postgres psql -X -q -v ON_ERROR_STOP=1 -d postgres -f - >/dev/null
for w in sh ps1; do
  for f in "" force; do
    s0=$(snap); c0=$(q "SELECT md5(string_agg(coalesce(description,''), '|' ORDER BY objoid, classoid)) FROM pg_shdescription")
    if [[ $w == sh ]]; then
      PGPASSWORD="$ADMPW" bash "$DBDIR/setup.sh" --superuser-cmd "psql -h 127.0.0.1 -U ${P}adm" --db ${P}na --role ${P}na ${f:+--force --drop} --seed >"$T/out" 2>&1; rc=$?
    else
      PGPASSWORD="$ADMPW" pwsh -NoProfile -NonInteractive -File "$DBDIR/setup.ps1" -AdminHost 127.0.0.1 -AdminUser ${P}adm -Db ${P}na -Role ${P}na ${f:+-Force -Drop} -Seed >"$T/out" 2>&1; rc=$?
    fi
    ok "TLD 9:19 [$w] non-superuser admin ${P}adm (CREATEROLE CREATEDB)${f:+ with --force} -> rc 5, message, DBs/roles/comments unchanged, no change step ran" \
      "$([[ $rc == 5 && $(snap) == "$s0" && $(q "SELECT md5(string_agg(coalesce(description,''), '|' ORDER BY objoid, classoid)) FROM pg_shdescription") == "$c0" && $(q "SELECT count(*) FROM pg_roles WHERE rolname LIKE '${PL}na%'") == 0 && $(q "SELECT count(*) FROM pg_database WHERE datname LIKE '${PL}na%'") == 0 ]] && grep -qF "Refused: the admin connection must be a PostgreSQL superuser (e.g. postgres); ${P}adm is not." "$T/out" && ! grep -Eq 'create or update|create if missing|Dropping|migrat' "$T/out" && echo 1 || echo 0)" "rc=$rc $(tail -2 "$T/out" | tr '\n' ' ')"
    ok "TLD 9:19 [$w] no password in the output" "$(grep -qF -e "$ADMPW" -e "$FF_DB_PASSWORD" -e 'SCRAM-SHA-256$' "$T/out" && echo 0 || echo 1)"
  done
done
q "DROP ROLE ${P}adm" >/dev/null


# KF-G (TLD 9:42): a membership between two roles of the same kit install is refused (preflight both scripts + DO block).
for g in "${P}a_owner:${P}a_login" "${P}a_login:${P}a_admin_api" "${P}a_issuer:${P}a_admin_api"; do
  gr=${g%%:*}; mb=${g##*:}
  q "GRANT $gr TO $mb" >/dev/null; s0=$(snap)
  rc=$(SH --db ${P}a --role ${P}a_owner --force --no-migrate)
  ok "KF-G [sh] GRANT $gr TO $mb -> rc 5, nothing changed" "$([[ $rc == 5 && $(snap) == "$s0" ]] && grep -q "role $mb is a member of kit role $gr (KF-G" "$T/out" && echo 1 || echo 0)" "rc=$rc $(tail -2 "$T/out" | tr '\n' ' ')"
  rc=$(PS -Db ${P}a -Role ${P}a_owner -Force -NoMigrate)
  ok "KF-G [ps1] GRANT $gr TO $mb -> rc 5, nothing changed" "$([[ $rc == 5 && $(snap) == "$s0" ]] && grep -q "role $mb is a member of kit role $gr (KF-G" "$T/out" && echo 1 || echo 0)" "rc=$rc"
  for t in $gr $mb; do
    h0=$(pwhash $t); out=$(roledirect $t); rc=$?
    ok "KF-G kit/role.sql DO block directly on $t ($mb in $gr) -> error, password and membership unchanged" "$([[ $rc != 0 && $out == *"it has role memberships (the kit uses none): $mb in $gr"* && $(pwhash $t) == "$h0" && $(snap) == "$s0" ]] && echo 1 || echo 0)" "rc=$rc $out"
  done
  q "REVOKE $gr FROM $mb" >/dev/null
done

# I-5 (TLD 9:42): forced ALTER ROLE error inside the DO-block EXECUTE. passwordcheck is loaded for the kit's superuser
# connections only, per connection (PGOPTIONS session_preload_libraries; no server config change). It rejects a
# password equal to the role name even as a SCRAM verifier (SQLSTATE 22023). The password here IS the role name, so
# the leak checks look for the verifier and for passwordcheck's own message instead.
PCO=-csession_preload_libraries=passwordcheck
VRE='SCRAM-SHA-256\$4096:[A-Za-z0-9+/]{16,}'
for w in sh ps1; do
  off=$(sudo -u postgres stat -c %s "$LOG"); h0=$(pwhash ${P}a_login)
  if [[ $w == sh ]]; then
    FF_LOGIN_PASSWORD=${P}a_login bash "$DBDIR/setup.sh" --superuser-cmd "sudo -u postgres env PGOPTIONS=$PCO psql" --db ${P}a --role ${P}a_owner --force --no-migrate >"$T/out" 2>&1; rc=$?
  else
    FF_LOGIN_PASSWORD=${P}a_login PGOPTIONS=$PCO sudo -u postgres --preserve-env=FF_DB_PASSWORD,FF_ADMIN_API_PASSWORD,FF_LOGIN_PASSWORD,FF_ISSUER_PASSWORD,PGOPTIONS pwsh -NoProfile -NonInteractive -File "$DBDIR/setup.ps1" -AdminHost '' -Db ${P}a -Role ${P}a_owner -Force -NoMigrate >"$T/out" 2>&1; rc=$?
  fi
  ok "I-5 [$w] passwordcheck rejects the ALTER ROLE -> rc 5, 'ALTER ROLE ${P}a_login failed (22023)', login password unchanged" \
    "$([[ $rc == 5 && $(pwhash ${P}a_login) == "$h0" ]] && grep -qF "floorfunds-kit: ALTER ROLE ${P}a_login failed (22023)" "$T/out" && echo 1 || echo 0)" "rc=$rc $(tail -2 "$T/out" | tr '\n' ' ' | sed -E "s#$VRE#<VERIFIER>#g")"
  ok "I-5 [$w] no verifier and no inner error text in the script output" "$(grep -Eq -e "$VRE" -e 'must not' "$T/out" && echo 0 || echo 1)"
  ok "I-5 [$w] no verifier and no inner error text in the new server-log lines (after offset $off)" "$(logfrom $off | grep -Eq -e "$VRE" -e 'must not' && echo 0 || echo 1)"
  ok "I-5 [$w] the server did log the wrapped error (log check is live)" "$(logfrom $off | grep -qF "ALTER ROLE ${P}a_login failed (22023)" && echo 1 || echo 0)"
done

cleanup
ok "cleanup: no ${P}* DB or role left" "$([[ $(q "SELECT (SELECT count(*) FROM pg_database WHERE datname LIKE '$PL%') + (SELECT count(*) FROM pg_roles WHERE rolname LIKE '$PL%')") == 0 ]] && echo 1 || echo 0)"
rm -rf "$T"
# I-6 (TLD 9:5x): Get-Help must render the comment-based help, incl. the I-3 -Password warning
gh=$(pwsh -NoProfile -NonInteractive -c "Get-Help '$DBDIR/setup.ps1' -Full" 2>&1)
ok "I-6 [ps1] Get-Help shows SYNOPSIS" "$([[ $gh == *SYNOPSIS* ]] && echo 1 || echo 0)"
ok "I-6 [ps1] Get-Help shows the I-3 -Password argv warning" "$([[ $gh == *'WARNING (I-3)'* ]] && echo 1 || echo 0)"
ok "I-6 [ps1] Get-Help shows the examples" "$([[ $gh == *'EXAMPLE 1'* ]] && echo 1 || echo 0)"

echo; echo "$pass passed, $fail failed"
[[ $fail == 0 ]]
