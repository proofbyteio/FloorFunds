-- One login lock and a logout cutoff. The shipped 20261110290000 function replaced an
-- expired row for the same jti. This migration drops that replace. A database that
-- already applied 20261110290000 or 20261110300000 gets the same functions through
-- CREATE OR REPLACE. The old migration text is left as history.

CREATE TABLE IF NOT EXISTS auth.login_cutoff (
  user_id uuid PRIMARY KEY,
  revoked_before timestamptz NOT NULL
);
-- No foreign key. A KEY SHARE check on delete of app_user would require the
-- migration user to hold UPDATE on this table. The definer trigger removes the row.
ALTER TABLE auth.login_cutoff DROP CONSTRAINT IF EXISTS login_cutoff_user_id_fkey;
REVOKE ALL ON TABLE auth.login_cutoff FROM PUBLIC;

CREATE OR REPLACE FUNCTION auth._login_cutoff_user_delete() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
BEGIN
  DELETE FROM auth.login_cutoff WHERE user_id = OLD.user_id;
  RETURN OLD;
END
$fn$;

REVOKE ALL ON FUNCTION auth._login_cutoff_user_delete() FROM PUBLIC;

DROP TRIGGER IF EXISTS app_user_login_cutoff_delete ON auth.app_user;
CREATE TRIGGER app_user_login_cutoff_delete
  BEFORE DELETE ON auth.app_user
  FOR EACH ROW EXECUTE FUNCTION auth._login_cutoff_user_delete();

ALTER TABLE auth.login_cutoff ENABLE ROW LEVEL SECURITY;
ALTER TABLE auth.login_cutoff FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS login_cutoff_owner ON auth.login_cutoff;
CREATE POLICY login_cutoff_owner ON auth.login_cutoff
  FOR ALL TO CURRENT_USER USING (true) WITH CHECK (true);

CREATE OR REPLACE FUNCTION auth._lock_login(p_user uuid)
  RETURNS void
  LANGUAGE plpgsql
  SET search_path = pg_catalog
AS $fn$
BEGIN
  IF p_user IS NULL THEN
    RETURN;
  END IF;
  -- 814064213 is this login lock. 814064204 stays the last-Super-Admin lock.
  -- The second key is the user. A hash collision waits two logins together.
  PERFORM pg_advisory_xact_lock(814064213, hashtext(p_user::text));
END
$fn$;

REVOKE ALL ON FUNCTION auth._lock_login(uuid) FROM PUBLIC;

CREATE OR REPLACE FUNCTION auth.open_session(
  p_iss text, p_sub text, p_jti uuid, p_exp timestamptz, p_iat timestamptz)
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
     OR p_sub IS NULL OR char_length(p_sub) < 1 OR p_jti IS NULL OR p_iat IS NULL THEN
    RAISE EXCEPTION 'session_denied' USING ERRCODE = '42501';
  END IF;
  IF p_exp IS NULL OR p_exp <= v_created THEN
    RAISE EXCEPTION 'session_expired' USING ERRCODE = '42501';
  END IF;
  SELECT u.user_id INTO v_user
    FROM auth.app_user u
    JOIN auth.user_issuer i ON i.user_id = u.user_id AND i.issuer = p_iss
   WHERE u.user_id::text = p_sub;
  IF v_user IS NULL THEN
    SELECT u.user_id INTO v_user
      FROM auth.external_login e
      JOIN auth.app_user u ON u.user_id = e.user_id
      JOIN auth.user_issuer i ON i.user_id = u.user_id AND i.issuer = e.issuer
     WHERE e.issuer = p_iss
       AND e.subject::text = p_sub;
  END IF;
  IF v_user IS NULL THEN
    RAISE EXCEPTION 'session_denied' USING ERRCODE = '42501';
  END IF;
  PERFORM auth._lock_login(v_user);
  -- Status is read again after the lock, against v_created. A lock that was in
  -- force when this open started still denies after locked_until passes while
  -- we wait, and that open does not insert a row.
  IF NOT EXISTS (SELECT 1 FROM auth.app_user u WHERE u.user_id = v_user AND u.status = 'active' AND (u.locked_until IS NULL OR u.locked_until <= v_created)) THEN
    RAISE EXCEPTION 'session_denied' USING ERRCODE = '42501';
  END IF;
  -- One second is the remaining future-iat window. A later iat is not proof
  -- the token was issued after logout.
  IF p_iat > clock_timestamp() + interval '1 second' THEN
    RAISE EXCEPTION 'session_denied' USING ERRCODE = '42501';
  END IF;
  IF EXISTS (
    SELECT 1 FROM auth.login_cutoff c
     WHERE c.user_id = v_user
       AND p_iat <= c.revoked_before
  ) THEN
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
    -- One jti is one row. An expired row is not replaced.
    RAISE EXCEPTION 'jti_reused' USING ERRCODE = '23505';
  END;
  RETURN v_token;
