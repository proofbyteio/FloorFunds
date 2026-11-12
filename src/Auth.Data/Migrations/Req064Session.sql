-- DEC-213. The request principal is a session bound to this backend and this
-- transaction. app.request_iss and app.request_sub are not read. The raw token
-- is never stored. admin_api cannot call open_session. The issuer role can,
-- when the kit created it. A scratch database creates that issuer role before
-- this migration so the owner is not granted open_session.

CREATE TABLE auth.request_session (
  token_hash bytea PRIMARY KEY,
  jti uuid NOT NULL UNIQUE,
  iss text NOT NULL,
  sub text NOT NULL,
  user_id uuid NOT NULL REFERENCES auth.app_user (user_id),
  created_at timestamptz NOT NULL,
  expires_at timestamptz NOT NULL,
  revoked_at timestamptz,
  CONSTRAINT request_session_ttl CHECK (expires_at <= created_at + interval '15 minutes'),
  CONSTRAINT request_session_iss_len CHECK (char_length(iss) BETWEEN 1 AND 500),
  CONSTRAINT request_session_sub_len CHECK (char_length(sub) BETWEEN 1 AND 500)
);
CREATE TABLE auth.request_binding (
  backend_pid integer NOT NULL,
  xact_id xid8 NOT NULL,
  token_hash bytea NOT NULL REFERENCES auth.request_session (token_hash),
  user_id uuid NOT NULL,
  PRIMARY KEY (backend_pid, xact_id)
);
REVOKE ALL ON TABLE auth.request_session, auth.request_binding FROM PUBLIC;

ALTER TABLE auth.request_session ENABLE ROW LEVEL SECURITY;
ALTER TABLE auth.request_session FORCE ROW LEVEL SECURITY;
ALTER TABLE auth.request_binding ENABLE ROW LEVEL SECURITY;
ALTER TABLE auth.request_binding FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS request_session_owner ON auth.request_session;
CREATE POLICY request_session_owner ON auth.request_session
  FOR ALL TO CURRENT_USER USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS request_binding_owner ON auth.request_binding;
CREATE POLICY request_binding_owner ON auth.request_binding
  FOR ALL TO CURRENT_USER USING (true) WITH CHECK (true);

-- 32 live sessions per principal. 600 opens in a minute. Both are fixed here.
CREATE OR REPLACE FUNCTION auth.open_session(p_iss text, p_sub text, p_jti uuid, p_exp timestamptz)
  RETURNS text
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
DECLARE
  v_user uuid;
  v_created timestamptz := clock_timestamp();
  v_exp timestamptz;
  v_token text;
  v_hash bytea;
