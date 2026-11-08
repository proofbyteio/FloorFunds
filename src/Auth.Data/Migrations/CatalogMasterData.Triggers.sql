-- Statement trigger. Reads the marker. Does not insert it and does not seal it.
-- Only a sealed row for this xid8 rejects the client statement. Unsealed allows the
-- create's second statement and the claim edit's internal updates.
CREATE FUNCTION catalog.tg_tx_marker_stmt() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
BEGIN
  IF EXISTS (
    SELECT 1 FROM catalog.tx_marker m
    WHERE m.txn_id = pg_current_xact_id() AND m.sealed
  ) THEN
    PERFORM catalog._err('P0001', 'catalog_transaction_action');
  END IF;
  RETURN NULL;
END
$fn$;

CREATE FUNCTION catalog.tg_no_delete() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
BEGIN
  PERFORM catalog._err('P0001', 'catalog_no_hard_delete');
  RETURN NULL;
END
$fn$;

CREATE FUNCTION catalog.tg_registry_before() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF NEW.row_version IS DISTINCT FROM 1 THEN
      PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
    END IF;
    RETURN NEW;
  END IF;
  IF NEW IS NOT DISTINCT FROM OLD THEN
    PERFORM catalog._err('P0001', 'catalog_noop');
  END IF;
  IF NEW.entry_id IS DISTINCT FROM OLD.entry_id
     OR NEW.family IS DISTINCT FROM OLD.family
     OR NEW.entry_class IS DISTINCT FROM OLD.entry_class
     OR NEW.owner_partner_id IS DISTINCT FROM OLD.owner_partner_id
     OR NEW.code IS DISTINCT FROM OLD.code
     OR NEW.created_by IS DISTINCT FROM OLD.created_by
     OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END IF;
  IF NEW.is_active IS DISTINCT FROM OLD.is_active THEN
    PERFORM catalog._err('P0001', 'catalog_flag_definer_absent');
  END IF;
  IF NEW.row_version IS DISTINCT FROM OLD.row_version
     OR NEW.updated_by IS DISTINCT FROM OLD.updated_by
     OR NEW.updated_at IS DISTINCT FROM OLD.updated_at THEN
    IF NOT EXISTS (
      SELECT 1 FROM catalog.edit_coord c
      WHERE c.entry_id = OLD.entry_id
        AND c.txn_id = txid_current()
        AND c.expected_version = OLD.row_version
    ) THEN
      PERFORM catalog._err('P0001', 'catalog_claim_edit_uncoordinated');
    END IF;
    IF NEW.row_version IS DISTINCT FROM OLD.row_version + 1 THEN
      PERFORM catalog._err('P0001', 'catalog_version_conflict');
    END IF;
  END IF;
  RETURN NEW;
END
$fn$;

-- Anchors, then pending. Actor is copied here and is not reread at commit.
-- This function does not insert or seal catalog.tx_marker.
CREATE FUNCTION catalog.tg_registry_pending() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
DECLARE v_actor record;
BEGIN
  IF NEW.owner_partner_id IS NULL AND NEW.entry_class IN ('descriptive', 'provenance') THEN
    INSERT INTO catalog.ref_global (entry_id, family) VALUES (NEW.entry_id, NEW.family);
  END IF;
  IF NEW.owner_partner_id IS NOT NULL
     AND NEW.family IN ('manufacturer','device_model','processor','reason_code','id_document_type','fee_type') THEN
    INSERT INTO catalog.ref_private (entry_id, family, owner_partner_id)
    VALUES (NEW.entry_id, NEW.family, NEW.owner_partner_id);
  END IF;
  IF NEW.owner_partner_id IS NULL AND NEW.family = 'device_model' THEN
    INSERT INTO catalog.global_device_model (entry_id, family) VALUES (NEW.entry_id, NEW.family);
  END IF;
  SELECT * INTO v_actor FROM catalog._actor();
  INSERT INTO catalog.history_pending (
    entry_id, family, registry_after, subtype_after,
    actor_user_id, actor_admin_type, actor_partner_id, reason, correlation_id, at)
  VALUES (
    NEW.entry_id, NEW.family, to_jsonb(NEW), NULL,
    v_actor.actor_user_id, v_actor.admin_type, v_actor.partner_id, v_actor.reason, v_actor.correlation_id,
    clock_timestamp());
  RETURN NULL;
END
$fn$;