END
$fn$;

DROP FUNCTION IF EXISTS auth.open_session(text, text, uuid, timestamptz);

-- The shipped revoke_login revoked EXECUTE from the database owner. Replacing
-- the function needs that privilege back for this statement only.
GRANT EXECUTE ON FUNCTION auth.revoke_login(text, text) TO CURRENT_USER;

CREATE OR REPLACE FUNCTION auth.revoke_login(p_iss text, p_sub text) RETURNS void
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
DECLARE
  v_user uuid;
  v_now timestamptz;
BEGIN
  IF p_iss IS NULL OR p_sub IS NULL OR char_length(p_iss) < 1 OR char_length(p_sub) < 1 THEN
    RETURN;
  END IF;
  SELECT u.user_id INTO v_user
    FROM auth.app_user u
    JOIN auth.user_issuer i ON i.user_id = u.user_id AND i.issuer = p_iss
   WHERE u.user_id::text = p_sub;
  IF v_user IS NULL THEN
    SELECT u.user_id INTO v_user
      FROM auth.external_login e
      JOIN auth.app_user u ON u.user_id = e.user_id
      JOIN auth.user_issuer i ON i.user_id = u.user_id AND i.issuer = e.issuer
     WHERE e.issuer = p_iss
       AND e.subject::text = p_sub;
  END IF;
  IF v_user IS NULL THEN
    RETURN;
  END IF;
  PERFORM auth._lock_login(v_user);
  v_now := clock_timestamp();
  INSERT INTO auth.login_cutoff (user_id, revoked_before)
  VALUES (v_user, v_now)
  ON CONFLICT (user_id) DO UPDATE SET revoked_before = EXCLUDED.revoked_before;
  UPDATE auth.request_session
     SET revoked_at = v_now
   WHERE user_id = v_user
     AND revoked_at IS NULL;
END
$fn$;

CREATE OR REPLACE FUNCTION auth.revoke_on_principal_change() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
DECLARE
  v_user uuid;
  v_old uuid;
  v_new uuid;
BEGIN
  IF TG_TABLE_NAME = 'app_user' THEN
    IF NEW.status IS DISTINCT FROM OLD.status OR NEW.locked_until IS DISTINCT FROM OLD.locked_until THEN
      PERFORM auth._lock_login(NEW.user_id);
      UPDATE auth.request_session SET revoked_at = clock_timestamp()
       WHERE user_id = NEW.user_id AND revoked_at IS NULL;
    END IF;
    RETURN NULL;
  END IF;
  v_user := CASE WHEN TG_OP = 'DELETE' THEN OLD.user_id ELSE NEW.user_id END;
  IF TG_OP = 'UPDATE' AND NEW.user_id IS DISTINCT FROM OLD.user_id THEN
    v_old := OLD.user_id;
    v_new := NEW.user_id;
    IF v_old < v_new THEN
      PERFORM auth._lock_login(v_old);
      PERFORM auth._lock_login(v_new);
    ELSE
      PERFORM auth._lock_login(v_new);
      PERFORM auth._lock_login(v_old);
    END IF;
  ELSE
    PERFORM auth._lock_login(v_user);
  END IF;
  UPDATE auth.request_session SET revoked_at = clock_timestamp()
   WHERE user_id = v_user AND revoked_at IS NULL;
  IF TG_OP = 'UPDATE' AND NEW.user_id IS DISTINCT FROM OLD.user_id THEN
    UPDATE auth.request_session SET revoked_at = clock_timestamp()
     WHERE user_id = OLD.user_id AND revoked_at IS NULL;
  END IF;
  RETURN NULL;
