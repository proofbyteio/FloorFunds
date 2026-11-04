#!/usr/bin/env bash
# K1/K5 tests for db/setup.sh AND db/setup.ps1 (SSE2 review of d787f76; TL 8:32). TEST-ONLY, engineers' box.
#  A. stub runs: the superuser psql is db/tests/stub-psql.sh (logs every script, answers the preflight with canned
#     findings), so the real postgres, template*, ff_sse and ff_sse2 are named but never touched. Asserts the exit
#     code, the message, and that nothing but the read-only preflight was sent (or nothing at all).
#  B. live runs on scratch ff_kit_k1* DBs/roles created here (unmarked ones by hand, for the purpose) and all
#     dropped at the end. Asserts refusals change nothing (oid, attributes, password hash, comment).
# usage: db/tests/k1-kit-tests.sh   (needs sudo -u postgres, pwsh 7, and for the DB checks a built src/Auth.Data.Checks)
set -uo pipefail
DBDIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$DBDIR/../src"
T="$(mktemp -d /tmp/kit-k1.XXXXXX)"; chmod 0711 "$T"
mkdir -p "$T/bin"; cp "$DBDIR/tests/stub-psql.sh" "$T/bin/psql"; chmod 0755 "$T/bin/psql"
export KIT_STUB_LOG="$T/stub.log" KIT_STUB_PREFLIGHT="$T/preflight.out"
pass=0; fail=0
ok() { if [[ "$2" == 1 ]]; then pass=$((pass+1)); echo "PASS $1"; else fail=$((fail+1)); echo "FAIL $1${3:+ :: $3}"; fi; }
t() { "$@" >/dev/null 2>&1 && echo 1 || echo 0; }
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
export FF_DB_PASSWORD="$(PW)" FF_ADMIN_API_PASSWORD="$(PW)" FF_LOGIN_PASSWORD="$(PW)"
echo "=== A. stub runs (no database is touched) ==="
stub_case "--db postgres --force" 5 0 "postgres is a system database" "" -- --db postgres --role ff_kit_k1z --force --no-migrate
stub_case "--db postgres --role postgres --force --drop" 5 0 "system database" "" -- --db postgres --role postgres --force --drop --no-migrate
stub_case "--db template0 --force --drop" 5 0 "template0 is a system database" "" -- --db template0 --role ff_kit_k1z --force --drop --no-migrate
stub_case "--db template1 --force" 5 0 "template1 is a system database" "" -- --db template1 --role ff_kit_k1z --force --no-migrate
stub_case "--role postgres --force" 5 0 "role postgres is the bootstrap superuser" "" -- --db ff_kit_k1z --role postgres --force --no-migrate
stub_case "--role pg_x (reserved prefix)" 5 0 "pg_ prefix is reserved" "" -- --db ff_kit_k1z --role pg_x --force --no-migrate
stub_case "--drop without --force" 2 0 "drop needs .?-?-?[Ff]orce|Drop needs -Force" "" -- --db ff_kit_k1z --role ff_kit_k1z --drop --no-migrate
# What the real catalog would report for the engineers' DBs (they were made by ff-db.sh, not the kit: no marker).
FF_SSE_PRE=$'REFUSE role ff_sse exists but was not created by the kit (no floorfunds-kit marker)\nREFUSE role ff_sse_admin_api exists but was not created by the kit (no floorfunds-kit marker)\nREFUSE role ff_sse_login exists but was not created by the kit (no floorfunds-kit marker)\nREFUSE database ff_sse exists but was not created by the kit (no floorfunds-kit marker)\nEXISTS role ff_sse\nEXISTS role ff_sse_admin_api\nEXISTS role ff_sse_login\nEXISTS database ff_sse\n'
stub_case "--db ff_sse --role ff_sse --force (unmarked) still refused" 5 1 "Refused: database ff_sse exists but was not created by the kit" "$FF_SSE_PRE" -- --db ff_sse --role ff_sse --force --no-migrate
stub_case "--db ff_sse --role ff_sse --force --drop (unmarked) still refused" 5 1 "never overrides" "$FF_SSE_PRE" -- --db ff_sse --role ff_sse --force --drop --no-migrate
stub_case "--db ff_sse2 --role ff_sse2 --force --drop (unmarked) still refused" 5 1 "Refused: role ff_sse2 exists but was not created" "${FF_SSE_PRE//ff_sse/ff_sse2}" -- --db ff_sse2 --role ff_sse2 --force --drop --no-migrate
stub_case "superuser role, even if marked, --force" 5 1 "Refused: role ff_kit_k1z is a superuser" $'REFUSE role ff_kit_k1z is a superuser\nEXISTS role ff_kit_k1z\n' -- --db ff_kit_k1z --role ff_kit_k1z --force --no-migrate
stub_case "existing kit objects without --force" 5 1 "re-run with .?-?-?[Ff]orce" $'EXISTS role ff_kit_k1z\nEXISTS database ff_kit_k1z\n' -- --db ff_kit_k1z --role ff_kit_k1z --no-migrate
# --force on the kit's own objects proceeds: 1 preflight + 3 roles + 1 DB, every mutating script is the guarded kit SQL.
stub_case "existing kit objects with --force proceeds (5 calls)" 0 5 "Done" $'EXISTS role ff_kit_k1z\nEXISTS database ff_kit_k1z\n' -- --db ff_kit_k1z --role ff_kit_k1z --force --no-migrate
ok "stub: the --force run sent only guarded kit SQL (role.sql x3 marker guard, database.sql guard)" \
  "$([[ $(grep -c 'refusing to alter a role the kit did not create' "$KIT_STUB_LOG") == 3 && $(grep -c 'refusing to touch a database the kit did not create' "$KIT_STUB_LOG") == 1 ]] && echo 1 || echo 0)"