-- Seals the pending subtype image once. Does not seal the transaction marker.
CREATE FUNCTION catalog.tg_subtype_seal() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
DECLARE v_n integer;
BEGIN
  UPDATE catalog.history_pending
  SET subtype_after = catalog.subtype_json(NEW.entry_id)
  WHERE entry_id = NEW.entry_id AND subtype_after IS NULL;
  GET DIAGNOSTICS v_n = ROW_COUNT;
  IF v_n <> 1 THEN
    PERFORM catalog._err('P0001', 'catalog_creation_snapshot');
  END IF;
  RETURN NULL;
END
$fn$;

CREATE FUNCTION catalog.tg_registry_commit() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
DECLARE
  v_pending catalog.history_pending%ROWTYPE;
  v_count integer;
  v_live jsonb;
  v_sub_owner uuid;
  v_global integer;
  v_private integer;
  v_gdm integer;
  v_expect_gdm integer;
BEGIN
  SELECT * INTO v_pending FROM catalog.history_pending WHERE entry_id = NEW.entry_id;
  IF NOT FOUND OR v_pending.subtype_after IS NULL THEN
    PERFORM catalog._err('P0001', 'catalog_creation_snapshot');
  END IF;
  v_count := catalog.subtype_count(NEW.entry_id);
  IF v_count IS DISTINCT FROM 1 THEN
    PERFORM catalog._err('P0001', 'catalog_subtype_count');
  END IF;
  v_live := catalog.subtype_json(NEW.entry_id);
  IF v_live IS DISTINCT FROM v_pending.subtype_after THEN
    PERFORM catalog._err('P0001', 'catalog_creation_snapshot');
  END IF;
  IF NEW.family = 'model_hardware_claim' THEN
    SELECT derived_owner INTO v_sub_owner FROM catalog.model_hardware_claim WHERE entry_id = NEW.entry_id;
    IF v_sub_owner IS DISTINCT FROM NEW.owner_partner_id THEN
      PERFORM catalog._err('P0001', 'catalog_owner_mismatch');
    END IF;
  ELSIF NEW.family IN ('manufacturer','device_model','processor','reason_code','id_document_type','fee_type') THEN
    EXECUTE format('SELECT owner_partner_id FROM catalog.%I WHERE entry_id = $1', NEW.family)
      INTO v_sub_owner USING NEW.entry_id;
    IF v_sub_owner IS DISTINCT FROM NEW.owner_partner_id THEN
      PERFORM catalog._err('P0001', 'catalog_owner_mismatch');
    END IF;
  END IF;
  SELECT count(*) INTO v_global FROM catalog.ref_global WHERE entry_id = NEW.entry_id AND family = NEW.family;
  SELECT count(*) INTO v_private FROM catalog.ref_private WHERE entry_id = NEW.entry_id AND family = NEW.family;
  SELECT count(*) INTO v_gdm FROM catalog.global_device_model WHERE entry_id = NEW.entry_id;
  IF NEW.entry_class = 'model_evidence' THEN
    IF v_global <> 0 OR v_private <> 0 OR v_gdm <> 0 THEN
      PERFORM catalog._err('P0001', 'catalog_creation_snapshot');
    END IF;
  ELSIF NEW.owner_partner_id IS NULL THEN
    IF v_global <> 1 OR v_private <> 0 THEN
      PERFORM catalog._err('P0001', 'catalog_creation_snapshot');
    END IF;
    v_expect_gdm := CASE WHEN NEW.family = 'device_model' THEN 1 ELSE 0 END;
    IF v_gdm <> v_expect_gdm THEN
      PERFORM catalog._err('P0001', 'catalog_creation_snapshot');
    END IF;
  ELSE
    IF v_private <> 1 OR v_global <> 0 OR v_gdm <> 0 THEN
      PERFORM catalog._err('P0001', 'catalog_creation_snapshot');
    END IF;
  END IF;
  INSERT INTO catalog.entry_change (
    change_id, entry_id, scope_level, target_partner_id, target_location_id,
    row_version, change_kind, actor_user_id, actor_admin_type, actor_partner_id,
    reason, source, at, before, after)
  VALUES (
    gen_random_uuid(), NEW.entry_id, 'entry', NULL, NULL,
    NEW.row_version, 'created',
    v_pending.actor_user_id, v_pending.actor_admin_type, v_pending.actor_partner_id,
    v_pending.reason, v_pending.correlation_id, v_pending.at,
    NULL,
    v_pending.registry_after || jsonb_build_object('subtype', v_pending.subtype_after));
  DELETE FROM catalog.history_pending WHERE entry_id = NEW.entry_id;
  RETURN NULL;