BEGIN
  IF p_iss IS NULL OR char_length(p_iss) < 1 OR char_length(p_iss) > 500
     OR p_sub IS NULL OR char_length(p_sub) < 1 OR p_jti IS NULL THEN
    RAISE EXCEPTION 'session_denied' USING ERRCODE = '42501';
  END IF;
  IF p_exp IS NULL OR p_exp <= v_created THEN
    RAISE EXCEPTION 'session_expired' USING ERRCODE = '42501';
  END IF;
  SELECT u.user_id INTO v_user
    FROM auth.app_user u
    JOIN auth.user_issuer i ON i.user_id = u.user_id AND i.issuer = p_iss
   WHERE u.user_id::text = p_sub
     AND u.status = 'active'
     AND (u.locked_until IS NULL OR u.locked_until <= v_created);
  IF v_user IS NULL THEN
    SELECT u.user_id INTO v_user
      FROM auth.external_login e
      JOIN auth.app_user u ON u.user_id = e.user_id
      JOIN auth.user_issuer i ON i.user_id = u.user_id AND i.issuer = e.issuer
     WHERE e.issuer = p_iss
       AND e.subject::text = p_sub
       AND u.status = 'active'
       AND (u.locked_until IS NULL OR u.locked_until <= v_created);
  END IF;
  IF v_user IS NULL THEN
    RAISE EXCEPTION 'session_denied' USING ERRCODE = '42501';
  END IF;
  IF (SELECT count(*) FROM auth.request_session s
       WHERE s.user_id = v_user AND s.revoked_at IS NULL AND s.expires_at > v_created) >= 32 THEN
    RAISE EXCEPTION 'session_cap' USING ERRCODE = '54000';
  END IF;
  IF (SELECT count(*) FROM auth.request_session s
       WHERE s.user_id = v_user AND s.created_at > v_created - interval '1 minute') >= 600 THEN
    RAISE EXCEPTION 'session_rate' USING ERRCODE = '54000';
  END IF;
  v_exp := least(p_exp, v_created + interval '15 minutes');
  v_token := encode(sha256(convert_to(gen_random_uuid()::text || gen_random_uuid()::text, 'UTF8')), 'hex');
  v_hash := sha256(convert_to(v_token, 'UTF8'));
  BEGIN
    INSERT INTO auth.request_session (token_hash, jti, iss, sub, user_id, created_at, expires_at)
    VALUES (v_hash, p_jti, p_iss, p_sub, v_user, v_created, v_exp);
  EXCEPTION WHEN unique_violation THEN
    -- Same jti, same subject, already expired, not revoked: replace the row.
    -- A live row, a revoked row, or a different subject stays jti_reused.
    DELETE FROM auth.request_binding b
     USING auth.request_session s
     WHERE b.token_hash = s.token_hash
       AND s.jti = p_jti
       AND s.user_id = v_user
       AND s.iss = p_iss
       AND s.sub = p_sub
       AND s.revoked_at IS NULL
       AND s.expires_at <= v_created;
    DELETE FROM auth.request_session s
     WHERE s.jti = p_jti
       AND s.user_id = v_user
       AND s.iss = p_iss
       AND s.sub = p_sub
       AND s.revoked_at IS NULL
       AND s.expires_at <= v_created;
    IF NOT FOUND THEN
      RAISE EXCEPTION 'jti_reused' USING ERRCODE = '23505';
    END IF;
    INSERT INTO auth.request_session (token_hash, jti, iss, sub, user_id, created_at, expires_at)
    VALUES (v_hash, p_jti, p_iss, p_sub, v_user, v_created, v_exp);
  END;
  RETURN v_token;
END
$fn$;

CREATE OR REPLACE FUNCTION auth.begin_request(p_token text) RETURNS void
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
DECLARE
  v_hash bytea := sha256(convert_to(p_token, 'UTF8'));
  v_user uuid;
BEGIN
  SELECT s.user_id INTO v_user
    FROM auth.request_session s
   WHERE s.token_hash = v_hash
     AND s.revoked_at IS NULL
     AND s.expires_at > clock_timestamp();
  IF v_user IS NULL THEN
    -- The row is still checked. Expired and not revoked is a distinct error
    -- so the host can return 401. Revoked and unknown stay session_denied.
    IF EXISTS (
      SELECT 1 FROM auth.request_session s
       WHERE s.token_hash = v_hash
         AND s.revoked_at IS NULL
         AND s.expires_at <= clock_timestamp()
    ) THEN
      RAISE EXCEPTION 'session_expired' USING ERRCODE = '42501';
    END IF;
    RAISE EXCEPTION 'session_denied' USING ERRCODE = '42501';
  END IF;
  BEGIN
    INSERT INTO auth.request_binding (backend_pid, xact_id, token_hash, user_id)
    VALUES (pg_backend_pid(), pg_current_xact_id(), v_hash, v_user);
  EXCEPTION WHEN unique_violation THEN
    RAISE EXCEPTION 'session_rebind' USING ERRCODE = '25001';
  END;
  DELETE FROM auth.request_binding b
   WHERE b.backend_pid = pg_backend_pid()
     AND b.xact_id IS DISTINCT FROM pg_current_xact_id();
  DELETE FROM auth.request_binding b
   WHERE NOT EXISTS (SELECT 1 FROM pg_stat_activity a WHERE a.pid = b.backend_pid);
  DELETE FROM auth.request_session s
   WHERE s.expires_at < clock_timestamp() - interval '1 hour';
