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
    'auth.open_session(text, text, uuid, timestamptz, timestamptz)',
    'auth._lock_login(uuid)',
    'auth.begin_request(text)',
    'auth.current_principal()',
    'auth.session_issuer()',
    'auth.revoke_session(text)',
    'auth.revoke_on_principal_change()',
    'auth.revoke_login(text, text)',
    'auth.session_still_open(text, timestamptz)',
    'auth._last_sa_guard()',
    'auth._last_sa_truncate_guard()',
    'auth.location_partner(uuid)',
    'auth.tenant_bound_elsewhere(uuid, uuid)'
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
  EXECUTE format('REVOKE ALL ON FUNCTION auth.open_session(text, text, uuid, timestamptz, timestamptz) FROM %I', owner_name);
  -- The migration user keeps SELECT on login_cutoff. Writes stay on the definer,
  -- which owns revoke_login after this step. Scratch databases skip this file,
  -- so their owner-run functions still have the table privileges.
  EXECUTE format('REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON TABLE auth.login_cutoff FROM %I', owner_name);
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = current_database() || '_issuer') THEN
    EXECUTE format('GRANT EXECUTE ON FUNCTION auth.session_still_open(text, timestamptz) TO %I', current_database() || '_issuer');
  END IF;
  -- The last-SA triggers stay on app_user and admin_assignment. The owner keeps
  -- TRUNCATE on those tables so the guard runs. EXECUTE lets the trigger fire.
  -- It does not allow CREATE OR REPLACE or ALTER OWNER.
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth._last_sa_guard(), auth._last_sa_truncate_guard() TO %I', owner_name);
  -- LoginConfigApi calls these as admin_api. ALTER OWNER keeps an explicit GRANT.
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = current_database() || '_admin_api') THEN
    EXECUTE format(
      'GRANT EXECUTE ON FUNCTION auth.location_partner(uuid), auth.tenant_bound_elsewhere(uuid, uuid) TO %I',
      current_database() || '_admin_api');
  END IF;
  EXECUTE format('ALTER FUNCTION auth._login_cutoff_user_delete() OWNER TO %I', r);
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth._login_cutoff_user_delete() TO %I', owner_name);
  EXECUTE format('GRANT DELETE ON TABLE auth.login_cutoff TO %I', r);
  EXECUTE 'DROP POLICY IF EXISTS login_cutoff_owner ON auth.login_cutoff';
  EXECUTE format(
    'CREATE POLICY login_cutoff_owner ON auth.login_cutoff FOR SELECT TO %I USING (true)',
    owner_name);
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

