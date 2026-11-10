-- REQ-064 history label. catalog._actor.partner_id is resolved_partner_ids[1]
-- with no ORDER BY, so it is not the partner of the row being labeled.
-- The stored actor_partner_id is the record's partner when the actor is not a
-- global Super Admin, otherwise the actor's single Partner Admin partner, never
-- "whichever partner happens to be first".

CREATE OR REPLACE FUNCTION catalog.history_partner(p_record uuid, p_actor uuid[], p_global boolean)
  RETURNS uuid
  LANGUAGE sql
  IMMUTABLE
  SET search_path = pg_catalog
AS $fn$
  SELECT CASE
    WHEN coalesce(p_global, false) THEN NULL::uuid
    WHEN p_record IS NOT NULL THEN p_record
    WHEN cardinality(coalesce(p_actor, ARRAY[]::uuid[])) = 1 THEN p_actor[1]
    ELSE NULL::uuid
  END
$fn$;

REVOKE ALL ON FUNCTION catalog.history_partner(uuid, uuid[], boolean) FROM PUBLIC;

CREATE OR REPLACE FUNCTION catalog.tg_registry_pending() RETURNS trigger
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
    v_actor.actor_user_id, v_actor.admin_type,
    catalog.history_partner(NEW.owner_partner_id, v_actor.resolved_partner_ids, v_actor.resolved_global),
    v_actor.reason, v_actor.correlation_id,
    clock_timestamp());
  RETURN NULL;
END
$fn$;

CREATE OR REPLACE FUNCTION catalog.tg_registry_update() RETURNS trigger
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
    v_actor.actor_user_id, v_actor.admin_type,
    catalog.history_partner(NEW.owner_partner_id, v_actor.resolved_partner_ids, v_actor.resolved_global),
    v_actor.reason, v_actor.correlation_id, clock_timestamp(),
    v_before, v_after);
  DELETE FROM catalog.edit_coord WHERE entry_id = NEW.entry_id AND txn_id = txid_current();
  RETURN NULL;
END
$fn$;
