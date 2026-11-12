-- SA L1. The two principal tables were readable by their owner with RLS off.
-- ENABLE and FORCE them. The migration user (the database owner) keeps one
-- permissive policy so seeds still write. The NOLOGIN definer gets the policy
-- its functions need. admin_api and login receive no policy and no grant.
ALTER TABLE auth.user_issuer ENABLE ROW LEVEL SECURITY;
ALTER TABLE auth.user_issuer FORCE ROW LEVEL SECURITY;
ALTER TABLE auth.login_write_principal ENABLE ROW LEVEL SECURITY;
ALTER TABLE auth.login_write_principal FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS user_issuer_owner ON auth.user_issuer;
CREATE POLICY user_issuer_owner ON auth.user_issuer
  FOR ALL TO CURRENT_USER USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS login_write_principal_owner ON auth.login_write_principal;
CREATE POLICY login_write_principal_owner ON auth.login_write_principal
  FOR ALL TO CURRENT_USER USING (true) WITH CHECK (true);

DO $l1$
DECLARE
  r text := current_database() || '_auth_definer';
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_roles
     WHERE rolname = r AND NOT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls
  ) THEN
    RETURN;
  END IF;
  EXECUTE 'DROP POLICY IF EXISTS user_issuer_definer ON auth.user_issuer';
  EXECUTE format('CREATE POLICY user_issuer_definer ON auth.user_issuer FOR SELECT TO %I USING (true)', r);
  EXECUTE 'DROP POLICY IF EXISTS login_write_principal_definer ON auth.login_write_principal';
  EXECUTE format(
    'CREATE POLICY login_write_principal_definer ON auth.login_write_principal FOR ALL TO %I USING (true) WITH CHECK (true)',
    r);
END
$l1$;