-- A2 plus the two TL overrides. <db>_auth_owner owns every auth table, schemas
-- auth and public, auth.resolver_read_seq, and public.__EFMigrationsHistory.
-- The migrator keeps SELECT on auth tables and DML (not TRUNCATE) on the history
-- table. It has no INSERT, UPDATE, DELETE, or TRUNCATE on auth or catalog.
-- DEC-229 / A2 line 110's TRUNCATE exception is overridden: no TRUNCATE grant.
DO $auth_owner$
DECLARE
  o text := current_database() || '_auth_owner';
  d text := current_database() || '_auth_definer';
  api text := current_database() || '_admin_api';
  login_role text := current_database() || '_login';
  owner_name text;
  t text;
  auth_tables text[] := ARRAY[
    'admin_assignment', 'app_user', 'audit_outbox', 'external_login', 'location',
    'location_login_config', 'location_login_config_change', 'login_cutoff',
    'login_write_principal', 'oidc_replay', 'partner_reason_bounds',
    'request_binding', 'request_session', 'session', 'user_issuer', 'user_location'
  ];
  pol text;
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_roles
     WHERE rolname = o AND NOT rolcanlogin AND NOT rolinherit AND NOT rolsuper
       AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication
  ) THEN
    RAISE EXCEPTION 'auth owner role % must exist, NOLOGIN NOINHERIT, and not a superuser', o;
  END IF;
  IF EXISTS (SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid IN (m.roleid, m.member) WHERE r.rolname = o) THEN
    RAISE EXCEPTION 'auth owner role % must have no memberships', o;
  END IF;
  SELECT pg_get_userbyid(datdba)::text INTO owner_name
    FROM pg_database WHERE datname = current_database();
  EXECUTE format('ALTER SCHEMA auth OWNER TO %I', o);
  EXECUTE format('ALTER SCHEMA public OWNER TO %I', o);
  EXECUTE format('GRANT USAGE, CREATE ON SCHEMA auth, public TO %I', owner_name);
  EXECUTE format('ALTER SEQUENCE auth.resolver_read_seq OWNER TO %I', o);
  EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE auth.resolver_read_seq TO %I', d);
  EXECUTE format('ALTER TABLE public."__EFMigrationsHistory" OWNER TO %I', o);
  EXECUTE 'ALTER TABLE public."__EFMigrationsHistory" ENABLE ROW LEVEL SECURITY';
  EXECUTE 'ALTER TABLE public."__EFMigrationsHistory" FORCE ROW LEVEL SECURITY';
  EXECUTE format('REVOKE ALL ON TABLE public."__EFMigrationsHistory" FROM PUBLIC, %I', owner_name);
  EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public."__EFMigrationsHistory" TO %I', owner_name);
  FOREACH pol IN ARRAY ARRAY['select','insert','update','delete'] LOOP
    EXECUTE format('DROP POLICY IF EXISTS %I ON public."__EFMigrationsHistory"', 'ef_history_migrator_' || pol);
  END LOOP;
  EXECUTE format('CREATE POLICY ef_history_migrator_select ON public."__EFMigrationsHistory" FOR SELECT TO %I USING (true)', owner_name);
  EXECUTE format('CREATE POLICY ef_history_migrator_insert ON public."__EFMigrationsHistory" FOR INSERT TO %I WITH CHECK (true)', owner_name);
  EXECUTE format('CREATE POLICY ef_history_migrator_update ON public."__EFMigrationsHistory" FOR UPDATE TO %I USING (true) WITH CHECK (true)', owner_name);
  EXECUTE format('CREATE POLICY ef_history_migrator_delete ON public."__EFMigrationsHistory" FOR DELETE TO %I USING (true)', owner_name);
  FOREACH t IN ARRAY auth_tables LOOP
    EXECUTE format('ALTER TABLE auth.%I OWNER TO %I', t, o);
    EXECUTE format('ALTER TABLE auth.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('ALTER TABLE auth.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('REVOKE ALL ON TABLE auth.%I FROM PUBLIC', t);
    EXECUTE format('REVOKE ALL ON TABLE auth.%I FROM %I', t, owner_name);
    EXECUTE format('GRANT SELECT ON TABLE auth.%I TO %I', t, owner_name);
    EXECUTE format('DROP POLICY IF EXISTS %I ON auth.%I', t || '_owner', t);
    EXECUTE 'DROP POLICY IF EXISTS llc_owner ON auth.location_login_config';
    EXECUTE 'DROP POLICY IF EXISTS llcc_owner ON auth.location_login_config_change';
    EXECUTE 'DROP POLICY IF EXISTS prb_owner ON auth.partner_reason_bounds';
    EXECUTE format('DROP POLICY IF EXISTS %I ON auth.%I', t || '_owner_select', t);
    EXECUTE format('DROP POLICY IF EXISTS %I ON auth.%I', t || '_owner_insert', t);
    EXECUTE format('DROP POLICY IF EXISTS %I ON auth.%I', t || '_owner_update', t);
    EXECUTE format('DROP POLICY IF EXISTS %I ON auth.%I', t || '_owner_delete', t);
    EXECUTE format('CREATE POLICY %I ON auth.%I FOR SELECT TO %I USING (true)', t || '_owner', t, owner_name);
  END LOOP;
  -- Definer grants, re-asserted after ALTER OWNER. Per-command policies replace FOR ALL.
  EXECUTE format('GRANT SELECT ON auth.app_user, auth.user_issuer, auth.external_login, auth.admin_assignment, auth.location TO %I', d);
  EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON auth.login_cutoff, auth.request_session, auth.request_binding TO %I', d);
  EXECUTE format('GRANT SELECT, INSERT, UPDATE ON auth.partner_reason_bounds TO %I', d);
  EXECUTE format('GRANT SELECT, INSERT ON auth.login_write_principal TO %I', d);
  EXECUTE format('GRANT SELECT, UPDATE ON auth.location_login_config TO %I', d);
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth._last_sa_guard(), auth._last_sa_truncate_guard() TO %I', owner_name);
  PERFORM 1;
  -- Split the definer FOR ALL policies into the commands the functions use.
  EXECUTE 'DROP POLICY IF EXISTS login_cutoff_definer ON auth.login_cutoff';
  EXECUTE format('DROP POLICY IF EXISTS login_cutoff_definer_select ON auth.login_cutoff');
  EXECUTE format('DROP POLICY IF EXISTS login_cutoff_definer_insert ON auth.login_cutoff');
  EXECUTE format('DROP POLICY IF EXISTS login_cutoff_definer_update ON auth.login_cutoff');
  EXECUTE format('DROP POLICY IF EXISTS login_cutoff_definer_delete ON auth.login_cutoff');
  EXECUTE format('CREATE POLICY login_cutoff_definer_select ON auth.login_cutoff FOR SELECT TO %I USING (true)', d);
  EXECUTE format('CREATE POLICY login_cutoff_definer_insert ON auth.login_cutoff FOR INSERT TO %I WITH CHECK (true)', d);
  EXECUTE format('CREATE POLICY login_cutoff_definer_update ON auth.login_cutoff FOR UPDATE TO %I USING (true) WITH CHECK (true)', d);
  EXECUTE format('CREATE POLICY login_cutoff_definer_delete ON auth.login_cutoff FOR DELETE TO %I USING (true)', d);
  EXECUTE 'DROP POLICY IF EXISTS request_session_definer ON auth.request_session';
  EXECUTE format('DROP POLICY IF EXISTS request_session_definer_select ON auth.request_session');
  EXECUTE format('DROP POLICY IF EXISTS request_session_definer_insert ON auth.request_session');
  EXECUTE format('DROP POLICY IF EXISTS request_session_definer_update ON auth.request_session');
  EXECUTE format('DROP POLICY IF EXISTS request_session_definer_delete ON auth.request_session');
  EXECUTE format('CREATE POLICY request_session_definer_select ON auth.request_session FOR SELECT TO %I USING (true)', d);
  EXECUTE format('CREATE POLICY request_session_definer_insert ON auth.request_session FOR INSERT TO %I WITH CHECK (true)', d);
  EXECUTE format('CREATE POLICY request_session_definer_update ON auth.request_session FOR UPDATE TO %I USING (true) WITH CHECK (true)', d);
  EXECUTE format('CREATE POLICY request_session_definer_delete ON auth.request_session FOR DELETE TO %I USING (true)', d);
  EXECUTE 'DROP POLICY IF EXISTS request_binding_definer ON auth.request_binding';
  EXECUTE format('DROP POLICY IF EXISTS request_binding_definer_select ON auth.request_binding');
  EXECUTE format('DROP POLICY IF EXISTS request_binding_definer_insert ON auth.request_binding');
  EXECUTE format('DROP POLICY IF EXISTS request_binding_definer_update ON auth.request_binding');
  EXECUTE format('DROP POLICY IF EXISTS request_binding_definer_delete ON auth.request_binding');
  EXECUTE format('CREATE POLICY request_binding_definer_select ON auth.request_binding FOR SELECT TO %I USING (true)', d);
  EXECUTE format('CREATE POLICY request_binding_definer_insert ON auth.request_binding FOR INSERT TO %I WITH CHECK (true)', d);
  EXECUTE format('CREATE POLICY request_binding_definer_update ON auth.request_binding FOR UPDATE TO %I USING (true) WITH CHECK (true)', d);
  EXECUTE format('CREATE POLICY request_binding_definer_delete ON auth.request_binding FOR DELETE TO %I USING (true)', d);
  EXECUTE 'DROP POLICY IF EXISTS prb_definer ON auth.partner_reason_bounds';
  EXECUTE 'DROP POLICY IF EXISTS prb_definer_select ON auth.partner_reason_bounds';
  EXECUTE 'DROP POLICY IF EXISTS prb_definer_insert ON auth.partner_reason_bounds';
  EXECUTE 'DROP POLICY IF EXISTS prb_definer_update ON auth.partner_reason_bounds';
  EXECUTE format('CREATE POLICY prb_definer_select ON auth.partner_reason_bounds FOR SELECT TO %I USING (true)', d);
  EXECUTE format('CREATE POLICY prb_definer_insert ON auth.partner_reason_bounds FOR INSERT TO %I WITH CHECK (true)', d);
  EXECUTE format('CREATE POLICY prb_definer_update ON auth.partner_reason_bounds FOR UPDATE TO %I USING (true) WITH CHECK (true)', d);
  EXECUTE 'DROP POLICY IF EXISTS login_write_principal_definer ON auth.login_write_principal';
  EXECUTE 'DROP POLICY IF EXISTS login_write_principal_definer_select ON auth.login_write_principal';
  EXECUTE 'DROP POLICY IF EXISTS login_write_principal_definer_insert ON auth.login_write_principal';
  EXECUTE format('CREATE POLICY login_write_principal_definer_select ON auth.login_write_principal FOR SELECT TO %I USING (true)', d);
  EXECUTE format('CREATE POLICY login_write_principal_definer_insert ON auth.login_write_principal FOR INSERT TO %I WITH CHECK (true)', d);
  EXECUTE 'DROP POLICY IF EXISTS app_user_definer ON auth.app_user';
  EXECUTE format('CREATE POLICY app_user_definer ON auth.app_user FOR SELECT TO %I USING (true)', d);
  EXECUTE 'DROP POLICY IF EXISTS external_login_definer ON auth.external_login';
  EXECUTE format('CREATE POLICY external_login_definer ON auth.external_login FOR SELECT TO %I USING (true)', d);
  -- admin_api: split FOR ALL, keep the predicates and the grants.
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = api) THEN
    EXECUTE format('GRANT SELECT, INSERT ON auth.location TO %I', api);
    EXECUTE format('GRANT UPDATE (name) ON auth.location TO %I', api);
    EXECUTE format('REVOKE UPDATE ON TABLE auth.location_login_config FROM %I', api);
    EXECUTE format('GRANT SELECT, INSERT ON auth.location_login_config TO %I', api);
    EXECUTE format('GRANT SELECT, INSERT ON auth.location_login_config_change TO %I', api);
    EXECUTE format('GRANT SELECT, INSERT ON auth.partner_reason_bounds TO %I', api);
    EXECUTE format('GRANT UPDATE (reason_min_length, reason_max_length, updated_by, updated_at, row_version) ON auth.partner_reason_bounds TO %I', api);
    EXECUTE format('GRANT INSERT ON auth.audit_outbox TO %I', api);
    EXECUTE 'DROP POLICY IF EXISTS location_admin ON auth.location';
    EXECUTE 'DROP POLICY IF EXISTS location_admin_select ON auth.location';
    EXECUTE 'DROP POLICY IF EXISTS location_admin_insert ON auth.location';
    EXECUTE 'DROP POLICY IF EXISTS location_admin_update ON auth.location';
    EXECUTE format('CREATE POLICY location_admin_select ON auth.location FOR SELECT TO %I USING (auth.actor_can_location(partner_id, location_id))', api);
    EXECUTE format('CREATE POLICY location_admin_insert ON auth.location FOR INSERT TO %I WITH CHECK (auth.actor_can_location(partner_id, location_id))', api);
    EXECUTE format('CREATE POLICY location_admin_update ON auth.location FOR UPDATE TO %I USING (auth.actor_can_location(partner_id, location_id)) WITH CHECK (auth.actor_can_location(partner_id, location_id))', api);
    EXECUTE 'DROP POLICY IF EXISTS llc_admin ON auth.location_login_config';
    EXECUTE 'DROP POLICY IF EXISTS llc_admin_select ON auth.location_login_config';
    EXECUTE 'DROP POLICY IF EXISTS llc_admin_insert ON auth.location_login_config';
    EXECUTE format($p$CREATE POLICY llc_admin_select ON auth.location_login_config FOR SELECT TO %I USING (EXISTS (SELECT 1 FROM auth.location l WHERE l.location_id = location_login_config.location_id))$p$, api);
    EXECUTE format($p$CREATE POLICY llc_admin_insert ON auth.location_login_config FOR INSERT TO %I WITH CHECK (EXISTS (SELECT 1 FROM auth.location l WHERE l.location_id = location_login_config.location_id))$p$, api);
    EXECUTE 'DROP POLICY IF EXISTS llcc_admin ON auth.location_login_config_change';
    EXECUTE 'DROP POLICY IF EXISTS llcc_admin_select ON auth.location_login_config_change';
    EXECUTE 'DROP POLICY IF EXISTS llcc_admin_insert ON auth.location_login_config_change';
    EXECUTE format($p$CREATE POLICY llcc_admin_select ON auth.location_login_config_change FOR SELECT TO %I USING (EXISTS (SELECT 1 FROM auth.location l WHERE l.location_id = location_login_config_change.location_id))$p$, api);
    EXECUTE format($p$CREATE POLICY llcc_admin_insert ON auth.location_login_config_change FOR INSERT TO %I WITH CHECK (EXISTS (SELECT 1 FROM auth.location l WHERE l.location_id = location_login_config_change.location_id))$p$, api);
    EXECUTE 'DROP POLICY IF EXISTS audit_outbox_admin ON auth.audit_outbox';
    EXECUTE format('CREATE POLICY audit_outbox_admin ON auth.audit_outbox FOR INSERT TO %I WITH CHECK (true)', api);
  END IF;
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = login_role) THEN
    EXECUTE format('GRANT SELECT ON auth.location, auth.location_login_config TO %I', login_role);
    EXECUTE format('GRANT SELECT ON auth.app_user, auth.external_login, auth.user_location TO %I', login_role);
    EXECUTE format('GRANT INSERT ON auth.audit_outbox TO %I', login_role);
    EXECUTE format('GRANT SELECT, INSERT, DELETE ON auth.oidc_replay TO %I', login_role);
    EXECUTE format('GRANT SELECT, INSERT, DELETE ON auth.session TO %I', login_role);
    EXECUTE format('GRANT UPDATE (last_seen_at, revoked_at, revoked_reason, interactive_used_at, handoff_used_at, cage_consumed_at, code_issued) ON auth.session TO %I', login_role);
    EXECUTE 'DROP POLICY IF EXISTS app_user_login ON auth.app_user';
    EXECUTE format('CREATE POLICY app_user_login ON auth.app_user FOR SELECT TO %I USING (true)', login_role);
    EXECUTE 'DROP POLICY IF EXISTS external_login_login ON auth.external_login';
    EXECUTE format('CREATE POLICY external_login_login ON auth.external_login FOR SELECT TO %I USING (true)', login_role);
    EXECUTE 'DROP POLICY IF EXISTS user_location_login ON auth.user_location';
    EXECUTE format('CREATE POLICY user_location_login ON auth.user_location FOR SELECT TO %I USING (true)', login_role);
    EXECUTE 'DROP POLICY IF EXISTS audit_outbox_login ON auth.audit_outbox';
    EXECUTE format('CREATE POLICY audit_outbox_login ON auth.audit_outbox FOR INSERT TO %I WITH CHECK (true)', login_role);
    EXECUTE 'DROP POLICY IF EXISTS oidc_replay_login_select ON auth.oidc_replay';
    EXECUTE 'DROP POLICY IF EXISTS oidc_replay_login_insert ON auth.oidc_replay';
    EXECUTE 'DROP POLICY IF EXISTS oidc_replay_login_delete ON auth.oidc_replay';
    EXECUTE format('CREATE POLICY oidc_replay_login_select ON auth.oidc_replay FOR SELECT TO %I USING (true)', login_role);
    EXECUTE format('CREATE POLICY oidc_replay_login_insert ON auth.oidc_replay FOR INSERT TO %I WITH CHECK (true)', login_role);
    EXECUTE format('CREATE POLICY oidc_replay_login_delete ON auth.oidc_replay FOR DELETE TO %I USING (true)', login_role);
    EXECUTE 'DROP POLICY IF EXISTS session_login_select ON auth.session';
    EXECUTE 'DROP POLICY IF EXISTS session_login_insert ON auth.session';
    EXECUTE 'DROP POLICY IF EXISTS session_login_delete ON auth.session';
    EXECUTE 'DROP POLICY IF EXISTS session_login_update ON auth.session';
    EXECUTE format('CREATE POLICY session_login_select ON auth.session FOR SELECT TO %I USING (true)', login_role);
    EXECUTE format('CREATE POLICY session_login_insert ON auth.session FOR INSERT TO %I WITH CHECK (true)', login_role);
    EXECUTE format('CREATE POLICY session_login_delete ON auth.session FOR DELETE TO %I USING (true)', login_role);
    EXECUTE format('CREATE POLICY session_login_update ON auth.session FOR UPDATE TO %I USING (true) WITH CHECK (true)', login_role);
  END IF;
END
$auth_owner$;