OLDPW="$FF_DB_PASSWORD"
for bad in $'nl\nbreak' $'cr\rbreak'; do
  FF_DB_PASSWORD="$bad" stub_case "K5 owner password with ${bad:0:2} refused" 2 0 "contains a newline or carriage return" "" -- --db ff_kit_k1z --role ff_kit_k1z --no-migrate
  FF_LOGIN_PASSWORD="$bad" stub_case "K5 login password with ${bad:0:2} refused" 2 0 "contains a newline or carriage return" "" -- --db ff_kit_k1z --role ff_kit_k1z --no-migrate
done

# ---------- B. live runs on scratch ff_kit_k1* objects ----------
echo; echo "=== B. live runs (scratch ff_kit_k1* only) ==="
snap() { # fingerprint of everything a refusal must leave alone
  q "SELECT md5(coalesce(string_agg(x, '|' ORDER BY x), '')) FROM (
       SELECT 'r:'||a.rolname||':'||a.oid||':'||a.rolsuper||a.rolcanlogin||a.rolcreatedb||a.rolbypassrls||':'||md5(coalesce(a.rolpassword,''))||':'||coalesce(shobj_description(a.oid,'pg_authid'),'-') x
         FROM pg_authid a WHERE a.rolname LIKE 'ff\_kit\_k1%'
       UNION ALL SELECT 'd:'||datname||':'||oid||':'||pg_get_userbyid(datdba)||':'||coalesce(datacl::text,'')||':'||coalesce(shobj_description(oid,'pg_database'),'-')
         FROM pg_database WHERE datname LIKE 'ff\_kit\_k1%') s"; }
marked() { q "SELECT count(*) FROM (SELECT oid FROM pg_roles WHERE rolname = ANY(string_to_array('$1', ',')) AND shobj_description(oid,'pg_authid')='floorfunds-kit'
                UNION ALL SELECT oid FROM pg_database WHERE datname='$2' AND shobj_description(oid,'pg_database')='floorfunds-kit') s"; }
