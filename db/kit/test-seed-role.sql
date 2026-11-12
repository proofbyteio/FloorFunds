-- TEST-ONLY. Superuser, connected to a scratch or kit database. Never run by the
-- Migrator or a production path. Password arrives as the psql variable
-- test_seed_password and is not written by this file.
-- A2.4 adds seven auth tables. No UPDATE, DELETE, or TRUNCATE. No history_pending grant.
SELECT set_config('floorfunds.test_seed_password', :'test_seed_password', false) AS test_seed_password_set \gset
DO $seed$
DECLARE
  s text := current_database() || '_test_seed';
  pw text := current_setting('floorfunds.test_seed_password');
  t text;
  families text[] := ARRAY[
    'catalog_release', 'transaction_type', 'channel', 'device_category', 'manufacturer', 'device_model',
    'hardware_capability_kind', 'model_hardware_claim', 'card_network', 'card_funding_class', 'payment_rail',
    'check_instrument_kind', 'check_drawer_class', 'check_purpose', 'processor', 'transaction_status',
    'reason_code', 'id_document_type', 'currency', 'fee_type'
  ];
  seeded text[] := ARRAY['registered_entry', 'fee_type', 'app_user', 'admin_assignment'];
  auth_tables text[] := ARRAY[
    'app_user', 'admin_assignment', 'user_issuer', 'location', 'location_login_config',
    'location_login_config_change', 'external_login', 'user_location', 'partner_reason_bounds'
  ];
BEGIN
  IF pw IS NULL OR length(pw) < 16 THEN
    RAISE EXCEPTION 'test seed password is missing';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = s) THEN
    EXECUTE format(
      'CREATE ROLE %I LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION PASSWORD %L',
      s, pw);
  ELSE
    EXECUTE format(
      'ALTER ROLE %I LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION PASSWORD %L',
      s, pw);
  END IF;
  IF EXISTS (SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid IN (m.roleid, m.member) WHERE r.rolname = s) THEN
    RAISE EXCEPTION 'test seed role % must have no memberships', s;
  END IF;
  EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), s);
  EXECUTE format('GRANT USAGE ON SCHEMA auth, catalog TO %I', s);
  FOREACH t IN ARRAY families || auth_tables LOOP
    IF NOT (t = ANY (seeded)) THEN
      seeded := seeded || t;
    END IF;
  END LOOP;
  FOREACH t IN ARRAY seeded LOOP
    IF t = ANY (auth_tables) THEN
      EXECUTE format('REVOKE ALL ON TABLE auth.%I FROM %I', t, s);
      EXECUTE format('GRANT SELECT, INSERT ON TABLE auth.%I TO %I', t, s);
      EXECUTE format('DROP POLICY IF EXISTS %I ON auth.%I', t || '_test_seed_select', t);
      EXECUTE format('DROP POLICY IF EXISTS %I ON auth.%I', t || '_test_seed_insert', t);
      EXECUTE format('CREATE POLICY %I ON auth.%I FOR SELECT TO %I USING (true)', t || '_test_seed_select', t, s);
      EXECUTE format('CREATE POLICY %I ON auth.%I FOR INSERT TO %I WITH CHECK (true)', t || '_test_seed_insert', t, s);
    ELSE
      EXECUTE format('REVOKE ALL ON TABLE catalog.%I FROM %I', t, s);
      EXECUTE format('GRANT SELECT, INSERT ON TABLE catalog.%I TO %I', t, s);
      EXECUTE format('DROP POLICY IF EXISTS %I ON catalog.%I', t || '_test_seed_select', t);
      EXECUTE format('DROP POLICY IF EXISTS %I ON catalog.%I', t || '_test_seed_insert', t);
      EXECUTE format('CREATE POLICY %I ON catalog.%I FOR SELECT TO %I USING (true)', t || '_test_seed_select', t, s);
      EXECUTE format('CREATE POLICY %I ON catalog.%I FOR INSERT TO %I WITH CHECK (true)', t || '_test_seed_insert', t, s);
    END IF;
  END LOOP;
  EXECUTE format('REVOKE ALL ON TABLE catalog.history_pending FROM %I', s);
  EXECUTE 'DROP POLICY IF EXISTS history_pending_test_seed_select ON catalog.history_pending';
  EXECUTE format('GRANT EXECUTE ON FUNCTION catalog.history_partner(uuid, uuid[], boolean) TO %I', s);
END
$seed$;