END
$fn$;

CREATE OR REPLACE FUNCTION auth.current_principal() RETURNS uuid
  LANGUAGE sql
  STABLE
  PARALLEL RESTRICTED
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
  SELECT s.user_id
    FROM auth.request_binding b
    JOIN auth.request_session s ON s.token_hash = b.token_hash
   WHERE b.backend_pid = pg_backend_pid()
     AND b.xact_id = pg_current_xact_id()
     AND s.revoked_at IS NULL
     AND s.expires_at > clock_timestamp()
$fn$;

CREATE OR REPLACE FUNCTION auth.session_issuer() RETURNS text
  LANGUAGE sql
  STABLE
  PARALLEL RESTRICTED
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
  SELECT s.iss
    FROM auth.request_binding b
    JOIN auth.request_session s ON s.token_hash = b.token_hash
   WHERE b.backend_pid = pg_backend_pid()
     AND b.xact_id = pg_current_xact_id()
     AND s.revoked_at IS NULL
     AND s.expires_at > clock_timestamp()
$fn$;

CREATE OR REPLACE FUNCTION auth.revoke_session(p_token text) RETURNS void
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
BEGIN
  UPDATE auth.request_session
     SET revoked_at = clock_timestamp()
   WHERE token_hash = sha256(convert_to(p_token, 'UTF8'))
     AND revoked_at IS NULL;
END
$fn$;

CREATE OR REPLACE FUNCTION auth._request_user() RETURNS uuid
  LANGUAGE sql
  STABLE
  PARALLEL RESTRICTED
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
  SELECT u.user_id
    FROM auth.app_user u
   WHERE u.user_id = auth.current_principal()
     AND u.status = 'active'
     AND (u.locked_until IS NULL OR u.locked_until <= clock_timestamp())
$fn$;

ALTER FUNCTION auth.actor_is_global() PARALLEL RESTRICTED;
ALTER FUNCTION auth.actor_is_any_admin() PARALLEL RESTRICTED;
ALTER FUNCTION auth.actor_has_any_location() PARALLEL RESTRICTED;
ALTER FUNCTION auth.actor_can_partner(uuid) PARALLEL RESTRICTED;
ALTER FUNCTION auth.actor_location_sees_partner(uuid) PARALLEL RESTRICTED;
ALTER FUNCTION auth.actor_has_location(uuid) PARALLEL RESTRICTED;
ALTER FUNCTION auth.actor_can_location(uuid, uuid) PARALLEL RESTRICTED;
ALTER FUNCTION auth.actor_sees_change(uuid, text, uuid, uuid) PARALLEL RESTRICTED;
ALTER FUNCTION auth.list_existing_partners() PARALLEL RESTRICTED;
ALTER FUNCTION auth.read_partner_bounds(uuid) PARALLEL RESTRICTED;

CREATE OR REPLACE FUNCTION auth.resolve_grant(p_issuer text, p_subject uuid)
  RETURNS TABLE (
    outcome text,
    user_id uuid,
    role_wire text,
    global_admin boolean,
    partner_ids uuid[],
    location_ids uuid[],
    perm_version bigint)
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
DECLARE
  v_user uuid;
  v_global boolean;
  v_partners uuid[];
  v_locations uuid[];
  v_version bigint;
  v_role text;