END
$fn$;

CREATE FUNCTION catalog.tg_registry_update() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
DECLARE
  v_actor record;
  v_before jsonb;
  v_after jsonb;
BEGIN
  SELECT c.before INTO v_before
  FROM catalog.edit_coord c
  WHERE c.entry_id = NEW.entry_id AND c.txn_id = txid_current();
  IF NOT FOUND THEN
    RETURN NULL;
  END IF;
  SELECT * INTO v_actor FROM catalog._actor();
  v_after := jsonb_build_object('registry', to_jsonb(NEW), 'subtype', catalog.subtype_json(NEW.entry_id));
  INSERT INTO catalog.entry_change (
    change_id, entry_id, scope_level, target_partner_id, target_location_id,
    row_version, change_kind, actor_user_id, actor_admin_type, actor_partner_id,
    reason, source, at, before, after)
  VALUES (
    gen_random_uuid(), NEW.entry_id, 'entry', NULL, NULL,
    NEW.row_version, 'updated',
    v_actor.actor_user_id, v_actor.admin_type, v_actor.partner_id,
    v_actor.reason, v_actor.correlation_id, clock_timestamp(),
    v_before, v_after);
  DELETE FROM catalog.edit_coord WHERE entry_id = NEW.entry_id AND txn_id = txid_current();
  RETURN NULL;
END
$fn$;

CREATE FUNCTION catalog.tg_claim_subtype() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
BEGIN
  IF NEW.entry_id IS DISTINCT FROM OLD.entry_id
     OR NEW.family IS DISTINCT FROM OLD.family
     OR NEW.code IS DISTINCT FROM OLD.code
     OR NEW.derived_owner IS DISTINCT FROM OLD.derived_owner
     OR NEW.model_global_id IS DISTINCT FROM OLD.model_global_id
     OR NEW.model_global_family IS DISTINCT FROM OLD.model_global_family
     OR NEW.model_private_id IS DISTINCT FROM OLD.model_private_id
     OR NEW.model_private_owner IS DISTINCT FROM OLD.model_private_owner
     OR NEW.model_private_family IS DISTINCT FROM OLD.model_private_family
     OR NEW.capability_entry_id IS DISTINCT FROM OLD.capability_entry_id
     OR NEW.option_key IS DISTINCT FROM OLD.option_key
     OR NEW.source_version IS DISTINCT FROM OLD.source_version THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END IF;
  IF NOT EXISTS (
    SELECT 1
    FROM catalog.edit_coord c
    JOIN catalog.registered_entry e ON e.entry_id = c.entry_id
    WHERE c.entry_id = OLD.entry_id
      AND c.txn_id = txid_current()
      AND c.expected_version = e.row_version
  ) THEN
    PERFORM catalog._err('P0001', 'catalog_claim_edit_uncoordinated');
  END IF;
  RETURN NEW;
END
$fn$;

CREATE FUNCTION catalog.tg_subtype_no_update() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
BEGIN
  PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  RETURN NULL;
END
$fn$;

-- Scope writes are stored as tables and are not an approved success path.
CREATE FUNCTION catalog.tg_scope_refuse() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
BEGIN
  PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  RETURN NULL;
END
$fn$;

CREATE FUNCTION catalog.tg_processor_parent() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
DECLARE v_parent uuid;
BEGIN
  v_parent := COALESCE(NEW.parent_global_id, NEW.parent_private_id);
  IF v_parent IS NOT NULL AND v_parent = NEW.entry_id THEN
    PERFORM catalog._err('P0001', 'catalog_processor_cycle');
  END IF;
  IF v_parent IS NOT NULL THEN
    PERFORM pg_advisory_xact_lock(hashtext('catalog.processor.graph')::bigint);
  END IF;
  RETURN NEW;
END
$fn$;

CREATE FUNCTION catalog.tg_processor_cycle_commit() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
DECLARE
  r record;
  v_next uuid;
  v_path uuid[];
  v_guard integer;