dboid() { q "SELECT coalesce((SELECT oid::text FROM pg_database WHERE datname='$1'), 'none')"; }
pwhash() { q "SELECT md5(coalesce(rolpassword,'')) FROM pg_authid WHERE rolname='$1'"; }
SH() { bash "$DBDIR/setup.sh" "$@" >"$T/out" 2>&1; echo $?; }
PS() { sudo -u postgres --preserve-env=FF_DB_PASSWORD,FF_ADMIN_API_PASSWORD,FF_LOGIN_PASSWORD pwsh -NoProfile -NonInteractive -File "$DBDIR/setup.ps1" -AdminHost '' "$@" >"$T/out" 2>&1; echo $?; }
checks() { # checks DB OWNER -> last line of the DB checks (passwords from the current env)
  ( cd "$SRC" && export PATH=/home/box/.dotnet:$PATH DOTNET_ROOT=/home/box/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
      CONNECTION_STRING="Host=127.0.0.1;Port=5432;Database=$1;Username=$2;Password=$FF_DB_PASSWORD" \
      ADMIN_API_CONNECTION_STRING="Host=127.0.0.1;Port=5432;Database=$1;Username=$1_admin_api;Password=$FF_ADMIN_API_PASSWORD" \
      LOGIN_CONNECTION_STRING="Host=127.0.0.1;Port=5432;Database=$1;Username=$1_login;Password=$FF_LOGIN_PASSWORD" \
      AUTH_CHECKS_DB="$1" && dotnet run --no-build --project Auth.Data.Checks -- Login.StandIn/fixtures.json 2>&1 | tail -1 ); }
cleanup() {
  for d in $(q "SELECT datname FROM pg_database WHERE datname LIKE 'ff\_kit\_k1%'"); do q "DROP DATABASE \"$d\" WITH (FORCE)" >/dev/null; done
  for r in $(q "SELECT rolname FROM pg_roles WHERE rolname LIKE 'ff\_kit\_k1%'"); do q "DROP ROLE \"$r\"" >/dev/null; done
}
cleanup
export FF_DB_PASSWORD="$(PW)" FF_ADMIN_API_PASSWORD="$(PW)" FF_LOGIN_PASSWORD="$(PW)"

# B1 setup.sh fresh, --role != --db (K2)
rc=$(SH --db ff_kit_k1a --role ff_kit_k1a_owner --seed)
ok "[sh] fresh ff_kit_k1a (--role ff_kit_k1a_owner) -> rc 0; DB + 3 roles carry the kit marker" "$([[ $rc == 0 && $(marked ff_kit_k1a_owner,ff_kit_k1a_admin_api,ff_kit_k1a_login ff_kit_k1a) == 4 ]] && echo 1 || echo 0)" "rc=$rc $(tail -3 "$T/out" | tr '\n' ' ')"
r=$(checks ff_kit_k1a ff_kit_k1a_owner); ok "[sh] DB checks on ff_kit_k1a with --role != --db (K2) -> $r" "$([[ $r == '162 passed, 0 failed' ]] && echo 1 || echo 0)"
s0=$(snap); rc=$(SH --db ff_kit_k1a --role ff_kit_k1a_owner --seed)
ok "[sh] re-run without --force -> rc 5, nothing changed" "$([[ $rc == 5 && $(snap) == "$s0" ]] && grep -q 're-run with --force' "$T/out" && echo 1 || echo 0)" "rc=$rc"
rc=$(SH --db ff_kit_k1a --role ff_kit_k1a_owner --drop)
ok "[sh] --drop without --force -> rc 2, nothing changed" "$([[ $rc == 2 && $(snap) == "$s0" ]] && echo 1 || echo 0)" "rc=$rc"
oid0=$(dboid ff_kit_k1a); h0=$(pwhash ff_kit_k1a_owner)
TRICKY="k1'q\\b-$(PW)"; rc=$(FF_DB_PASSWORD="$TRICKY" SH --db ff_kit_k1a --role ff_kit_k1a_owner --force)
ok "[sh] --force on the kit's own objects -> rc 0, same DB oid, owner password re-set (quote+backslash password logs in)" \
  "$([[ $rc == 0 && $(dboid ff_kit_k1a) == "$oid0" && $(pwhash ff_kit_k1a_owner) != "$h0" ]] && PGPASSWORD="$TRICKY" psql -X -tA -h 127.0.0.1 -U ff_kit_k1a_owner -d ff_kit_k1a -c 'select 1' >/dev/null 2>&1 && echo 1 || echo 0)" "rc=$rc"