BEGIN
  -- Arguments are ignored. The binding is the principal. A caller that passes
  -- a different issuer or subject still resolves the bound user.
  PERFORM p_issuer, p_subject;
  PERFORM nextval('auth.resolver_read_seq');
  v_user := auth._request_user();
  IF v_user IS NULL THEN
    outcome := 'deny'; user_id := NULL; role_wire := 'none'; global_admin := false;
    partner_ids := ARRAY[]::uuid[]; location_ids := ARRAY[]::uuid[]; perm_version := 0;
    RETURN NEXT;
    RETURN;
  END IF;
  SELECT coalesce(bool_or(a.role_id = 'super_admin' AND a.scope_kind = 'global'), false),
         coalesce(array_agg(DISTINCT a.partner_id) FILTER (
           WHERE a.scope_kind = 'partner' AND a.role_id = 'partner_admin' AND a.partner_id IS NOT NULL
         ), ARRAY[]::uuid[]),
         coalesce(array_agg(DISTINCT a.location_id) FILTER (
           WHERE a.scope_kind = 'location' AND a.location_id IS NOT NULL
         ), ARRAY[]::uuid[]),
         coalesce(max(a.version), 0)
    INTO v_global, v_partners, v_locations, v_version
    FROM auth.admin_assignment a
   WHERE a.user_id = v_user
     AND a.status = 'active'
     AND a.revoked_at IS NULL;
  IF v_version = 0 AND NOT v_global AND v_partners = ARRAY[]::uuid[] AND v_locations = ARRAY[]::uuid[] THEN
    outcome := 'deny'; user_id := v_user; role_wire := 'none'; global_admin := false;
    partner_ids := ARRAY[]::uuid[]; location_ids := ARRAY[]::uuid[]; perm_version := 0;
    RETURN NEXT;
    RETURN;
  END IF;
  IF v_global THEN
    v_role := 'super_admin';
  ELSIF EXISTS (
    SELECT 1 FROM auth.admin_assignment a
     WHERE a.user_id = v_user AND a.status = 'active' AND a.revoked_at IS NULL
       AND a.role_id = 'partner_admin' AND a.scope_kind = 'partner' AND a.partner_id IS NOT NULL
  ) THEN
    v_role := 'partner_admin';
  ELSE
    SELECT a.role_id INTO v_role
      FROM auth.admin_assignment a
     WHERE a.user_id = v_user AND a.status = 'active' AND a.revoked_at IS NULL
     ORDER BY a.role_id
     LIMIT 1;
    v_role := coalesce(v_role, 'none');
  END IF;
  outcome := 'grant';
  user_id := v_user;
  role_wire := v_role;
  global_admin := v_global;
  partner_ids := v_partners;
  location_ids := v_locations;
  perm_version := v_version;
  RETURN NEXT;
END
$fn$;

CREATE OR REPLACE FUNCTION auth.record_login_write(p_kind text, p_target uuid) RETURNS void
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
DECLARE
  v_iss text;
  v_sub text;
BEGIN
  SELECT s.iss, s.sub INTO v_iss, v_sub
    FROM auth.request_binding b
    JOIN auth.request_session s ON s.token_hash = b.token_hash
   WHERE b.backend_pid = pg_backend_pid()
     AND b.xact_id = pg_current_xact_id()
     AND s.revoked_at IS NULL
     AND s.expires_at > clock_timestamp();
  IF v_iss IS NULL OR v_sub IS NULL
     OR v_sub !~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN
    RAISE EXCEPTION 'login_write_principal' USING ERRCODE = 'P0001';
  END IF;
  INSERT INTO auth.login_write_principal (write_id, kind, target_id, issuer, subject)
  VALUES (gen_random_uuid(), p_kind, p_target, v_iss, v_sub::uuid);
END
$fn$;

CREATE OR REPLACE FUNCTION auth.revoke_on_principal_change() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
DECLARE
  v_user uuid;
BEGIN
  IF TG_TABLE_NAME = 'app_user' THEN
    IF NEW.status IS DISTINCT FROM OLD.status OR NEW.locked_until IS DISTINCT FROM OLD.locked_until THEN
      UPDATE auth.request_session SET revoked_at = clock_timestamp()
       WHERE user_id = NEW.user_id AND revoked_at IS NULL;
    END IF;
    RETURN NULL;
  END IF;
  v_user := CASE WHEN TG_OP = 'DELETE' THEN OLD.user_id ELSE NEW.user_id END;
  UPDATE auth.request_session SET revoked_at = clock_timestamp()
   WHERE user_id = v_user AND revoked_at IS NULL;
  IF TG_OP = 'UPDATE' AND NEW.user_id IS DISTINCT FROM OLD.user_id THEN
    UPDATE auth.request_session SET revoked_at = clock_timestamp()
     WHERE user_id = OLD.user_id AND revoked_at IS NULL;
  END IF;
  RETURN NULL;
