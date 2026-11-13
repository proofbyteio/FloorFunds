-- The sign-in identifier the session already stores. Same row current_principal reads.
CREATE OR REPLACE FUNCTION auth.session_subject() RETURNS text
  LANGUAGE sql
  STABLE
  PARALLEL RESTRICTED
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
  SELECT s.sub
    FROM auth.request_binding b
    JOIN auth.request_session s ON s.token_hash = b.token_hash
   WHERE b.backend_pid = pg_backend_pid()
     AND b.xact_id = pg_current_xact_id()
     AND s.revoked_at IS NULL
     AND s.expires_at > clock_timestamp()
$fn$;

REVOKE ALL ON FUNCTION auth.session_subject() FROM PUBLIC;

DO $subj$
DECLARE
  a text := current_database() || '_admin_api';
BEGIN
  EXECUTE 'GRANT EXECUTE ON FUNCTION auth.session_subject() TO CURRENT_USER';
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = a) THEN
    EXECUTE format('GRANT EXECUTE ON FUNCTION auth.session_subject() TO %I', a);
  END IF;
END
$subj$;