rc=$(SH --db ff_kit_k1a --role ff_kit_k1a_owner --force --drop --seed)
ok "[sh] --force --drop -> rc 0, DB recreated (new oid), still marked" "$([[ $rc == 0 && $(dboid ff_kit_k1a) != "$oid0" && $(marked x ff_kit_k1a) == 1 ]] && echo 1 || echo 0)" "rc=$rc"
r=$(checks ff_kit_k1a ff_kit_k1a_owner); ok "[sh] DB checks after --force --drop -> $r" "$([[ $r == '162 passed, 0 failed' ]] && echo 1 || echo 0)"

# B2 setup.ps1 fresh, --role == --db
rc=$(PS -Db ff_kit_k1b -Role ff_kit_k1b -Seed)
ok "[ps1] fresh ff_kit_k1b -> rc 0; DB + 3 roles carry the kit marker" "$([[ $rc == 0 && $(marked ff_kit_k1b,ff_kit_k1b_admin_api,ff_kit_k1b_login ff_kit_k1b) == 4 ]] && echo 1 || echo 0)" "rc=$rc $(tail -3 "$T/out" | tr '\n' ' ')"
r=$(checks ff_kit_k1b ff_kit_k1b); ok "[ps1] DB checks on ff_kit_k1b -> $r" "$([[ $r == '162 passed, 0 failed' ]] && echo 1 || echo 0)"
s0=$(snap); rc=$(PS -Db ff_kit_k1b -Role ff_kit_k1b)
ok "[ps1] re-run without -Force -> rc 5, nothing changed" "$([[ $rc == 5 && $(snap) == "$s0" ]] && grep -q 're-run with -Force' "$T/out" && echo 1 || echo 0)" "rc=$rc"
rc=$(PS -Db ff_kit_k1b -Role ff_kit_k1b -Drop)
ok "[ps1] -Drop without -Force -> rc 2, nothing changed" "$([[ $rc == 2 && $(snap) == "$s0" ]] && echo 1 || echo 0)" "rc=$rc"
oid0=$(dboid ff_kit_k1b); rc=$(PS -Db ff_kit_k1b -Role ff_kit_k1b -Force -Drop -Seed)
ok "[ps1] -Force -Drop -> rc 0, DB recreated (new oid), still marked" "$([[ $rc == 0 && $(dboid ff_kit_k1b) != "$oid0" && $(marked x ff_kit_k1b) == 1 ]] && echo 1 || echo 0)" "rc=$rc"
r=$(checks ff_kit_k1b ff_kit_k1b); ok "[ps1] DB checks after -Force -Drop -> $r" "$([[ $r == '162 passed, 0 failed' ]] && echo 1 || echo 0)"

# B3 hand-made UNMARKED scratch objects (stand-ins for "someone else's DB/role") + a marked superuser
q "CREATE ROLE ff_kit_k1u NOLOGIN" >/dev/null; q "CREATE DATABASE ff_kit_k1u OWNER ff_kit_k1u" >/dev/null
q "CREATE ROLE ff_kit_k1r NOLOGIN" >/dev/null
q "CREATE ROLE ff_kit_k1d_login NOLOGIN" >/dev/null
q "CREATE ROLE ff_kit_k1s NOLOGIN SUPERUSER" >/dev/null; q "COMMENT ON ROLE ff_kit_k1s IS 'floorfunds-kit'" >/dev/null
live_refuse() { # NAME MSG -- sh-args
  local name="$1" msg="$2"; shift 3
  for which in sh ps1; do
    s0=$(snap)
    if [[ $which == sh ]]; then rc=$(SH "$@"); else mapfile -t pa < <(ps1args "$@"); rc=$(PS "${pa[@]}"); fi
    ok "[$which] $name -> rc 5, /$msg/, nothing changed" "$([[ $rc == 5 && $(snap) == "$s0" ]] && grep -Eq -- "$msg" "$T/out" && echo 1 || echo 0)" "rc=$rc out=$(tr '\n' ' ' <"$T/out" | cut -c1-300)"
  done
}
live_refuse "unmarked DB+role ff_kit_k1u with --force --drop" "database ff_kit_k1u exists but was not created by the kit" -- --db ff_kit_k1u --role ff_kit_k1u --force --drop --no-migrate
live_refuse "unmarked --role ff_kit_k1r with --force (new DB not created)" "role ff_kit_k1r exists but was not created by the kit" -- --db ff_kit_k1n --role ff_kit_k1r --force --no-migrate
live_refuse "unmarked app role ff_kit_k1d_login with --force" "role ff_kit_k1d_login exists but was not created by the kit" -- --db ff_kit_k1d --role ff_kit_k1d_o --force --no-migrate
live_refuse "marked SUPERUSER ff_kit_k1s with --force" "role ff_kit_k1s is a superuser" -- --db ff_kit_k1m --role ff_kit_k1s --force --no-migrate
live_refuse "kit role ff_kit_k1a_owner owning another DB with --force" "role ff_kit_k1a_owner owns database ff_kit_k1a" -- --db ff_kit_k1c --role ff_kit_k1a_owner --force --no-migrate
live_refuse "kit DB ff_kit_k1a owned by another kit role with --force --drop" "database ff_kit_k1a is owned by ff_kit_k1a_owner, not by --role ff_kit_k1b" -- --db ff_kit_k1a --role ff_kit_k1b --force --drop --no-migrate
ok "live: no DB ff_kit_k1n/k1d/k1m/k1c and no role ff_kit_k1d_o were created by the refused runs" \
  "$([[ $(q "SELECT count(*) FROM pg_database WHERE datname IN ('ff_kit_k1n','ff_kit_k1d','ff_kit_k1m','ff_kit_k1c')") == 0 && $(q "SELECT count(*) FROM pg_roles WHERE rolname='ff_kit_k1d_o'") == 0 ]] && echo 1 || echo 0)"
