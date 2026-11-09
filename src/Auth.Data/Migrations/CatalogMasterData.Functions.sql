CREATE FUNCTION catalog._err(p_state text, p_message text) RETURNS void
  LANGUAGE plpgsql
  SET search_path = pg_catalog
AS $fn$
BEGIN
  RAISE EXCEPTION USING ERRCODE = p_state, MESSAGE = p_message;
END
$fn$;

CREATE FUNCTION catalog._actor(OUT admin_type text, OUT partner_id uuid, OUT actor_user_id uuid, OUT correlation_id text, OUT reason text)
  LANGUAGE plpgsql
  SET search_path = pg_catalog
AS $fn$
BEGIN
  admin_type := current_setting('app.admin_type', true);
  IF admin_type IS NULL OR admin_type NOT IN ('super', 'partner') THEN
    PERFORM catalog._err('P0001', 'catalog_actor_context');
  END IF;
  IF admin_type = 'partner' THEN
    BEGIN
      partner_id := nullif(current_setting('app.partner_id', true), '')::uuid;
    EXCEPTION WHEN invalid_text_representation THEN
      PERFORM catalog._err('P0001', 'catalog_actor_context');
    END;
    IF partner_id IS NULL THEN
      PERFORM catalog._err('P0001', 'catalog_actor_context');
    END IF;
  END IF;
  BEGIN
    actor_user_id := nullif(current_setting('app.actor_user_id', true), '')::uuid;
  EXCEPTION WHEN invalid_text_representation THEN
    PERFORM catalog._err('P0001', 'catalog_actor_context');
  END;
  IF actor_user_id IS NULL THEN
    PERFORM catalog._err('P0001', 'catalog_actor_context');
  END IF;
  correlation_id := nullif(current_setting('app.correlation_id', true), '');
  IF correlation_id IS NULL THEN
    PERFORM catalog._err('P0001', 'catalog_actor_context');
  END IF;
  reason := nullif(current_setting('app.reason', true), '');
  IF reason IS NOT NULL AND (reason <> btrim(reason) OR char_length(reason) NOT BETWEEN 1 AND 500) THEN
    PERFORM catalog._err('P0001', 'catalog_reason_required');
  END IF;
END
$fn$;

CREATE FUNCTION catalog._keys_ok(p_patch jsonb, p_allowed text[]) RETURNS void
  LANGUAGE plpgsql
  SET search_path = pg_catalog
AS $fn$
DECLARE k text;
BEGIN
  IF p_patch IS NULL OR jsonb_typeof(p_patch) <> 'object' THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END IF;
  FOR k IN SELECT jsonb_object_keys(p_patch) LOOP
    IF NOT (k = ANY (p_allowed)) THEN
      PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
    END IF;
  END LOOP;
END
$fn$;

CREATE FUNCTION catalog._req_text(j jsonb, k text, lo int, hi int) RETURNS text
  LANGUAGE plpgsql IMMUTABLE
  SET search_path = pg_catalog
AS $fn$
DECLARE v text;
BEGIN
  IF j IS NULL OR NOT (j ? k) OR jsonb_typeof(j->k) = 'null' THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END IF;
  v := j->>k;
  IF v IS NULL OR char_length(v) < lo OR char_length(v) > hi THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END IF;
  RETURN v;
END
$fn$;

CREATE FUNCTION catalog._opt_text(j jsonb, k text, lo int, hi int) RETURNS text
  LANGUAGE plpgsql IMMUTABLE
  SET search_path = pg_catalog
AS $fn$
DECLARE v text;
BEGIN
  IF j IS NULL OR NOT (j ? k) OR jsonb_typeof(j->k) = 'null' THEN
    RETURN NULL;
  END IF;
  v := j->>k;
  IF v IS NULL OR char_length(v) < lo OR char_length(v) > hi THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END IF;
  RETURN v;
END
$fn$;

CREATE FUNCTION catalog._req_bool(j jsonb, k text) RETURNS boolean
  LANGUAGE plpgsql IMMUTABLE
  SET search_path = pg_catalog
AS $fn$
BEGIN
  IF j IS NULL OR NOT (j ? k) OR jsonb_typeof(j->k) <> 'boolean' THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END IF;
  RETURN (j->>k)::boolean;
END
$fn$;

CREATE FUNCTION catalog._opt_uuid(j jsonb, k text) RETURNS uuid
  LANGUAGE plpgsql IMMUTABLE
  SET search_path = pg_catalog
AS $fn$
BEGIN
  IF j IS NULL OR NOT (j ? k) OR jsonb_typeof(j->k) = 'null' THEN
    RETURN NULL;
  END IF;
  BEGIN
    RETURN (j->>k)::uuid;
  EXCEPTION WHEN invalid_text_representation THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END;
END
$fn$;