BEGIN
  IF NOT EXISTS (SELECT 1 FROM catalog.processor WHERE parent_entry_id IS NOT NULL) THEN
    RETURN NULL;
  END IF;
  PERFORM pg_advisory_xact_lock(hashtext('catalog.processor.graph')::bigint);
  FOR r IN SELECT entry_id FROM catalog.processor WHERE parent_entry_id IS NOT NULL LOOP
    v_path := ARRAY[r.entry_id];
    SELECT p.parent_entry_id INTO v_next FROM catalog.processor p WHERE p.entry_id = r.entry_id;
    v_guard := 0;
    WHILE v_next IS NOT NULL LOOP
      IF v_next = ANY (v_path) THEN
        PERFORM catalog._err('P0001', 'catalog_processor_cycle');
      END IF;
      v_path := v_path || v_next;
      v_guard := v_guard + 1;
      IF v_guard > 10000 THEN
        PERFORM catalog._err('P0001', 'catalog_processor_cycle');
      END IF;
      SELECT p.parent_entry_id INTO v_next FROM catalog.processor p WHERE p.entry_id = v_next;
    END LOOP;
  END LOOP;
  RETURN NULL;
END
$fn$;

-- Deferred so a same-statement sibling is visible.
-- Quoted so the name sorts before RI_ConstraintTrigger_* (uppercase R sorts before lowercase).
CREATE FUNCTION catalog.tg_a_ref_guard() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
BEGIN
  IF TG_TABLE_NAME = 'processor' THEN
    IF NEW.parent_global_id IS NOT NULL AND NOT EXISTS (
      SELECT 1 FROM catalog.ref_global g
      WHERE g.entry_id = NEW.parent_global_id AND g.family = 'processor'
    ) THEN
      PERFORM catalog._err('23503', 'catalog_reference_not_found');
    END IF;
    IF NEW.parent_private_id IS NOT NULL AND NOT EXISTS (
      SELECT 1 FROM catalog.ref_private p
      WHERE p.entry_id = NEW.parent_private_id AND p.family = 'processor'
        AND p.owner_partner_id IS NOT DISTINCT FROM NEW.parent_private_owner
    ) THEN
      PERFORM catalog._err('23503', 'catalog_reference_not_found');
    END IF;
  ELSIF TG_TABLE_NAME = 'device_model' THEN
    IF NEW.manufacturer_global_id IS NOT NULL AND NOT EXISTS (
      SELECT 1 FROM catalog.ref_global g
      WHERE g.entry_id = NEW.manufacturer_global_id AND g.family = 'manufacturer'
    ) THEN
      PERFORM catalog._err('23503', 'catalog_reference_not_found');
    END IF;
    IF NEW.manufacturer_private_id IS NOT NULL AND NOT EXISTS (
      SELECT 1 FROM catalog.ref_private p
      WHERE p.entry_id = NEW.manufacturer_private_id AND p.family = 'manufacturer'
        AND p.owner_partner_id IS NOT DISTINCT FROM NEW.manufacturer_private_owner
    ) THEN
      PERFORM catalog._err('23503', 'catalog_reference_not_found');
    END IF;
    IF NOT EXISTS (
      SELECT 1 FROM catalog.ref_global g
      WHERE g.entry_id = NEW.category_entry_id AND g.family = 'device_category'
    ) THEN
      PERFORM catalog._err('23503', 'catalog_reference_not_found');
    END IF;
  ELSIF TG_TABLE_NAME = 'model_hardware_claim' THEN
    IF NEW.model_global_id IS NOT NULL AND NOT EXISTS (
      SELECT 1 FROM catalog.global_device_model g
      WHERE g.entry_id = NEW.model_global_id AND g.family = 'device_model'
    ) THEN
      PERFORM catalog._err('23503', 'catalog_reference_not_found');
    END IF;
    IF NEW.model_private_id IS NOT NULL AND NOT EXISTS (
      SELECT 1 FROM catalog.ref_private p
      WHERE p.entry_id = NEW.model_private_id AND p.family = 'device_model'
        AND p.owner_partner_id IS NOT DISTINCT FROM NEW.model_private_owner
    ) THEN
      PERFORM catalog._err('23503', 'catalog_reference_not_found');
    END IF;
    IF NOT EXISTS (
      SELECT 1 FROM catalog.ref_global g
      WHERE g.entry_id = NEW.capability_entry_id AND g.family = 'hardware_capability_kind'
    ) THEN
      PERFORM catalog._err('23503', 'catalog_reference_not_found');
    END IF;
  END IF;
  RETURN NULL;