ok "live: ff_kit_k1s is still SUPERUSER NOLOGIN; ff_kit_k1u/k1r/k1d_login still NOLOGIN and unmarked" \
  "$([[ $(q "SELECT count(*) FROM pg_roles WHERE (rolname='ff_kit_k1s' AND rolsuper AND NOT rolcanlogin) OR (rolname IN ('ff_kit_k1u','ff_kit_k1r','ff_kit_k1d_login') AND NOT rolcanlogin AND shobj_description(oid,'pg_authid') IS NULL)") == 4 ]] && echo 1 || echo 0)"

# B4 defence in depth: the mutating kit SQL itself refuses unmarked objects (if the preflight were bypassed / raced)
s0=$(snap)
out=$( { printf "\\\\set r 'ff_kit_k1u'\n\\\\set p 'x'\n"; cat "$DBDIR/kit/role.sql"; } | sudo -u postgres psql -X -q -v ON_ERROR_STOP=1 -d postgres -f - 2>&1); rc=$?
ok "kit/role.sql directly on unmarked ff_kit_k1u -> error, role unchanged" "$([[ $rc != 0 && $out == *'refusing to alter a role the kit did not create'* && $(snap) == "$s0" ]] && echo 1 || echo 0)" "rc=$rc"
out=$( { printf "\\\\set r 'ff_kit_k1s'\n\\\\set p 'x'\n"; cat "$DBDIR/kit/role.sql"; } | sudo -u postgres psql -X -q -v ON_ERROR_STOP=1 -d postgres -f - 2>&1); rc=$?
ok "kit/role.sql directly on marked superuser ff_kit_k1s -> error, still superuser" "$([[ $rc != 0 && $out == *'refusing to alter'* && $(snap) == "$s0" ]] && echo 1 || echo 0)" "rc=$rc"
out=$(sudo -u postgres psql -X -q -v ON_ERROR_STOP=1 -d postgres -v db=ff_kit_k1u -v role=ff_kit_k1u -v drop=1 -f - <"$DBDIR/kit/database.sql" 2>&1); rc=$?
ok "kit/database.sql drop=1 directly on unmarked ff_kit_k1u -> error, DB not dropped/regranted" "$([[ $rc != 0 && $out == *'refusing to touch a database the kit did not create'* && $(snap) == "$s0" ]] && echo 1 || echo 0)" "rc=$rc"

cleanup
ok "cleanup: no ff_kit_k1* DB or role left" "$([[ $(q "SELECT (SELECT count(*) FROM pg_database WHERE datname LIKE 'ff\_kit\_k1%') + (SELECT count(*) FROM pg_roles WHERE rolname LIKE 'ff\_kit\_k1%')") == 0 ]] && echo 1 || echo 0)"
rm -rf "$T"
echo; echo "$pass passed, $fail failed"
[[ $fail == 0 ]]
