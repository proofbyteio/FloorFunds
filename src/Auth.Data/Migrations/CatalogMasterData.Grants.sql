-- Catalog read policies. Authorization reads app.resolved_global_admin and the resolved
-- partner and location id lists set by the resolver. A value that is not a uuid is ignored,
-- so a bad setting returns zero rows rather than an error.
DO $pol$
DECLARE
  api text := current_database() || '_admin_api';
  owner_name text := current_user;
  uuid_re text := '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$';
  read_expr text := $e$CASE
    WHEN current_setting('app.resolved_global_admin', true) = 'on' THEN true
    WHEN EXISTS (
      SELECT 1
        FROM unnest(string_to_array(coalesce(current_setting('app.resolved_partner_ids', true), ''), ',')) AS p(id)
       WHERE p.id ~ '%s'
         AND (%s IS NULL OR %s::text = p.id)
    ) THEN true
    WHEN EXISTS (
      SELECT 1
        FROM unnest(string_to_array(coalesce(current_setting('app.resolved_location_ids', true), ''), ',')) AS loc(id)
        JOIN auth.location l ON l.location_id::text = loc.id
       WHERE loc.id ~ '%s'
         AND (%s IS NULL OR l.partner_id::text = %s::text)
    ) THEN true
    ELSE false
  END$e$;
  t text;
  owned text[] := ARRAY[
    'manufacturer','device_model','processor','reason_code','id_document_type','fee_type'];
  global_only text[] := ARRAY[
    'catalog_release','transaction_type','channel','device_category','hardware_capability_kind',
    'card_network','card_funding_class','payment_rail','check_instrument_kind','check_drawer_class',
    'check_purpose','transaction_status','currency','ref_global','global_device_model'];
  all_tables text[] := ARRAY[
    'registered_entry','ref_global','ref_private','global_device_model',
    'catalog_release','transaction_type','channel','device_category','manufacturer','device_model',
    'hardware_capability_kind','model_hardware_claim','card_network','card_funding_class','payment_rail',
    'check_instrument_kind','check_drawer_class','check_purpose','processor','transaction_status',
    'reason_code','id_document_type','currency','fee_type',
    'partner_switch','location_switch','history_pending','entry_change','edit_coord','tx_marker'];
