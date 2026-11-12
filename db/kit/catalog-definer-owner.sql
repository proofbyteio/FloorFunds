-- Runs as a superuser, connected to the kit database. Transfers the 22 catalog
-- SECURITY DEFINER functions, the 11 invoker helpers, schema catalog, and the
-- 30 catalog tables to <db>_catalog_definer. The migration user cannot undo it.
DO $cat$
DECLARE
  cat text := current_database() || '_catalog_definer';
  owner_name text;
  api text := current_database() || '_admin_api';
  login_role text := current_database() || '_login';
  issuer text := current_database() || '_issuer';
  fn regprocedure;
  seq regclass;
  t text;
  n_def int;
  n_help int;
  definers text[] := ARRAY[
    '_insert_marker', '_insert_subtype', '_seal_marker', '_sealed_marker', '_visible_ref',
    'apply_claim_edit', 'apply_entry_create', 'subtype_count', 'subtype_json',
    'tg_a_ref_guard', 'tg_claim_subtype', 'tg_no_delete', 'tg_processor_cycle_commit',
    'tg_processor_parent', 'tg_registry_before', 'tg_registry_commit', 'tg_registry_pending',
    'tg_registry_update', 'tg_scope_refuse', 'tg_subtype_no_update', 'tg_subtype_seal',
    'tg_tx_marker_stmt'
  ];
  helpers text[] := ARRAY[
    '_actor', '_err', '_keys_ok', '_req_text', '_opt_text', '_req_bool', '_opt_uuid',
    '_req_uuid', '_req_timestamptz', '_req_smallint', 'history_partner'
  ];
  tables text[] := ARRAY[
    'registered_entry', 'ref_global', 'ref_private', 'global_device_model',
    'catalog_release', 'transaction_type', 'channel', 'device_category', 'manufacturer', 'device_model',
    'hardware_capability_kind', 'model_hardware_claim', 'card_network', 'card_funding_class', 'payment_rail',
    'check_instrument_kind', 'check_drawer_class', 'check_purpose', 'processor', 'transaction_status',
    'reason_code', 'id_document_type', 'currency', 'fee_type', 'partner_switch', 'location_switch',
    'history_pending', 'entry_change', 'edit_coord', 'tx_marker'
  ];
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_roles
     WHERE rolname = cat AND NOT rolcanlogin AND NOT rolinherit AND NOT rolsuper
       AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication
  ) THEN
    RAISE EXCEPTION 'catalog definer role % must exist, NOLOGIN NOINHERIT, and not a superuser', cat;
  END IF;
  IF EXISTS (SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid IN (m.roleid, m.member) WHERE r.rolname = cat) THEN
    RAISE EXCEPTION 'catalog definer role % must have no memberships', cat;
  END IF;
  SELECT count(*) INTO n_def FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
   WHERE n.nspname = 'catalog' AND p.prosecdef AND p.proname = ANY (definers);
  SELECT count(*) INTO n_help FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
   WHERE n.nspname = 'catalog' AND NOT p.prosecdef AND p.proname = ANY (helpers);
  IF n_def <> 22 OR n_help <> 11 THEN
    RAISE EXCEPTION 'catalog transfer expected 22 definers and 11 helpers, found % and %', n_def, n_help;
  END IF;
  SELECT pg_get_userbyid(datdba)::text INTO owner_name
    FROM pg_database WHERE datname = current_database();
  EXECUTE format('ALTER SCHEMA catalog OWNER TO %I', cat);
  EXECUTE format('GRANT USAGE, CREATE ON SCHEMA catalog TO %I', owner_name);
  EXECUTE format('GRANT USAGE ON SCHEMA auth, catalog TO %I', cat);
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.resolve_grant(text, uuid) TO %I', cat);
  FOREACH t IN ARRAY tables LOOP
    EXECUTE format('ALTER TABLE catalog.%I OWNER TO %I', t, cat);
    EXECUTE format('ALTER TABLE catalog.%I FORCE ROW LEVEL SECURITY', t);
    EXECUTE format('DROP POLICY IF EXISTS %I ON catalog.%I', t || '_owner', t);
    EXECUTE format('CREATE POLICY %I ON catalog.%I FOR SELECT TO %I USING (true)', t || '_owner', t, owner_name);
    EXECUTE format('REVOKE ALL ON TABLE catalog.%I FROM %I', t, owner_name);
    EXECUTE format('GRANT SELECT ON TABLE catalog.%I TO %I', t, owner_name);
    EXECUTE format('REVOKE ALL ON TABLE catalog.%I FROM PUBLIC', t);
    EXECUTE format('DROP POLICY IF EXISTS %I ON catalog.%I', t || '_catalog_definer', t);
    EXECUTE format('DROP POLICY IF EXISTS %I ON catalog.%I', t || '_catalog_definer_select', t);
    EXECUTE format('DROP POLICY IF EXISTS %I ON catalog.%I', t || '_catalog_definer_insert', t);
    EXECUTE format('DROP POLICY IF EXISTS %I ON catalog.%I', t || '_catalog_definer_update', t);
    EXECUTE format('DROP POLICY IF EXISTS %I ON catalog.%I', t || '_catalog_definer_delete', t);
    EXECUTE format('CREATE POLICY %I ON catalog.%I FOR SELECT TO %I USING (true)', t || '_catalog_definer_select', t, cat);
    EXECUTE format('CREATE POLICY %I ON catalog.%I FOR INSERT TO %I WITH CHECK (true)', t || '_catalog_definer_insert', t, cat);
    EXECUTE format('CREATE POLICY %I ON catalog.%I FOR UPDATE TO %I USING (true) WITH CHECK (true)', t || '_catalog_definer_update', t, cat);
    EXECUTE format('CREATE POLICY %I ON catalog.%I FOR DELETE TO %I USING (true)', t || '_catalog_definer_delete', t, cat);
    EXECUTE format('ALTER TABLE catalog.%I ENABLE ROW LEVEL SECURITY', t);
  END LOOP;
  FOR seq IN
    SELECT c.oid::regclass FROM pg_class c
      JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname = 'catalog' AND c.relkind = 'S'
  LOOP
    EXECUTE format('ALTER SEQUENCE %s OWNER TO %I', seq, cat);
  END LOOP;
  FOR fn IN
    SELECT p.oid::regprocedure FROM pg_proc p
      JOIN pg_namespace n ON n.oid = p.pronamespace
     WHERE n.nspname = 'catalog' AND p.proname = ANY (definers || helpers)
  LOOP
    EXECUTE format('ALTER FUNCTION %s OWNER TO %I', fn, cat);
    EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', fn);
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = api) THEN
      EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I', fn, api);
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = login_role) THEN
      EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I', fn, login_role);
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = issuer) THEN
      EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I', fn, issuer);
    END IF;
    EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I', fn, owner_name);
  END LOOP;
END
$cat$;
