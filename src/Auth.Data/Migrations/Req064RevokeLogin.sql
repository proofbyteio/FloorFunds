-- Sign-out revokes every live request session for that user. Only the issuer
-- role can call this. The database owner and admin_api cannot.

CREATE OR REPLACE FUNCTION auth.revoke_login(p_iss text, p_sub text) RETURNS void
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
DECLARE
  v_user uuid;
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
  UPDATE auth.request_session
     SET revoked_at = clock_timestamp()
   WHERE user_id = v_user
     AND revoked_at IS NULL;
END
$fn$;

REVOKE ALL ON FUNCTION auth.revoke_login(text, text) FROM PUBLIC;

DO $rev$
DECLARE
  a text := current_database() || '_admin_api';
  i text := current_database() || '_issuer';
  owner_name text;
BEGIN
  SELECT pg_get_userbyid(datdba)::text INTO owner_name
    FROM pg_database WHERE datname = current_database();
  EXECUTE format('REVOKE ALL ON FUNCTION auth.revoke_login(text, text) FROM %I', owner_name);
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = a) THEN
    EXECUTE format('REVOKE ALL ON FUNCTION auth.revoke_login(text, text) FROM %I', a);
  END IF;
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = i AND rolcanlogin AND NOT rolsuper) THEN
    EXECUTE format('GRANT EXECUTE ON FUNCTION auth.revoke_login(text, text) TO %I', i);
  END IF;
END
$rev$;