BEGIN
  FOREACH t IN ARRAY all_tables LOOP
    EXECUTE format('ALTER TABLE catalog.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('DROP POLICY IF EXISTS %I ON catalog.%I', t || '_owner', t);
    EXECUTE format(
      'CREATE POLICY %I ON catalog.%I FOR ALL TO %I USING (true) WITH CHECK (true)',
      t || '_owner', t, owner_name);
  END LOOP;
  EXECUTE 'ALTER TABLE catalog.history_pending FORCE ROW LEVEL SECURITY';
  EXECUTE 'ALTER TABLE catalog.edit_coord FORCE ROW LEVEL SECURITY';
  EXECUTE 'ALTER TABLE catalog.tx_marker FORCE ROW LEVEL SECURITY';
  -- The uuid pattern is inserted with %L so a literal percent cannot reach format().
  read_expr := format(read_expr, uuid_re, '%s', '%s', uuid_re, '%s', '%s');

  EXECUTE format('DROP POLICY IF EXISTS registered_entry_read ON catalog.registered_entry');
  EXECUTE format('DROP POLICY IF EXISTS ref_global_read ON catalog.ref_global');
  EXECUTE format('DROP POLICY IF EXISTS ref_private_read ON catalog.ref_private');
  EXECUTE format('DROP POLICY IF EXISTS model_hardware_claim_read ON catalog.model_hardware_claim');
  EXECUTE format('DROP POLICY IF EXISTS claim_read ON catalog.model_hardware_claim');
  EXECUTE format('DROP POLICY IF EXISTS partner_switch_read ON catalog.partner_switch');
  EXECUTE format('DROP POLICY IF EXISTS location_switch_read ON catalog.location_switch');
  EXECUTE format('DROP POLICY IF EXISTS entry_change_read ON catalog.entry_change');
  FOREACH t IN ARRAY owned LOOP
    EXECUTE format('DROP POLICY IF EXISTS %I ON catalog.%I', t || '_read', t);
  END LOOP;

  EXECUTE format(
    'CREATE POLICY registered_entry_read ON catalog.registered_entry FOR SELECT TO %I USING (%s)',
    api, format(read_expr, 'owner_partner_id', 'owner_partner_id', 'owner_partner_id', 'owner_partner_id'));
  FOREACH t IN ARRAY global_only LOOP
    EXECUTE format('DROP POLICY IF EXISTS %I ON catalog.%I', t || '_read', t);
    EXECUTE format(
      'CREATE POLICY %I ON catalog.%I FOR SELECT TO %I USING (%s)',
      t || '_read', t, api, format(read_expr, 'NULL', 'NULL', 'NULL', 'NULL'));
  END LOOP;
  EXECUTE format(
    'CREATE POLICY ref_private_read ON catalog.ref_private FOR SELECT TO %I USING (%s)',
    api, format(read_expr, 'owner_partner_id', 'owner_partner_id', 'owner_partner_id', 'owner_partner_id'));
  FOREACH t IN ARRAY owned LOOP
    EXECUTE format(
      'CREATE POLICY %I ON catalog.%I FOR SELECT TO %I USING (%s)',
      t || '_read', t, api,
      format(read_expr, 'owner_partner_id', 'owner_partner_id', 'owner_partner_id', 'owner_partner_id'));
  END LOOP;
  EXECUTE format(
    'CREATE POLICY model_hardware_claim_read ON catalog.model_hardware_claim FOR SELECT TO %I USING (%s)',
    api, format(read_expr, 'derived_owner', 'derived_owner', 'derived_owner', 'derived_owner'));

  EXECUTE format($f$
    CREATE POLICY partner_switch_read ON catalog.partner_switch FOR SELECT TO %I USING (
      current_setting('app.resolved_global_admin', true) = 'on'
      OR (
        partner_switch.partner_id::text = ANY (string_to_array(coalesce(current_setting('app.resolved_partner_ids', true), ''), ','))
        AND partner_switch.partner_id::text ~ %L
        AND EXISTS (
          SELECT 1 FROM catalog.registered_entry e
           WHERE e.entry_id = partner_switch.entry_id
             AND (%s)
        )
      )
      OR (
        EXISTS (
          SELECT 1
            FROM unnest(string_to_array(coalesce(current_setting('app.resolved_location_ids', true), ''), ',')) AS loc(id)
            JOIN auth.location l ON l.location_id::text = loc.id
           WHERE loc.id ~ %L
             AND l.partner_id = partner_switch.partner_id
        )
        AND EXISTS (
          SELECT 1 FROM catalog.registered_entry e
           WHERE e.entry_id = partner_switch.entry_id
             AND (%s)
        )
      )
    )
  $f$, api, uuid_re,
    format(read_expr, 'e.owner_partner_id', 'e.owner_partner_id', 'e.owner_partner_id', 'e.owner_partner_id'),
    uuid_re,
    format(read_expr, 'e.owner_partner_id', 'e.owner_partner_id', 'e.owner_partner_id', 'e.owner_partner_id'));

  EXECUTE format($f$
    CREATE POLICY location_switch_read ON catalog.location_switch FOR SELECT TO %I USING (
      current_setting('app.resolved_global_admin', true) = 'on'
      OR (
        EXISTS (
          SELECT 1 FROM auth.location l
           WHERE l.location_id = location_switch.location_id
             AND l.partner_id::text = ANY (string_to_array(coalesce(current_setting('app.resolved_partner_ids', true), ''), ','))
             AND l.partner_id::text ~ %L
        )
        AND EXISTS (
          SELECT 1 FROM catalog.registered_entry e
           WHERE e.entry_id = location_switch.entry_id
             AND (%s)
        )
      )
      OR (
        location_switch.location_id::text = ANY (string_to_array(coalesce(current_setting('app.resolved_location_ids', true), ''), ','))
        AND location_switch.location_id::text ~ %L
        AND EXISTS (
          SELECT 1 FROM catalog.registered_entry e
           WHERE e.entry_id = location_switch.entry_id
             AND (%s)
        )
      )
    )
  $f$, api, uuid_re,
    format(read_expr, 'e.owner_partner_id', 'e.owner_partner_id', 'e.owner_partner_id', 'e.owner_partner_id'),
    uuid_re,
    format(read_expr, 'e.owner_partner_id', 'e.owner_partner_id', 'e.owner_partner_id', 'e.owner_partner_id'));

  EXECUTE format($f$
    CREATE POLICY entry_change_read ON catalog.entry_change FOR SELECT TO %I USING (
      current_setting('app.resolved_global_admin', true) = 'on'
      OR EXISTS (
        SELECT 1
          FROM unnest(string_to_array(coalesce(current_setting('app.resolved_partner_ids', true), ''), ',')) AS p(id)
         WHERE p.id ~ %L
           AND EXISTS (
             SELECT 1 FROM catalog.registered_entry e
              WHERE e.entry_id = entry_change.entry_id
                AND (e.owner_partner_id IS NULL OR e.owner_partner_id::text = p.id)
           )
           AND (
             (entry_change.scope_level = 'entry' AND entry_change.target_partner_id IS NULL AND entry_change.target_location_id IS NULL)
             OR (entry_change.scope_level = 'partner' AND entry_change.target_partner_id::text = p.id AND entry_change.target_location_id IS NULL)
             OR (entry_change.scope_level = 'location' AND entry_change.target_partner_id::text = p.id AND EXISTS (
                  SELECT 1 FROM auth.location l
                   WHERE l.location_id = entry_change.target_location_id AND l.partner_id::text = p.id
                ))
           )
      )
      OR EXISTS (
        SELECT 1
          FROM unnest(string_to_array(coalesce(current_setting('app.resolved_location_ids', true), ''), ',')) AS loc(id)
          JOIN auth.location mine ON mine.location_id::text = loc.id
         WHERE loc.id ~ %L
           AND EXISTS (
             SELECT 1 FROM catalog.registered_entry e
              WHERE e.entry_id = entry_change.entry_id
                AND (e.owner_partner_id IS NULL OR e.owner_partner_id = mine.partner_id)
           )
           AND (
             (entry_change.scope_level = 'entry' AND entry_change.target_partner_id IS NULL AND entry_change.target_location_id IS NULL)
             OR (entry_change.scope_level = 'partner' AND entry_change.target_partner_id = mine.partner_id AND entry_change.target_location_id IS NULL)
             OR (entry_change.scope_level = 'location' AND entry_change.target_location_id = mine.location_id AND entry_change.target_partner_id = mine.partner_id)
           )
      )
    )
  $f$, api, uuid_re, uuid_re);
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
