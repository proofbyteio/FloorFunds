-- floorfunds-kit: create or update ONE login role. psql variables: r (name), p (password; set on stdin, never argv),
-- db and role (to name this run's kit roles: role, <db>_admin_api, <db>_login).
-- K1 + KF1/KF2: the check and the change run in ONE statement (a single DO block, one transaction):
--   * role absent  -> CREATE ROLE ... and COMMENT ... IS 'floorfunds-kit' together (both or neither);
--   * role present -> ALTER ROLE only if it carries the marker, is not a superuser, has none of BYPASSRLS,
--     CREATEROLE, CREATEDB, REPLICATION, and is a member of no role outside this run's kit-marked roles.
--   Anything else raises, and nothing is changed. Values reach the DO block as transaction-local settings
--   (set_config(..., true)); \gset keeps them off stdout.
BEGIN;
SELECT set_config('floorfunds_kit.r', :'r', true) AS kit_r, set_config('floorfunds_kit.p', :'p', true) AS kit_p,
       set_config('floorfunds_kit.kit', concat_ws(',', :'role', :'db' || '_admin_api', :'db' || '_login'), true) AS kit_k \gset
\unset kit_p
DO $kit$
DECLARE
  r   text := current_setting('floorfunds_kit.r');
  p   text := current_setting('floorfunds_kit.p');
  kit text[] := string_to_array(current_setting('floorfunds_kit.kit'), ',');
  a   record;
  bad text;
BEGIN
  SELECT oid, rolsuper, rolbypassrls, rolcreaterole, rolcreatedb, rolreplication,
         shobj_description(oid, 'pg_authid') IS NOT DISTINCT FROM 'floorfunds-kit' AS marked
    INTO a FROM pg_roles WHERE rolname = r;
  IF NOT FOUND THEN
    EXECUTE format('CREATE ROLE %I LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', r, p);
    EXECUTE format('COMMENT ON ROLE %I IS %L', r, 'floorfunds-kit');
    RETURN;
  END IF;
  IF NOT a.marked OR a.rolsuper THEN
    RAISE EXCEPTION 'floorfunds-kit: refusing to alter a role the kit did not create (no marker, or a superuser): %', r;
  END IF;
  IF a.rolbypassrls OR a.rolcreaterole OR a.rolcreatedb OR a.rolreplication THEN
    RAISE EXCEPTION 'floorfunds-kit: refusing to alter role %: it has BYPASSRLS, CREATEROLE, CREATEDB or REPLICATION', r;
  END IF;
  WITH RECURSIVE mem(roleid) AS (
    SELECT m.roleid FROM pg_auth_members m WHERE m.member = a.oid
    UNION SELECT m.roleid FROM mem JOIN pg_auth_members m ON m.member = mem.roleid)
  SELECT string_agg(g.rolname, ', ' ORDER BY g.rolname) INTO bad FROM mem JOIN pg_roles g ON g.oid = mem.roleid
   WHERE NOT (g.rolname = ANY (kit) AND shobj_description(g.oid, 'pg_authid') IS NOT DISTINCT FROM 'floorfunds-kit');
  IF bad IS NOT NULL THEN
    RAISE EXCEPTION 'floorfunds-kit: refusing to alter role %: it is a member of role(s) outside the kit: %', r, bad;
  END IF;
  EXECUTE format('ALTER ROLE %I LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', r, p);
END
$kit$;
COMMIT;
