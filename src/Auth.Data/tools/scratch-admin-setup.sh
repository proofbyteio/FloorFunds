#!/usr/bin/env bash
# One-time local setup for the Auth.Data.Checks scratch DB (TLD 2026-11-04 11:21, Q2 option b). LOCAL LOOPBACK TEST CLUSTER ONLY.
# Creates (or, when already marked, re-keys) the maintenance role ff_scratchadm (LOGIN CREATEDB CREATEROLE, NOSUPERUSER
# NOBYPASSRLS NOREPLICATION). [TLD 2026-11-04 12:04 F1] It no longer creates a maintenance DB: the checks connect to the
# maintenance DB SCRATCH_MAINT_DB (default postgres) on SCRATCH_PG_HOST (127.0.0.1 or ::1 only) / SCRATCH_PG_PORT (default
# 5432); the Host / Port / Database in the env file are replaced by those at run time. An ff_maint_scratch made by an older
# version of this script is no longer used (dropped under TLD 12:33 item 2). The peer connection below uses the default local cluster.
# PG16+ CREATEROLE: ff_scratchadm can only alter/drop roles it created; DROP DATABASE needs ownership, so it can never drop
# ff_sse / ff_sse2. Writes SCRATCH_ADMIN_CONNECTION_STRING to /tmp/ff-db/ff_scratchadm.env (0600, test-only, never printed).
# Refuses an existing role of that name without the marker comment. Needs sudo -u postgres.
# [TLD 2026-11-04 12:33] ff_scratchadm is documented kit infrastructure (src/Auth.Data.Checks/README.md): marked
# floorfunds-scratch-admin, NOSUPERUSER NOINHERIT NOREPLICATION NOBYPASSRLS, only CREATEDB + CREATEROLE, no settings, no
# memberships except the PG17 implicit grants on the marked per-run roles it creates; every main run checks this
# (ScratchDb.AdminRoleViolationsAsync). The password is generated here, written only to the 0600 env file, never in the repo.
# The ff_maint_scratch DB from an older version of this script was dropped under TLD 12:33 item 2.
set -euo pipefail
ENV=/tmp/ff-db/ff_scratchadm.env
PW="$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')"
{ printf "\\\\set p '%s'\n" "$PW"; cat <<'SQL'
\set ON_ERROR_STOP 1
SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ff_scratchadm') AS role_exists \gset
\if :role_exists
  DO $g$ BEGIN
    IF coalesce(shobj_description((SELECT oid FROM pg_roles WHERE rolname = 'ff_scratchadm'), 'pg_authid'), '') <> 'floorfunds-scratch-admin'
    THEN RAISE EXCEPTION 'refuse: role ff_scratchadm exists without the floorfunds-scratch-admin marker'; END IF;
  END $g$;
\else
  CREATE ROLE ff_scratchadm LOGIN CREATEDB CREATEROLE NOSUPERUSER NOBYPASSRLS NOREPLICATION NOINHERIT;
  COMMENT ON ROLE ff_scratchadm IS 'floorfunds-scratch-admin';
\endif
ALTER ROLE ff_scratchadm PASSWORD :'p';
SQL
} | sudo -u postgres psql -X -q -d postgres -f - >/dev/null
umask 077
printf "SCRATCH_ADMIN_CONNECTION_STRING='Host=127.0.0.1;Port=5432;Database=postgres;Username=ff_scratchadm;Password=%s'\n" "$PW" > "$ENV"
echo "scratch-admin-setup: ok (role ff_scratchadm; env written to $ENV, not printed)"
