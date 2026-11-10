-- REQ-064. The lookup functions run as a NOLOGIN role created by the kit before this
-- migration. This migration cannot CREATE ROLE or ALTER OWNER: the migration user is
-- NOCREATEROLE and is not a member of the definer. The superuser owner step does that.
-- Policies here let that role see the rows the functions read. admin_api is not a member.

DO $def$
DECLARE
  r text := current_database() || '_auth_definer';
BEGIN
  -- The kit creates this role before migrate. A scratch database that cannot
  -- CREATE ROLE skips the grants; its functions stay owned by the migration user.
  IF NOT EXISTS (
    SELECT 1 FROM pg_roles
     WHERE rolname = r AND NOT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls
  ) THEN
    RETURN;
  END IF;
  EXECUTE format('GRANT USAGE ON SCHEMA auth, catalog TO %I', r);
  EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE auth.resolver_read_seq TO %I', r);
  EXECUTE format('GRANT SELECT ON auth.app_user, auth.user_issuer, auth.external_login, auth.admin_assignment, auth.location TO %I', r);
  EXECUTE format('GRANT SELECT, INSERT, UPDATE ON auth.partner_reason_bounds TO %I', r);
  EXECUTE format('GRANT SELECT, INSERT ON auth.login_write_principal TO %I', r);
  EXECUTE format('GRANT SELECT ON catalog.registered_entry TO %I', r);
  EXECUTE 'DROP POLICY IF EXISTS admin_assignment_definer ON auth.admin_assignment';
  EXECUTE format('CREATE POLICY admin_assignment_definer ON auth.admin_assignment FOR SELECT TO %I USING (true)', r);
  EXECUTE 'DROP POLICY IF EXISTS location_definer ON auth.location';
  EXECUTE format('CREATE POLICY location_definer ON auth.location FOR SELECT TO %I USING (true)', r);
  EXECUTE 'DROP POLICY IF EXISTS prb_definer ON auth.partner_reason_bounds';
  EXECUTE format('CREATE POLICY prb_definer ON auth.partner_reason_bounds FOR ALL TO %I USING (true) WITH CHECK (true)', r);
  EXECUTE 'DROP POLICY IF EXISTS registered_entry_definer ON catalog.registered_entry';
  EXECUTE format('CREATE POLICY registered_entry_definer ON catalog.registered_entry FOR SELECT TO %I USING (true)', r);
END
$def$;
