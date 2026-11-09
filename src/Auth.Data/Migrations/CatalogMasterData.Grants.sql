-- SELECT policies inline the session mask. No catalog function is granted to admin_api or login.
-- Owner policies are TO the migration role, not PUBLIC. Protected tables are FORCE RLS.
DO $pol$
DECLARE
  t text;
  owner_name text := current_user;
  api text := current_database() || '_admin_api';
  read_expr text := $e$CASE
    WHEN current_setting('app.admin_type', true) = 'super' THEN true
    WHEN current_setting('app.admin_type', true) = 'partner'
      AND nullif(current_setting('app.partner_id', true), '') ~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
      AND (%s IS NULL OR %s = nullif(current_setting('app.partner_id', true), '')::uuid)
    THEN true
    ELSE false
  END$e$;
  partner_ok text := $e$(current_setting('app.admin_type', true) = 'partner'
      AND nullif(current_setting('app.partner_id', true), '') ~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
      AND nullif(current_setting('app.partner_id', true), '')::uuid IS NOT NULL)$e$;
  all_tables text[] := ARRAY[
    'registered_entry','ref_global','ref_private','global_device_model',
    'catalog_release','transaction_type','channel','device_category','manufacturer','device_model',
    'hardware_capability_kind','model_hardware_claim','card_network','card_funding_class','payment_rail',
    'check_instrument_kind','check_drawer_class','check_purpose','processor','transaction_status',
    'reason_code','id_document_type','currency','fee_type',
    'partner_switch','location_switch','history_pending','entry_change','edit_coord','tx_marker'];
  owned text[] := ARRAY['manufacturer','device_model','processor','reason_code','id_document_type','fee_type'];
  global_only text[] := ARRAY[
    'catalog_release','transaction_type','channel','device_category','hardware_capability_kind',
    'card_network','card_funding_class','payment_rail','check_instrument_kind','check_drawer_class',
    'check_purpose','transaction_status','currency','ref_global','global_device_model'];