END
$fn$;

DO $attach$
DECLARE t text;
DECLARE subtypes text[] := ARRAY[
  'catalog_release','transaction_type','channel','device_category','manufacturer','device_model',
  'hardware_capability_kind','model_hardware_claim','card_network','card_funding_class','payment_rail',
  'check_instrument_kind','check_drawer_class','check_purpose','processor','transaction_status',
  'reason_code','id_document_type','currency','fee_type'];
DECLARE marked text[];
BEGIN
  marked := subtypes || ARRAY['registered_entry','partner_switch','location_switch'];
  FOREACH t IN ARRAY marked LOOP
    EXECUTE format(
      'CREATE TRIGGER tg_tx_marker_stmt BEFORE INSERT OR UPDATE OR DELETE ON catalog.%I FOR EACH STATEMENT EXECUTE FUNCTION catalog.tg_tx_marker_stmt()',
      t);
  END LOOP;
  FOREACH t IN ARRAY subtypes LOOP
    EXECUTE format(
      'CREATE TRIGGER tg_no_delete BEFORE DELETE ON catalog.%I FOR EACH ROW EXECUTE FUNCTION catalog.tg_no_delete()',
      t);
    EXECUTE format(
      'CREATE TRIGGER tg_subtype_seal AFTER INSERT ON catalog.%I FOR EACH ROW EXECUTE FUNCTION catalog.tg_subtype_seal()',
      t);
  END LOOP;
  FOREACH t IN ARRAY subtypes LOOP
    IF t <> 'model_hardware_claim' THEN
      EXECUTE format(
        'CREATE TRIGGER tg_subtype_no_update BEFORE UPDATE ON catalog.%I FOR EACH ROW EXECUTE FUNCTION catalog.tg_subtype_no_update()',
        t);
    END IF;
  END LOOP;
END
$attach$;

CREATE TRIGGER tg_no_delete BEFORE DELETE ON catalog.registered_entry
  FOR EACH ROW EXECUTE FUNCTION catalog.tg_no_delete();
CREATE TRIGGER tg_registry_before BEFORE INSERT OR UPDATE ON catalog.registered_entry
  FOR EACH ROW EXECUTE FUNCTION catalog.tg_registry_before();
CREATE TRIGGER tg_registry_pending AFTER INSERT ON catalog.registered_entry
  FOR EACH ROW EXECUTE FUNCTION catalog.tg_registry_pending();
CREATE CONSTRAINT TRIGGER tg_registry_commit AFTER INSERT ON catalog.registered_entry
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION catalog.tg_registry_commit();
CREATE TRIGGER tg_registry_update AFTER UPDATE ON catalog.registered_entry
  FOR EACH ROW EXECUTE FUNCTION catalog.tg_registry_update();

CREATE TRIGGER tg_claim_subtype BEFORE UPDATE ON catalog.model_hardware_claim
  FOR EACH ROW EXECUTE FUNCTION catalog.tg_claim_subtype();
CREATE TRIGGER tg_processor_parent BEFORE INSERT OR UPDATE ON catalog.processor
  FOR EACH ROW EXECUTE FUNCTION catalog.tg_processor_parent();
CREATE CONSTRAINT TRIGGER tg_processor_cycle_commit AFTER INSERT OR UPDATE ON catalog.processor
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION catalog.tg_processor_cycle_commit();

CREATE CONSTRAINT TRIGGER "A_ref_guard" AFTER INSERT OR UPDATE ON catalog.processor
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION catalog.tg_a_ref_guard();
CREATE CONSTRAINT TRIGGER "A_ref_guard" AFTER INSERT OR UPDATE ON catalog.device_model
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION catalog.tg_a_ref_guard();
CREATE CONSTRAINT TRIGGER "A_ref_guard" AFTER INSERT OR UPDATE ON catalog.model_hardware_claim
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION catalog.tg_a_ref_guard();

CREATE TRIGGER tg_scope_refuse BEFORE INSERT OR UPDATE OR DELETE ON catalog.partner_switch
  FOR EACH ROW EXECUTE FUNCTION catalog.tg_scope_refuse();
CREATE TRIGGER tg_scope_refuse BEFORE INSERT OR UPDATE OR DELETE ON catalog.location_switch
  FOR EACH ROW EXECUTE FUNCTION catalog.tg_scope_refuse();
