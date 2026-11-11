-- Runs as a superuser, connected to the kit database. The migration user is NOCREATEROLE
-- and is not a member of this role, so it cannot ALTER FUNCTION OWNER. No membership is granted.
DO $own$
DECLARE
  r text := current_database() || '_auth_definer';
  fn text;
  owner_name text;
  names text[] := ARRAY[
    'auth._request_user()',
    'auth.actor_is_global()',
    'auth.actor_is_any_admin()',
    'auth.actor_has_any_location()',
    'auth.actor_can_partner(uuid)',
    'auth.actor_location_sees_partner(uuid)',
    'auth.actor_has_location(uuid)',
    'auth.actor_can_location(uuid, uuid)',
    'auth.actor_sees_change(uuid, text, uuid, uuid)',
    'auth.list_existing_partners()',
    'auth.resolve_grant(text, uuid)',
    'auth.read_partner_bounds(uuid)',
    'auth.save_partner_bounds(uuid, bigint, integer, integer, uuid, timestamptz)',
    'auth.record_login_write(text, uuid)',
    'auth.save_location_login_config(uuid, bigint, text, text, uuid[], text, text, boolean, text, boolean, boolean, uuid, timestamptz)',
    'auth.open_session(text, text, uuid, timestamptz)',
    'auth.begin_request(text)',
    'auth.current_principal()',
    'auth.session_issuer()',
    'auth.revoke_session(text)',
    'auth.revoke_on_principal_change()',
    'auth.revoke_login(text, text)'
  ];
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_roles
     WHERE rolname = r AND NOT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls AND NOT rolinherit
  ) THEN
    RAISE EXCEPTION 'auth definer role % must exist, NOLOGIN, NOINHERIT, and not a superuser', r;
  END IF;
  FOREACH fn IN ARRAY names LOOP
    EXECUTE format('ALTER FUNCTION %s OWNER TO %I', fn, r);
  END LOOP;
  -- ALTER OWNER drops the migration user's implicit execute right. open_session stays
  -- with the issuer role only. The owner keeps the read and revoke functions.
  SELECT pg_get_userbyid(datdba)::text INTO owner_name
    FROM pg_database WHERE datname = current_database();
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.begin_request(text), auth.current_principal(), auth.session_issuer(), auth.revoke_session(text) TO %I', owner_name);
  EXECUTE format('REVOKE ALL ON FUNCTION auth.open_session(text, text, uuid, timestamptz) FROM %I', owner_name);
  -- catalog._actor calls resolve_grant while the registry trigger runs as the owner.
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.resolve_grant(text, uuid) TO %I', owner_name);
  -- The login-config UPDATE reads the kept columns. The definer needs SELECT as well as UPDATE.
  -- RLS also needs a SELECT policy: an UPDATE policy alone matches zero rows.
  EXECUTE format('GRANT SELECT, UPDATE ON auth.location_login_config TO %I', r);
  EXECUTE 'DROP POLICY IF EXISTS llc_definer_read ON auth.location_login_config';
  EXECUTE format(
    'CREATE POLICY llc_definer_read ON auth.location_login_config FOR SELECT TO %I USING (true)',
    r);
END
$own$;