BEGIN
  FOREACH t IN ARRAY all_tables LOOP
    EXECUTE format('ALTER TABLE catalog.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format(
      'CREATE POLICY %I ON catalog.%I FOR ALL TO %I USING (true) WITH CHECK (true)',
      t || '_owner', t, owner_name);
  END LOOP;
  EXECUTE 'ALTER TABLE catalog.history_pending FORCE ROW LEVEL SECURITY';
  EXECUTE 'ALTER TABLE catalog.edit_coord FORCE ROW LEVEL SECURITY';
  EXECUTE 'ALTER TABLE catalog.tx_marker FORCE ROW LEVEL SECURITY';

  EXECUTE format(
    'CREATE POLICY registered_entry_read ON catalog.registered_entry FOR SELECT TO %I USING (%s)',
    api, format(read_expr, 'owner_partner_id', 'owner_partner_id'));
  EXECUTE format(
    'CREATE POLICY ref_private_read ON catalog.ref_private FOR SELECT TO %I USING (%s)',
    api, format(read_expr, 'owner_partner_id', 'owner_partner_id'));
  EXECUTE format(
    'CREATE POLICY model_hardware_claim_read ON catalog.model_hardware_claim FOR SELECT TO %I USING (%s)',
    api, format(read_expr, 'derived_owner', 'derived_owner'));
  FOREACH t IN ARRAY owned LOOP
    EXECUTE format(
      'CREATE POLICY %I ON catalog.%I FOR SELECT TO %I USING (%s)',
      t || '_read', t, api, format(read_expr, 'owner_partner_id', 'owner_partner_id'));
  END LOOP;
  FOREACH t IN ARRAY global_only LOOP
    EXECUTE format(
      'CREATE POLICY %I ON catalog.%I FOR SELECT TO %I USING (%s)',
      t || '_read', t, api, format(read_expr, 'NULL', 'NULL'));
  END LOOP;

  EXECUTE format(
    'CREATE POLICY partner_switch_read ON catalog.partner_switch FOR SELECT TO %I USING (%s)',
    api,
    format($p$current_setting('app.admin_type', true) = 'super'
      OR (%s
        AND partner_id = nullif(current_setting('app.partner_id', true), '')::uuid
        AND EXISTS (
          SELECT 1 FROM catalog.registered_entry e
          WHERE e.entry_id = partner_switch.entry_id
            AND (%s)
        ))$p$, partner_ok, format(read_expr, 'e.owner_partner_id', 'e.owner_partner_id')));

  EXECUTE format(
    'CREATE POLICY location_switch_read ON catalog.location_switch FOR SELECT TO %I USING (%s)',
    api,
    format($p$current_setting('app.admin_type', true) = 'super'
      OR (%s
        AND EXISTS (
          SELECT 1 FROM auth.location l
          WHERE l.location_id = location_switch.location_id
            AND l.partner_id = nullif(current_setting('app.partner_id', true), '')::uuid
        )
        AND EXISTS (
          SELECT 1 FROM catalog.registered_entry e
          WHERE e.entry_id = location_switch.entry_id
            AND (%s)
        ))$p$, partner_ok, format(read_expr, 'e.owner_partner_id', 'e.owner_partner_id')));

  EXECUTE format(
    'CREATE POLICY entry_change_read ON catalog.entry_change FOR SELECT TO %I USING (%s)',
    api,
    format($p$current_setting('app.admin_type', true) = 'super'
      OR (%s
        AND EXISTS (
          SELECT 1 FROM catalog.registered_entry e
          WHERE e.entry_id = entry_change.entry_id
            AND (e.owner_partner_id IS NULL OR e.owner_partner_id = nullif(current_setting('app.partner_id', true), '')::uuid)
        )
        AND (
          (entry_change.scope_level = 'entry'
            AND entry_change.target_partner_id IS NULL
            AND entry_change.target_location_id IS NULL)
          OR (entry_change.scope_level = 'partner'
            AND entry_change.target_partner_id = nullif(current_setting('app.partner_id', true), '')::uuid
            AND entry_change.target_location_id IS NULL)
          OR (entry_change.scope_level = 'location'
            AND entry_change.target_partner_id = nullif(current_setting('app.partner_id', true), '')::uuid
            AND EXISTS (
              SELECT 1 FROM auth.location l
              WHERE l.location_id = entry_change.target_location_id
                AND l.partner_id = nullif(current_setting('app.partner_id', true), '')::uuid
            ))
        ))$p$, partner_ok));
END
$pol$;

-- Production admin_api is SELECT on the readable tables only. No EXECUTE. No DML.
-- history_pending, edit_coord, and tx_marker get no SELECT. Login gets nothing here.
-- Scratch EXECUTE and any scratch grant are harness-only and are not in this migration.
DO $grant$
DECLARE
  api text := current_database() || '_admin_api';
  t text;
  readable text[] := ARRAY[
    'registered_entry','ref_global','ref_private','global_device_model',
    'catalog_release','transaction_type','channel','device_category','manufacturer','device_model',
    'hardware_capability_kind','model_hardware_claim','card_network','card_funding_class','payment_rail',
    'check_instrument_kind','check_drawer_class','check_purpose','processor','transaction_status',
    'reason_code','id_document_type','currency','fee_type',
    'partner_switch','location_switch','entry_change'];
  protected text[] := ARRAY['history_pending','edit_coord','tx_marker'];
  fn record;
BEGIN
  EXECUTE format('GRANT USAGE ON SCHEMA catalog TO %I', api);
  FOREACH t IN ARRAY readable LOOP
    EXECUTE format('GRANT SELECT ON catalog.%I TO %I', t, api);
    EXECUTE format('REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON catalog.%I FROM %I, PUBLIC', t, api);
  END LOOP;
  FOREACH t IN ARRAY protected LOOP
    EXECUTE format('REVOKE ALL ON catalog.%I FROM %I, PUBLIC', t, api);
  END LOOP;
  FOR fn IN
    SELECT p.oid::regprocedure AS sig
    FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
    WHERE n.nspname = 'catalog'
  LOOP
    EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', fn.sig);
    EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I', fn.sig, api);
  END LOOP;
END
$grant$;
