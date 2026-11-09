CREATE FUNCTION catalog._insert_subtype(p_entry uuid, p_family text, p_code text, p_owner uuid, j jsonb) RETURNS void
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
BEGIN
  CASE p_family
    WHEN 'catalog_release' THEN
      INSERT INTO catalog.catalog_release (entry_id, family, code, release_family, version, source_url, source_digest, imported_at, licence_ref, supersedes_entry_id, display_name, description)
      VALUES (p_entry, p_family, p_code,
        catalog._req_text(j, 'release_family', 1, 200), catalog._req_text(j, 'version', 1, 200),
        catalog._opt_text(j, 'source_url', 1, 200), catalog._opt_text(j, 'source_digest', 1, 200),
        catalog._req_timestamptz(j, 'imported_at'), catalog._opt_text(j, 'licence_ref', 1, 200),
        catalog._opt_uuid(j, 'supersedes_entry_id'), catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000));
    WHEN 'transaction_type' THEN
      INSERT INTO catalog.transaction_type (entry_id, family, code, semantic_key, value_effect, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, catalog._req_text(j, 'semantic_key', 1, 64), catalog._req_text(j, 'value_effect', 1, 64),
        catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'channel' THEN
      INSERT INTO catalog.channel (entry_id, family, code, attendance, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, catalog._req_text(j, 'attendance', 1, 32),
        catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'device_category' THEN
      INSERT INTO catalog.device_category (entry_id, family, code, definition, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, catalog._req_text(j, 'definition', 1, 2000),
        catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'manufacturer' THEN
      INSERT INTO catalog.manufacturer (entry_id, family, code, owner_partner_id, legal_name, public_website, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, p_owner, catalog._req_text(j, 'legal_name', 1, 200), catalog._opt_text(j, 'public_website', 1, 2000),
        catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'device_model' THEN
      INSERT INTO catalog.device_model (entry_id, family, code, owner_partner_id, model_name, variant, region,
        manufacturer_global_id, manufacturer_global_family, manufacturer_private_id, manufacturer_private_family, manufacturer_private_owner,
        category_entry_id, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, p_owner, catalog._req_text(j, 'model_name', 1, 200),
        catalog._opt_text(j, 'variant', 1, 200), catalog._opt_text(j, 'region', 1, 200),
        catalog._opt_uuid(j, 'manufacturer_global_id'),
        CASE WHEN catalog._opt_uuid(j, 'manufacturer_global_id') IS NULL THEN NULL ELSE 'manufacturer' END,
        catalog._opt_uuid(j, 'manufacturer_private_id'),
        CASE WHEN catalog._opt_uuid(j, 'manufacturer_private_id') IS NULL THEN NULL ELSE 'manufacturer' END,
        catalog._opt_uuid(j, 'manufacturer_private_owner'),
        catalog._req_uuid(j, 'category_entry_id'),
        catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'hardware_capability_kind' THEN
      INSERT INTO catalog.hardware_capability_kind (entry_id, family, code, measurement_kind, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, catalog._req_text(j, 'measurement_kind', 1, 16),
        catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'model_hardware_claim' THEN
      INSERT INTO catalog.model_hardware_claim (entry_id, family, code, derived_owner,
        model_global_id, model_global_family, model_private_id, model_private_owner, model_private_family,
        capability_entry_id, claim_value, availability, option_key, source_url, source_version, checked_at, display_name, description)
      VALUES (p_entry, p_family, p_code, p_owner,
        catalog._opt_uuid(j, 'model_global_id'),
        CASE WHEN catalog._opt_uuid(j, 'model_global_id') IS NULL THEN NULL ELSE 'device_model' END,
        catalog._opt_uuid(j, 'model_private_id'), catalog._opt_uuid(j, 'model_private_owner'),
        CASE WHEN catalog._opt_uuid(j, 'model_private_id') IS NULL THEN NULL ELSE 'device_model' END,
        catalog._req_uuid(j, 'capability_entry_id'),
        catalog._req_text(j, 'claim_value', 1, 200), catalog._req_text(j, 'availability', 1, 16),
        catalog._req_text(j, 'option_key', 1, 64), catalog._opt_text(j, 'source_url', 1, 2000),
        catalog._req_text(j, 'source_version', 1, 200), catalog._req_timestamptz(j, 'checked_at'),
        catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000));
    WHEN 'card_network' THEN
      INSERT INTO catalog.card_network (entry_id, family, code, official_name, network_owner, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, catalog._req_text(j, 'official_name', 1, 200), catalog._opt_text(j, 'network_owner', 1, 200),
        catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'card_funding_class' THEN
      INSERT INTO catalog.card_funding_class (entry_id, family, code, is_classification_unknown, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, catalog._req_bool(j, 'is_classification_unknown'),
        catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'payment_rail' THEN
      INSERT INTO catalog.payment_rail (entry_id, family, code, processor_namespace, rail_family, specification_reference, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, catalog._req_text(j, 'processor_namespace', 1, 64), catalog._req_text(j, 'rail_family', 1, 64),
        catalog._opt_text(j, 'specification_reference', 1, 200), catalog._req_text(j, 'display_name', 1, 200),
        catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'check_instrument_kind' THEN
      INSERT INTO catalog.check_instrument_kind (entry_id, family, code, definition, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, catalog._req_text(j, 'definition', 1, 2000), catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'check_drawer_class' THEN
      INSERT INTO catalog.check_drawer_class (entry_id, family, code, definition, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, catalog._req_text(j, 'definition', 1, 2000), catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'check_purpose' THEN
      INSERT INTO catalog.check_purpose (entry_id, family, code, definition, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, catalog._req_text(j, 'definition', 1, 2000), catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'processor' THEN
      INSERT INTO catalog.processor (entry_id, family, code, owner_partner_id, provider_name, service_family, public_url,
        parent_global_id, parent_global_family, parent_private_id, parent_private_family, parent_private_owner,
        display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, p_owner, catalog._req_text(j, 'provider_name', 1, 200),
        catalog._opt_text(j, 'service_family', 1, 200), catalog._opt_text(j, 'public_url', 1, 2000),
        catalog._opt_uuid(j, 'parent_global_id'),
        CASE WHEN catalog._opt_uuid(j, 'parent_global_id') IS NULL THEN NULL ELSE 'processor' END,
        catalog._opt_uuid(j, 'parent_private_id'),
        CASE WHEN catalog._opt_uuid(j, 'parent_private_id') IS NULL THEN NULL ELSE 'processor' END,
        catalog._opt_uuid(j, 'parent_private_owner'),
        catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'transaction_status' THEN
      INSERT INTO catalog.transaction_status (entry_id, family, code, state_dimension, is_terminal, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, catalog._req_text(j, 'state_dimension', 1, 64), catalog._req_bool(j, 'is_terminal'),
        catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'reason_code' THEN
      INSERT INTO catalog.reason_code (entry_id, family, code, owner_partner_id, namespace, category, safe_operator_message, requires_note, customer_message_key, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, p_owner, catalog._req_text(j, 'namespace', 1, 200), catalog._req_text(j, 'category', 1, 200),
        catalog._req_text(j, 'safe_operator_message', 1, 500), catalog._req_bool(j, 'requires_note'), catalog._opt_text(j, 'customer_message_key', 1, 200),
        catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'id_document_type' THEN
      INSERT INTO catalog.id_document_type (entry_id, family, code, owner_partner_id, document_family, capture_method_key, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, p_owner, catalog._req_text(j, 'document_family', 1, 64), catalog._req_text(j, 'capture_method_key', 1, 64),
        catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'currency' THEN
      INSERT INTO catalog.currency (entry_id, family, code, iso_alpha3, iso_numeric3, minor_unit, provenance, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, catalog._req_text(j, 'iso_alpha3', 3, 3), catalog._req_text(j, 'iso_numeric3', 3, 3),
        catalog._req_smallint(j, 'minor_unit', 0, 4), catalog._opt_text(j, 'provenance', 1, 200),
        catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    WHEN 'fee_type' THEN
      INSERT INTO catalog.fee_type (entry_id, family, code, owner_partner_id, definition, display_name, description, release_id)
      VALUES (p_entry, p_family, p_code, p_owner, catalog._req_text(j, 'definition', 1, 2000),
        catalog._req_text(j, 'display_name', 1, 200), catalog._opt_text(j, 'description', 1, 4000), catalog._opt_uuid(j, 'release_id'));
    ELSE
      PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END CASE;
END
$fn$;

CREATE FUNCTION catalog.apply_entry_create(
  p_entry_id uuid,
  p_family text,
  p_entry_class text,
  p_owner_partner_id uuid,
  p_code text,
  p_is_active boolean,
  p_subtype jsonb,
  p_registry_row_count integer DEFAULT 1
) RETURNS uuid
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
DECLARE
  v_actor record;
  v_owner uuid;
  v_at timestamptz;
  v_allowed text[];
  v_release uuid;
BEGIN
  PERFORM catalog._sealed_marker();
  IF p_registry_row_count IS DISTINCT FROM 1 THEN
    PERFORM catalog._err('P0001', 'catalog_create_row_count');
  END IF;
  SELECT * INTO v_actor FROM catalog._actor();
  IF p_entry_id IS NULL OR p_code IS NULL OR p_code !~ '^[a-z0-9][a-z0-9_]{0,62}$' OR p_is_active IS NULL THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END IF;
  IF p_family = 'catalog_release' AND (p_entry_class IS DISTINCT FROM 'provenance' OR p_owner_partner_id IS NOT NULL) THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  ELSIF p_family = 'model_hardware_claim' AND p_entry_class IS DISTINCT FROM 'model_evidence' THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  ELSIF p_family NOT IN ('catalog_release', 'model_hardware_claim') AND p_entry_class IS DISTINCT FROM 'descriptive' THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  ELSIF p_owner_partner_id IS NOT NULL
    AND p_family NOT IN ('manufacturer','device_model','processor','reason_code','id_document_type','fee_type','model_hardware_claim') THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END IF;
  IF v_actor.admin_type = 'super' THEN
    NULL;
  ELSIF v_actor.admin_type = 'partner'
    AND p_entry_class = 'descriptive'
    AND p_family IN ('manufacturer','device_model','processor','reason_code','id_document_type','fee_type')
    AND p_owner_partner_id IS NOT NULL
    AND p_owner_partner_id = v_actor.partner_id THEN
    NULL;
  ELSE
    PERFORM catalog._err('P0001', 'catalog_create_actor');
  END IF;

  v_allowed := CASE p_family
    WHEN 'catalog_release' THEN ARRAY['release_family','version','source_url','source_digest','imported_at','licence_ref','supersedes_entry_id','display_name','description']
    WHEN 'transaction_type' THEN ARRAY['semantic_key','value_effect','display_name','description','release_id']
    WHEN 'channel' THEN ARRAY['attendance','display_name','description','release_id']
    WHEN 'device_category' THEN ARRAY['definition','display_name','description','release_id']
    WHEN 'manufacturer' THEN ARRAY['legal_name','public_website','display_name','description','release_id']
    WHEN 'device_model' THEN ARRAY['model_name','variant','region','manufacturer_global_id','manufacturer_private_id','manufacturer_private_owner','category_entry_id','display_name','description','release_id']
    WHEN 'hardware_capability_kind' THEN ARRAY['measurement_kind','display_name','description','release_id']
    WHEN 'model_hardware_claim' THEN ARRAY['model_global_id','model_private_id','model_private_owner','capability_entry_id','claim_value','availability','option_key','source_url','source_version','checked_at','display_name','description']
    WHEN 'card_network' THEN ARRAY['official_name','network_owner','display_name','description','release_id']
    WHEN 'card_funding_class' THEN ARRAY['is_classification_unknown','display_name','description','release_id']
    WHEN 'payment_rail' THEN ARRAY['processor_namespace','rail_family','specification_reference','display_name','description','release_id']
    WHEN 'check_instrument_kind' THEN ARRAY['definition','display_name','description','release_id']
    WHEN 'check_drawer_class' THEN ARRAY['definition','display_name','description','release_id']
    WHEN 'check_purpose' THEN ARRAY['definition','display_name','description','release_id']
    WHEN 'processor' THEN ARRAY['provider_name','service_family','public_url','parent_global_id','parent_private_id','parent_private_owner','display_name','description','release_id']
    WHEN 'transaction_status' THEN ARRAY['state_dimension','is_terminal','display_name','description','release_id']
    WHEN 'reason_code' THEN ARRAY['namespace','category','safe_operator_message','requires_note','customer_message_key','display_name','description','release_id']
    WHEN 'id_document_type' THEN ARRAY['document_family','capture_method_key','display_name','description','release_id']
    WHEN 'currency' THEN ARRAY['iso_alpha3','iso_numeric3','minor_unit','provenance','display_name','description','release_id']
    WHEN 'fee_type' THEN ARRAY['definition','display_name','description','release_id']
    ELSE NULL
  END;
  IF v_allowed IS NULL THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END IF;
  PERFORM catalog._keys_ok(p_subtype, v_allowed);

  v_owner := p_owner_partner_id;
  IF p_family = 'model_hardware_claim' THEN
    IF catalog._opt_uuid(p_subtype, 'model_global_id') IS NOT NULL THEN
      PERFORM catalog._visible_ref(catalog._req_uuid(p_subtype, 'model_global_id'), 'device_model', NULL, 'global');
      v_owner := NULL;
    ELSIF catalog._opt_uuid(p_subtype, 'model_private_id') IS NOT NULL THEN
      PERFORM catalog._visible_ref(catalog._req_uuid(p_subtype, 'model_private_id'), 'device_model', catalog._req_uuid(p_subtype, 'model_private_owner'), 'same_owner');
      v_owner := catalog._req_uuid(p_subtype, 'model_private_owner');
    ELSE
      PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
    END IF;
    IF p_owner_partner_id IS DISTINCT FROM v_owner THEN
      PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
    END IF;
    PERFORM catalog._visible_ref(catalog._req_uuid(p_subtype, 'capability_entry_id'), 'hardware_capability_kind', NULL, 'global');
  ELSIF p_family = 'device_model' THEN
    IF catalog._opt_uuid(p_subtype, 'manufacturer_global_id') IS NOT NULL
       AND catalog._opt_uuid(p_subtype, 'manufacturer_private_id') IS NOT NULL THEN
      PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
    END IF;
    IF catalog._opt_uuid(p_subtype, 'manufacturer_global_id') IS NOT NULL THEN
      PERFORM catalog._visible_ref(catalog._req_uuid(p_subtype, 'manufacturer_global_id'), 'manufacturer', NULL, 'global');
    ELSIF catalog._opt_uuid(p_subtype, 'manufacturer_private_id') IS NOT NULL THEN
      IF catalog._opt_uuid(p_subtype, 'manufacturer_private_owner') IS DISTINCT FROM p_owner_partner_id THEN
        PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
      END IF;
      PERFORM catalog._visible_ref(catalog._req_uuid(p_subtype, 'manufacturer_private_id'), 'manufacturer', catalog._req_uuid(p_subtype, 'manufacturer_private_owner'), 'same_owner');
    ELSE
      PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
    END IF;
    PERFORM catalog._visible_ref(catalog._req_uuid(p_subtype, 'category_entry_id'), 'device_category', NULL, 'global');
  ELSIF p_family = 'processor' THEN
    IF catalog._opt_uuid(p_subtype, 'parent_global_id') = p_entry_id
       OR catalog._opt_uuid(p_subtype, 'parent_private_id') = p_entry_id THEN
      PERFORM catalog._err('P0001', 'catalog_processor_cycle');
    END IF;
    IF catalog._opt_uuid(p_subtype, 'parent_global_id') IS NOT NULL
       AND catalog._opt_uuid(p_subtype, 'parent_private_id') IS NOT NULL THEN
      PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
    END IF;
    IF catalog._opt_uuid(p_subtype, 'parent_private_id') IS NOT NULL
       AND catalog._opt_uuid(p_subtype, 'parent_private_owner') IS DISTINCT FROM p_owner_partner_id THEN
      PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
    END IF;
    IF catalog._opt_uuid(p_subtype, 'parent_global_id') IS NOT NULL THEN
      PERFORM catalog._visible_ref(catalog._req_uuid(p_subtype, 'parent_global_id'), 'processor', NULL, 'global');
    ELSIF catalog._opt_uuid(p_subtype, 'parent_private_id') IS NOT NULL THEN
      PERFORM catalog._visible_ref(catalog._req_uuid(p_subtype, 'parent_private_id'), 'processor', catalog._req_uuid(p_subtype, 'parent_private_owner'), 'same_owner');
    END IF;
  END IF;
  IF p_family <> 'catalog_release' AND p_family <> 'model_hardware_claim' THEN
    v_release := catalog._opt_uuid(p_subtype, 'release_id');
    IF v_release IS NOT NULL THEN
      PERFORM catalog._visible_ref(v_release, 'catalog_release', NULL, 'global');
    END IF;
  END IF;
  IF p_family = 'catalog_release' AND catalog._opt_uuid(p_subtype, 'supersedes_entry_id') IS NOT NULL THEN
    IF catalog._opt_uuid(p_subtype, 'supersedes_entry_id') = p_entry_id THEN
      PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
    END IF;
    PERFORM catalog._visible_ref(catalog._req_uuid(p_subtype, 'supersedes_entry_id'), 'catalog_release', NULL, 'global');
  END IF;

  v_at := clock_timestamp();
  PERFORM catalog._insert_marker(p_entry_id, 'create');
  INSERT INTO catalog.registered_entry (
    entry_id, family, entry_class, owner_partner_id, code, is_active, row_version,
    created_by, created_at, updated_by, updated_at)
  VALUES (
    p_entry_id, p_family, p_entry_class, v_owner, p_code, p_is_active, 1,
    v_actor.actor_user_id, v_at, v_actor.actor_user_id, v_at);
  PERFORM catalog._insert_subtype(p_entry_id, p_family, p_code, v_owner, p_subtype);
  PERFORM catalog._seal_marker();
  RETURN p_entry_id;
END
$fn$;

CREATE FUNCTION catalog.apply_claim_edit(p_entry_id uuid, p_expected_version bigint, p_patch jsonb) RETURNS void
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog
AS $fn$
DECLARE
  v_actor record;
  v_owner uuid;
  v_found boolean;
  v_version bigint;
  v_sub catalog.model_hardware_claim%ROWTYPE;
  v_reg jsonb;
  v_before jsonb;
  k text;
  v_same boolean := true;
BEGIN
  PERFORM catalog._sealed_marker();
  SELECT * INTO v_actor FROM catalog._actor();
  IF p_entry_id IS NULL THEN
    PERFORM catalog._err('23503', 'catalog_reference_not_found');
  END IF;
  SELECT e.owner_partner_id INTO v_owner
  FROM catalog.registered_entry e
  WHERE e.entry_id = p_entry_id AND e.family = 'model_hardware_claim';
  v_found := FOUND;
  IF NOT v_found THEN
    PERFORM catalog._err('23503', 'catalog_reference_not_found');
  END IF;
  IF v_actor.admin_type = 'partner' AND v_owner IS NOT NULL AND v_owner IS DISTINCT FROM v_actor.partner_id THEN
    PERFORM catalog._err('23503', 'catalog_reference_not_found');
  END IF;
  IF v_actor.admin_type = 'partner' AND v_owner IS NULL THEN
    PERFORM catalog._err('P0001', 'catalog_claim_edit_actor');
  END IF;
  IF v_actor.admin_type <> 'super' AND NOT (v_actor.admin_type = 'partner' AND v_owner IS NOT NULL AND v_owner = v_actor.partner_id) THEN
    PERFORM catalog._err('P0001', 'catalog_claim_edit_actor');
  END IF;

  SELECT e.row_version INTO v_version
  FROM catalog.registered_entry e
  WHERE e.entry_id = p_entry_id AND e.family = 'model_hardware_claim'
  FOR UPDATE;
  IF p_expected_version IS NULL OR p_expected_version < 1 OR v_version IS DISTINCT FROM p_expected_version THEN
    PERFORM catalog._err('P0001', 'catalog_version_conflict');
  END IF;

  PERFORM catalog._keys_ok(p_patch, ARRAY['claim_value','availability','source_url','checked_at','display_name','description']);
  SELECT * INTO v_sub FROM catalog.model_hardware_claim WHERE entry_id = p_entry_id FOR UPDATE;
  IF NOT FOUND THEN
    PERFORM catalog._err('23503', 'catalog_reference_not_found');
  END IF;
  FOR k IN SELECT jsonb_object_keys(p_patch) LOOP
    IF k = 'claim_value' AND catalog._req_text(p_patch, k, 1, 200) IS DISTINCT FROM v_sub.claim_value THEN v_same := false; END IF;
    IF k = 'availability' AND catalog._req_text(p_patch, k, 1, 16) IS DISTINCT FROM v_sub.availability THEN v_same := false; END IF;
    IF k = 'source_url' AND catalog._opt_text(p_patch, k, 1, 2000) IS DISTINCT FROM v_sub.source_url THEN v_same := false; END IF;
    IF k = 'checked_at' AND catalog._req_timestamptz(p_patch, k) IS DISTINCT FROM v_sub.checked_at THEN v_same := false; END IF;
    IF k = 'display_name' AND catalog._req_text(p_patch, k, 1, 200) IS DISTINCT FROM v_sub.display_name THEN v_same := false; END IF;
    IF k = 'description' AND catalog._opt_text(p_patch, k, 1, 4000) IS DISTINCT FROM v_sub.description THEN v_same := false; END IF;
  END LOOP;
  IF v_same THEN
    PERFORM catalog._err('P0001', 'catalog_noop');
  END IF;
  IF p_patch->>'availability' IS NOT NULL AND p_patch->>'availability' NOT IN ('standard', 'optional', 'unknown') THEN
    PERFORM catalog._err('P0001', 'catalog_column_matrix_unsettled');
  END IF;

  SELECT to_jsonb(e) INTO v_reg FROM catalog.registered_entry e WHERE e.entry_id = p_entry_id;
  v_before := jsonb_build_object('registry', v_reg, 'subtype', to_jsonb(v_sub));
  PERFORM catalog._insert_marker(p_entry_id, 'claim-edit');
  INSERT INTO catalog.edit_coord (entry_id, txn_id, expected_version, before, patch)
  VALUES (p_entry_id, txid_current(), v_version, v_before, p_patch);
  UPDATE catalog.model_hardware_claim SET
    claim_value = CASE WHEN p_patch ? 'claim_value' THEN catalog._req_text(p_patch, 'claim_value', 1, 200) ELSE claim_value END,
    availability = CASE WHEN p_patch ? 'availability' THEN catalog._req_text(p_patch, 'availability', 1, 16) ELSE availability END,
    source_url = CASE WHEN p_patch ? 'source_url' THEN catalog._opt_text(p_patch, 'source_url', 1, 2000) ELSE source_url END,
    checked_at = CASE WHEN p_patch ? 'checked_at' THEN catalog._req_timestamptz(p_patch, 'checked_at') ELSE checked_at END,
    display_name = CASE WHEN p_patch ? 'display_name' THEN catalog._req_text(p_patch, 'display_name', 1, 200) ELSE display_name END,
    description = CASE WHEN p_patch ? 'description' THEN catalog._opt_text(p_patch, 'description', 1, 4000) ELSE description END
  WHERE entry_id = p_entry_id;
  UPDATE catalog.registered_entry SET
    row_version = row_version + 1,
    updated_by = v_actor.actor_user_id,
    updated_at = clock_timestamp()
  WHERE entry_id = p_entry_id AND row_version = v_version;
  IF NOT FOUND THEN
    PERFORM catalog._err('P0001', 'catalog_version_conflict');
  END IF;
  PERFORM catalog._seal_marker();
END
$fn$;