CREATE FUNCTION catalog._req_uuid(j jsonb, k text) RETURNS uuid
  LANGUAGE plpgsql IMMUTABLE
  SET search_path = pg_catalog
AS $fn$
DECLARE v uuid;
BEGIN
  v := catalog._opt_uuid(j, k);
  IF v IS NULL THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END IF;
  RETURN v;
END
$fn$;

CREATE FUNCTION catalog._req_timestamptz(j jsonb, k text) RETURNS timestamptz
  LANGUAGE plpgsql IMMUTABLE
  SET search_path = pg_catalog
AS $fn$
BEGIN
  IF j IS NULL OR NOT (j ? k) OR jsonb_typeof(j->k) = 'null' THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END IF;
  BEGIN
    RETURN (j->>k)::timestamptz;
  EXCEPTION WHEN invalid_text_representation OR datetime_field_overflow THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END;
END
$fn$;

CREATE FUNCTION catalog._req_smallint(j jsonb, k text, lo int, hi int) RETURNS smallint
  LANGUAGE plpgsql IMMUTABLE
  SET search_path = pg_catalog
AS $fn$
DECLARE n int;
BEGIN
  IF j IS NULL OR NOT (j ? k) OR jsonb_typeof(j->k) <> 'number' THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END IF;
  n := (j->>k)::int;
  IF n < lo OR n > hi THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END IF;
  RETURN n::smallint;
END
$fn$;

-- Missing and hidden references share one public status and body. No id, detail, or hint.
-- p_mode global: target owner must be null. same_owner: target owner must equal p_owner.
-- visible: target is global or owned by p_owner. Missing, wrong family, and hidden share one body.
CREATE FUNCTION catalog._visible_ref(p_entry uuid, p_family text, p_owner uuid, p_mode text) RETURNS void
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
DECLARE v_owner uuid;
BEGIN
  SELECT e.owner_partner_id INTO v_owner
  FROM catalog.registered_entry e
  WHERE e.entry_id = p_entry AND e.family = p_family;
  IF NOT FOUND
     OR (p_mode = 'global' AND v_owner IS NOT NULL)
     OR (p_mode = 'same_owner' AND (v_owner IS NULL OR v_owner IS DISTINCT FROM p_owner))
     OR (p_mode = 'visible' AND v_owner IS NOT NULL AND v_owner IS DISTINCT FROM p_owner) THEN
    PERFORM catalog._err('23503', 'catalog_reference_not_found');
  END IF;
END
$fn$;

CREATE FUNCTION catalog._sealed_marker() RETURNS void
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
END
$fn$;

CREATE FUNCTION catalog._insert_marker(p_entry uuid, p_kind text) RETURNS void
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
BEGIN
  IF EXISTS (SELECT 1 FROM catalog.tx_marker m WHERE m.txn_id = pg_current_xact_id()) THEN
    RETURN;
  END IF;
  INSERT INTO catalog.tx_marker (txn_id, entry_id, action_kind, sealed)
  VALUES (pg_current_xact_id(), p_entry, p_kind, false);
END
$fn$;

CREATE FUNCTION catalog._seal_marker() RETURNS void
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
BEGIN
  UPDATE catalog.tx_marker SET sealed = true
  WHERE txn_id = pg_current_xact_id() AND sealed = false;
  IF NOT FOUND THEN
    PERFORM catalog._err('P0001', 'catalog_transaction_action');
  END IF;
END
$fn$;

CREATE FUNCTION catalog.subtype_count(p_entry uuid) RETURNS integer
  LANGUAGE sql
  STABLE
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
  SELECT (
    (SELECT count(*) FROM catalog.catalog_release WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.transaction_type WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.channel WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.device_category WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.manufacturer WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.device_model WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.hardware_capability_kind WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.model_hardware_claim WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.card_network WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.card_funding_class WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.payment_rail WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.check_instrument_kind WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.check_drawer_class WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.check_purpose WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.processor WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.transaction_status WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.reason_code WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.id_document_type WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.currency WHERE entry_id = p_entry) +
    (SELECT count(*) FROM catalog.fee_type WHERE entry_id = p_entry)
  )::integer
$fn$;

CREATE FUNCTION catalog.subtype_json(p_entry uuid) RETURNS jsonb
  LANGUAGE sql
  STABLE
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
  SELECT COALESCE(
    (SELECT to_jsonb(t) FROM catalog.catalog_release t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.transaction_type t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.channel t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.device_category t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.manufacturer t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.device_model t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.hardware_capability_kind t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.model_hardware_claim t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.card_network t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.card_funding_class t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.payment_rail t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.check_instrument_kind t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.check_drawer_class t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.check_purpose t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.processor t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.transaction_status t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.reason_code t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.id_document_type t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.currency t WHERE t.entry_id = p_entry),
    (SELECT to_jsonb(t) FROM catalog.fee_type t WHERE t.entry_id = p_entry)
  )
$fn$;
