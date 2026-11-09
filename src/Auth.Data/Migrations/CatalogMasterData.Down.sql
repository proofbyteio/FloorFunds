-- Not CASCADE. Raises before any drop. Does not alter, drop, or cascade into auth.
DO $down$
DECLARE
  v_name text;
BEGIN
  IF EXISTS (SELECT 1 FROM catalog.registered_entry)
     OR EXISTS (SELECT 1 FROM catalog.entry_change)
     OR EXISTS (SELECT 1 FROM catalog.history_pending)
     OR EXISTS (SELECT 1 FROM catalog.partner_switch)
     OR EXISTS (SELECT 1 FROM catalog.location_switch)
     OR EXISTS (SELECT 1 FROM catalog.catalog_release)
     OR EXISTS (SELECT 1 FROM catalog.transaction_type)
     OR EXISTS (SELECT 1 FROM catalog.channel)
     OR EXISTS (SELECT 1 FROM catalog.device_category)
     OR EXISTS (SELECT 1 FROM catalog.manufacturer)
     OR EXISTS (SELECT 1 FROM catalog.device_model)
     OR EXISTS (SELECT 1 FROM catalog.hardware_capability_kind)
     OR EXISTS (SELECT 1 FROM catalog.model_hardware_claim)
     OR EXISTS (SELECT 1 FROM catalog.card_network)
     OR EXISTS (SELECT 1 FROM catalog.card_funding_class)
     OR EXISTS (SELECT 1 FROM catalog.payment_rail)
     OR EXISTS (SELECT 1 FROM catalog.check_instrument_kind)
     OR EXISTS (SELECT 1 FROM catalog.check_drawer_class)
     OR EXISTS (SELECT 1 FROM catalog.check_purpose)
     OR EXISTS (SELECT 1 FROM catalog.processor)
     OR EXISTS (SELECT 1 FROM catalog.transaction_status)
     OR EXISTS (SELECT 1 FROM catalog.reason_code)
     OR EXISTS (SELECT 1 FROM catalog.id_document_type)
     OR EXISTS (SELECT 1 FROM catalog.currency)
     OR EXISTS (SELECT 1 FROM catalog.fee_type)
  THEN
    RAISE EXCEPTION 'catalog down refused: populated';
  END IF;

  IF EXISTS (
    SELECT 1 FROM auth.audit_outbox o
    WHERE (o.envelope->'details' ? 'catalog_entry_id')
       OR (
         (o.envelope->'details' ? 'family')
         AND o.envelope->>'event_type' ~ '^catalog\.[a-z_]+\.[a-z_]+$'
       )
  ) THEN
    RAISE EXCEPTION 'catalog down refused: catalog audit';
  END IF;

  SELECT format('%I.%I', n.nspname, c.relname) INTO v_name
  FROM pg_constraint con
  JOIN pg_class c ON c.oid = con.conrelid
  JOIN pg_namespace n ON n.oid = c.relnamespace
  JOIN pg_class ref ON ref.oid = con.confrelid
  JOIN pg_namespace rn ON rn.oid = ref.relnamespace
  WHERE con.contype = 'f'
    AND rn.nspname = 'catalog'
    AND n.nspname <> 'catalog'
  LIMIT 1;
  IF v_name IS NOT NULL THEN
    RAISE EXCEPTION 'catalog down refused: external dependent %', v_name;
  END IF;

  SELECT format('%I.%I', n.nspname, c.relname) INTO v_name
  FROM pg_depend d
  JOIN pg_class c ON c.oid = d.objid AND d.classid = 'pg_class'::regclass
  JOIN pg_namespace n ON n.oid = c.relnamespace
  JOIN pg_class ref ON ref.oid = d.refobjid AND d.refclassid = 'pg_class'::regclass
  JOIN pg_namespace rn ON rn.oid = ref.relnamespace
  WHERE rn.nspname = 'catalog'
    AND n.nspname NOT IN ('catalog', 'pg_catalog', 'information_schema', 'pg_toast')
  LIMIT 1;
  IF v_name IS NOT NULL THEN
    RAISE EXCEPTION 'catalog down refused: external dependent %', v_name;
  END IF;

  SELECT format('%I.%I', n.nspname, p.proname) INTO v_name
  FROM pg_depend d
  JOIN pg_proc p ON p.oid = d.objid AND d.classid = 'pg_proc'::regclass
  JOIN pg_namespace n ON n.oid = p.pronamespace
  JOIN pg_class ref ON ref.oid = d.refobjid AND d.refclassid = 'pg_class'::regclass
  JOIN pg_namespace rn ON rn.oid = ref.relnamespace
  WHERE rn.nspname = 'catalog'
    AND n.nspname NOT IN ('catalog', 'pg_catalog', 'information_schema', 'pg_toast')
  LIMIT 1;
  IF v_name IS NOT NULL THEN
    RAISE EXCEPTION 'catalog down refused: external dependent %', v_name;
  END IF;
END
$down$;

DO $revoke$
DECLARE
  api text := current_database() || '_admin_api';
  t text;
  names text[] := ARRAY[
    'registered_entry','ref_global','ref_private','global_device_model',
    'catalog_release','transaction_type','channel','device_category','manufacturer','device_model',
    'hardware_capability_kind','model_hardware_claim','card_network','card_funding_class','payment_rail',
    'check_instrument_kind','check_drawer_class','check_purpose','processor','transaction_status',
    'reason_code','id_document_type','currency','fee_type',
    'partner_switch','location_switch','history_pending','entry_change','edit_coord','tx_marker'];
BEGIN
  FOREACH t IN ARRAY names LOOP
    EXECUTE format('REVOKE ALL ON catalog.%I FROM %I, PUBLIC', t, api);
  END LOOP;
  EXECUTE format('REVOKE ALL ON SCHEMA catalog FROM %I, PUBLIC', api);
END
$revoke$;

DROP TABLE
  catalog.model_hardware_claim,
  catalog.device_model,
  catalog.processor,
  catalog.manufacturer,
  catalog.reason_code,
  catalog.id_document_type,
  catalog.fee_type,
  catalog.hardware_capability_kind,
  catalog.transaction_type,
  catalog.channel,
  catalog.device_category,
  catalog.card_network,
  catalog.card_funding_class,
  catalog.payment_rail,
  catalog.check_instrument_kind,
  catalog.check_drawer_class,
  catalog.check_purpose,
  catalog.transaction_status,
  catalog.currency,
  catalog.catalog_release,
  catalog.partner_switch,
  catalog.location_switch,
  catalog.history_pending,
  catalog.entry_change,
  catalog.edit_coord,
  catalog.tx_marker,
  catalog.global_device_model,
  catalog.ref_private,
  catalog.ref_global,
  catalog.registered_entry
RESTRICT;

DO $dropfn$
DECLARE r record;
BEGIN
  FOR r IN
    SELECT p.oid::regprocedure AS sig
    FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
    WHERE n.nspname = 'catalog'
  LOOP
    EXECUTE format('DROP FUNCTION %s', r.sig);
  END LOOP;
END
$dropfn$;

DROP SCHEMA catalog RESTRICT;