END
$fn$;

DROP TRIGGER IF EXISTS app_user_revoke_sessions ON auth.app_user;
CREATE TRIGGER app_user_revoke_sessions
  AFTER UPDATE OF status, locked_until ON auth.app_user
  FOR EACH ROW EXECUTE FUNCTION auth.revoke_on_principal_change();

DROP TRIGGER IF EXISTS admin_assignment_revoke_sessions ON auth.admin_assignment;
CREATE TRIGGER admin_assignment_revoke_sessions
  AFTER INSERT OR UPDATE OR DELETE ON auth.admin_assignment
  FOR EACH ROW EXECUTE FUNCTION auth.revoke_on_principal_change();

REVOKE ALL ON FUNCTION auth.open_session(text, text, uuid, timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth.begin_request(text) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth.current_principal() FROM PUBLIC;
REVOKE ALL ON FUNCTION auth.session_issuer() FROM PUBLIC;
REVOKE ALL ON FUNCTION auth.revoke_session(text) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth.revoke_on_principal_change() FROM PUBLIC;

DO $sess$
DECLARE
  a text := current_database() || '_admin_api';
  l text := current_database() || '_login';
  i text := current_database() || '_issuer';
  r text := current_database() || '_auth_definer';
BEGIN
  -- open_session is not granted to the database owner. Catalog scratch creates
  -- <db>_issuer before this migration, and the block below grants that role.
  -- admin_api is not granted open_session.
  EXECUTE 'GRANT EXECUTE ON FUNCTION auth.begin_request(text), auth.current_principal(), auth.session_issuer(), auth.revoke_session(text) TO CURRENT_USER';
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = a) THEN
    EXECUTE format('REVOKE ALL ON TABLE auth.request_session, auth.request_binding FROM %I', a);
    EXECUTE format('REVOKE ALL ON FUNCTION auth.open_session(text, text, uuid, timestamptz) FROM %I', a);
    EXECUTE format('REVOKE ALL ON FUNCTION auth.revoke_session(text) FROM %I', a);
    EXECUTE format('GRANT EXECUTE ON FUNCTION auth.begin_request(text), auth.current_principal(), auth.session_issuer() TO %I', a);
  END IF;
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = l) THEN
    EXECUTE format('REVOKE ALL ON TABLE auth.request_session, auth.request_binding FROM %I', l);
    EXECUTE format('REVOKE ALL ON FUNCTION auth.open_session(text, text, uuid, timestamptz) FROM %I', l);
    EXECUTE format('REVOKE ALL ON FUNCTION auth.begin_request(text) FROM %I', l);
  END IF;
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = i AND rolcanlogin AND NOT rolsuper) THEN
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), i);
    EXECUTE format('GRANT USAGE ON SCHEMA auth TO %I', i);
    EXECUTE format('GRANT EXECUTE ON FUNCTION auth.open_session(text, text, uuid, timestamptz) TO %I', i);
    EXECUTE format('GRANT EXECUTE ON FUNCTION auth.revoke_session(text) TO %I', i);
  END IF;
  IF EXISTS (
    SELECT 1 FROM pg_roles
     WHERE rolname = r AND NOT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls
  ) THEN
    EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON auth.request_session, auth.request_binding TO %I', r);
    EXECUTE 'DROP POLICY IF EXISTS request_session_definer ON auth.request_session';
    EXECUTE format('CREATE POLICY request_session_definer ON auth.request_session FOR ALL TO %I USING (true) WITH CHECK (true)', r);
    EXECUTE 'DROP POLICY IF EXISTS request_binding_definer ON auth.request_binding';
    EXECUTE format('CREATE POLICY request_binding_definer ON auth.request_binding FOR ALL TO %I USING (true) WITH CHECK (true)', r);
  END IF;
END
$sess$;