END
$fn$;

-- A cached issuer session is replayed only while this is still true. A revoked
-- row, an expired row, or an iat at or before login_cutoff is not served from
-- the cache. A null iat is denied: NULL <= revoked_before is unknown, so the
-- cutoff comparison would otherwise treat a logged-out user as still open.
CREATE OR REPLACE FUNCTION auth.session_still_open(p_token text, p_iat timestamptz) RETURNS boolean
  LANGUAGE plpgsql
  STABLE
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
BEGIN
  IF p_iat IS NULL THEN RAISE EXCEPTION 'session_denied' USING ERRCODE = '42501'; END IF;
  RETURN EXISTS (
    SELECT 1
      FROM auth.request_session s
     WHERE s.token_hash = sha256(convert_to(p_token, 'UTF8'))
       AND s.revoked_at IS NULL
       AND s.expires_at > clock_timestamp()
       AND NOT EXISTS (
         SELECT 1 FROM auth.login_cutoff c
          WHERE c.user_id = s.user_id
            AND p_iat <= c.revoked_before
       )
  );
END
$fn$;

REVOKE ALL ON FUNCTION auth.session_still_open(text, timestamptz) FROM PUBLIC;

REVOKE ALL ON FUNCTION auth.open_session(text, text, uuid, timestamptz, timestamptz) FROM PUBLIC;

DO $close$
DECLARE
  a text := current_database() || '_admin_api';
  l text := current_database() || '_login';
  i text := current_database() || '_issuer';
  r text := current_database() || '_auth_definer';
  owner_name text;
BEGIN
  SELECT pg_get_userbyid(datdba)::text INTO owner_name
    FROM pg_database WHERE datname = current_database();
  EXECUTE format('REVOKE ALL ON FUNCTION auth.open_session(text, text, uuid, timestamptz, timestamptz) FROM %I', owner_name);
  -- The database owner keeps EXECUTE on _lock_login. Scratch databases do not run
  -- the definer-owner step, so the trigger still runs as that owner. The kit
  -- definer also receives EXECUTE below, for after that step changes the owner.
  EXECUTE format('REVOKE ALL ON FUNCTION auth.revoke_login(text, text) FROM %I', owner_name);
  -- login_cutoff has no foreign key. The last-active-super-admin guard is the
  -- trigger on app_user and admin_assignment. TRUNCATE here would erase every
  -- cutoff and let a pre-logout iat open, so the owner does not keep it.
  EXECUTE format('REVOKE TRUNCATE ON TABLE auth.login_cutoff FROM %I', owner_name);
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = a) THEN
    EXECUTE format('REVOKE ALL ON FUNCTION auth.open_session(text, text, uuid, timestamptz, timestamptz) FROM %I', a);
    EXECUTE format('REVOKE ALL ON TABLE auth.login_cutoff FROM %I', a);
  END IF;
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = l) THEN
    EXECUTE format('REVOKE ALL ON FUNCTION auth.open_session(text, text, uuid, timestamptz, timestamptz) FROM %I', l);
    EXECUTE format('REVOKE ALL ON TABLE auth.login_cutoff FROM %I', l);
  END IF;
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = i AND rolcanlogin AND NOT rolsuper) THEN
    EXECUTE format('GRANT EXECUTE ON FUNCTION auth.open_session(text, text, uuid, timestamptz, timestamptz) TO %I', i);
    EXECUTE format('GRANT EXECUTE ON FUNCTION auth.revoke_login(text, text) TO %I', i);
    EXECUTE format('GRANT EXECUTE ON FUNCTION auth.session_still_open(text, timestamptz) TO %I', i);
  END IF;
  IF EXISTS (
    SELECT 1 FROM pg_roles
     WHERE rolname = r AND NOT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls
  ) THEN
    EXECUTE format('GRANT EXECUTE ON FUNCTION auth._lock_login(uuid) TO %I', r);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON auth.login_cutoff TO %I', r);
    EXECUTE 'DROP POLICY IF EXISTS login_cutoff_definer ON auth.login_cutoff';
    EXECUTE format('CREATE POLICY login_cutoff_definer ON auth.login_cutoff FOR ALL TO %I USING (true) WITH CHECK (true)', r);
  END IF;
END
$close$;
