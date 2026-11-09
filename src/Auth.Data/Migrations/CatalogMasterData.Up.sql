-- DEC-161 catalog foundation. Additive. No seed. No outbox writer. No cause table.
-- No flag definer. economic_role is not a column. Nested dollar quotes use their own tags.

CREATE SCHEMA catalog;
REVOKE ALL ON SCHEMA catalog FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA catalog REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA catalog REVOKE ALL ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA catalog REVOKE ALL ON SEQUENCES FROM PUBLIC;

CREATE TABLE catalog.registered_entry (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  entry_class text NOT NULL,
  owner_partner_id uuid NULL,
  code text NOT NULL,
  is_active boolean NOT NULL,
  row_version bigint NOT NULL,
  created_by uuid NOT NULL,
  created_at timestamptz NOT NULL,
  updated_by uuid NOT NULL,
  updated_at timestamptz NOT NULL,
  CONSTRAINT registered_entry_pkey PRIMARY KEY (entry_id),
  CONSTRAINT registered_entry_family CHECK (family IN (
    'catalog_release','transaction_type','channel','device_category','manufacturer',
    'device_model','hardware_capability_kind','model_hardware_claim','card_network',
    'card_funding_class','payment_rail','check_instrument_kind','check_drawer_class',
    'check_purpose','processor','transaction_status','reason_code','id_document_type',
    'currency','fee_type')),
  CONSTRAINT registered_entry_class CHECK (
    (entry_class = 'descriptive'
      AND family NOT IN ('model_hardware_claim', 'catalog_release')
      AND (owner_partner_id IS NULL OR family IN (
        'manufacturer','device_model','processor','reason_code','id_document_type','fee_type')))
    OR (entry_class = 'provenance' AND family = 'catalog_release' AND owner_partner_id IS NULL)
    OR (entry_class = 'model_evidence' AND family = 'model_hardware_claim')),
  CONSTRAINT registered_entry_code_shape CHECK (code ~ '^[a-z0-9][a-z0-9_]{0,62}$'),
  CONSTRAINT registered_entry_version CHECK (row_version >= 1)
);
-- Technical code pattern and within-owner uniqueness. Not DEC170 owner policy.
CREATE UNIQUE INDEX registered_entry_id_family_uq ON catalog.registered_entry (entry_id, family);
CREATE UNIQUE INDEX registered_entry_id_family_code_uq ON catalog.registered_entry (entry_id, family, code);
CREATE UNIQUE INDEX registered_entry_id_family_owner_uq ON catalog.registered_entry (entry_id, family, owner_partner_id) NULLS NOT DISTINCT;
CREATE UNIQUE INDEX registered_entry_global_code_uq ON catalog.registered_entry (family, code) WHERE owner_partner_id IS NULL;
CREATE UNIQUE INDEX centry_private_code_uq ON catalog.registered_entry (family, owner_partner_id, code) WHERE owner_partner_id IS NOT NULL;

CREATE TABLE catalog.ref_global (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  CONSTRAINT ref_global_pkey PRIMARY KEY (entry_id, family),
  CONSTRAINT ref_global_entry_fk FOREIGN KEY (entry_id, family)
    REFERENCES catalog.registered_entry (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);
CREATE TABLE catalog.ref_private (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  owner_partner_id uuid NOT NULL,
  CONSTRAINT ref_private_pkey PRIMARY KEY (entry_id, family, owner_partner_id),
  CONSTRAINT ref_private_family CHECK (family IN (
    'manufacturer','device_model','processor','reason_code','id_document_type','fee_type')),
  CONSTRAINT ref_private_entry_fk FOREIGN KEY (entry_id, family)
    REFERENCES catalog.registered_entry (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);
CREATE TABLE catalog.global_device_model (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  CONSTRAINT global_device_model_pkey PRIMARY KEY (entry_id, family),
  CONSTRAINT global_device_model_family CHECK (family = 'device_model'),
  CONSTRAINT global_device_model_ref_fk FOREIGN KEY (entry_id, family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

-- Subtypes. display_name and description have no default. release_id is not a dependency edge.
CREATE TABLE catalog.catalog_release (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  release_family text NOT NULL,
  version text NOT NULL,
  source_url text NULL,
  source_digest text NULL,
  imported_at timestamptz NOT NULL,
  licence_ref text NULL,
  supersedes_entry_id uuid NULL,
  supersedes_family text GENERATED ALWAYS AS (
    CASE WHEN supersedes_entry_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  display_name text NOT NULL,
  description text NULL,
  CONSTRAINT catalog_release_pkey PRIMARY KEY (entry_id),
  CONSTRAINT catalog_release_family CHECK (family = 'catalog_release'),
  CONSTRAINT catalog_release_text CHECK (
    char_length(release_family) BETWEEN 1 AND 200
    AND char_length(version) BETWEEN 1 AND 200
    AND (source_url IS NULL OR char_length(source_url) BETWEEN 1 AND 200)
    AND (source_digest IS NULL OR char_length(source_digest) BETWEEN 1 AND 200)
    AND (licence_ref IS NULL OR char_length(licence_ref) BETWEEN 1 AND 200)
    AND char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT catalog_release_noself CHECK (supersedes_entry_id IS DISTINCT FROM entry_id),
  CONSTRAINT catalog_release_version_uq UNIQUE (release_family, version),
  CONSTRAINT catalog_release_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT catalog_release_supersedes_fk FOREIGN KEY (supersedes_entry_id, supersedes_family)
    REFERENCES catalog.registered_entry (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.transaction_type (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  semantic_key text NOT NULL,
  value_effect text NOT NULL,
  display_name text NOT NULL,
  description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT transaction_type_pkey PRIMARY KEY (entry_id),
  CONSTRAINT transaction_type_family CHECK (family = 'transaction_type'),
  CONSTRAINT transaction_type_text CHECK (
    char_length(semantic_key) BETWEEN 1 AND 64 AND char_length(value_effect) BETWEEN 1 AND 64
    AND char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT transaction_type_semantic_uq UNIQUE (semantic_key),
  CONSTRAINT transaction_type_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT transaction_type_release_fk FOREIGN KEY (release_id, release_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.channel (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  attendance text NOT NULL,
  display_name text NOT NULL,
  description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT channel_pkey PRIMARY KEY (entry_id),
  CONSTRAINT channel_family CHECK (family = 'channel'),
  CONSTRAINT channel_attendance CHECK (attendance IN ('staffed', 'unattended')),
  CONSTRAINT channel_text CHECK (
    char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT channel_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT channel_release_fk FOREIGN KEY (release_id, release_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.device_category (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  definition text NOT NULL,
  display_name text NOT NULL,
  description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT device_category_pkey PRIMARY KEY (entry_id),
  CONSTRAINT device_category_family CHECK (family = 'device_category'),
  CONSTRAINT device_category_text CHECK (
    char_length(definition) BETWEEN 1 AND 2000
    AND char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT device_category_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT device_category_release_fk FOREIGN KEY (release_id, release_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.manufacturer (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  owner_partner_id uuid NULL,
  legal_name text NOT NULL,
  public_website text NULL,
  display_name text NOT NULL,
  description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT manufacturer_pkey PRIMARY KEY (entry_id),
  CONSTRAINT manufacturer_family CHECK (family = 'manufacturer'),
  CONSTRAINT manufacturer_text CHECK (
    char_length(legal_name) BETWEEN 1 AND 200
    AND (public_website IS NULL OR char_length(public_website) BETWEEN 1 AND 2000)
    AND char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT manufacturer_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT manufacturer_release_fk FOREIGN KEY (release_id, release_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.device_model (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  owner_partner_id uuid NULL,
  model_name text NOT NULL,
  variant text NULL,
  region text NULL,
  manufacturer_global_id uuid NULL,
  manufacturer_global_family text NULL,
  manufacturer_private_id uuid NULL,
  manufacturer_private_family text NULL,
  manufacturer_private_owner uuid NULL,
  manufacturer_entry_id uuid GENERATED ALWAYS AS (COALESCE(manufacturer_global_id, manufacturer_private_id)) STORED,
  category_entry_id uuid NOT NULL,
  category_family text GENERATED ALWAYS AS ('device_category') STORED,
  display_name text NOT NULL,
  description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT device_model_pkey PRIMARY KEY (entry_id),
  CONSTRAINT device_model_family CHECK (family = 'device_model'),
  CONSTRAINT device_model_name CHECK (char_length(model_name) BETWEEN 1 AND 200),
  CONSTRAINT device_model_variant CHECK (variant IS NULL OR char_length(variant) BETWEEN 1 AND 200),
  CONSTRAINT device_model_region CHECK (region IS NULL OR char_length(region) BETWEEN 1 AND 200),
  CONSTRAINT device_model_text CHECK (
    char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT device_model_mfr_path CHECK (
    (manufacturer_global_id IS NOT NULL AND manufacturer_global_family = 'manufacturer'
      AND manufacturer_private_id IS NULL AND manufacturer_private_family IS NULL AND manufacturer_private_owner IS NULL)
    OR
    (manufacturer_private_id IS NOT NULL AND manufacturer_private_family = 'manufacturer' AND manufacturer_private_owner IS NOT NULL
      AND manufacturer_global_id IS NULL AND manufacturer_global_family IS NULL
      AND owner_partner_id IS NOT NULL AND manufacturer_private_owner = owner_partner_id)),
  CONSTRAINT device_model_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT device_model_mfr_global_fk FOREIGN KEY (manufacturer_global_id, manufacturer_global_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT device_model_mfr_private_fk FOREIGN KEY (manufacturer_private_id, manufacturer_private_family, manufacturer_private_owner)
    REFERENCES catalog.ref_private (entry_id, family, owner_partner_id) ON DELETE RESTRICT ON UPDATE RESTRICT DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT device_model_category_fk FOREIGN KEY (category_entry_id, category_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT device_model_release_fk FOREIGN KEY (release_id, release_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);
CREATE UNIQUE INDEX device_model_global_tuple_uq ON catalog.device_model
  (manufacturer_entry_id, model_name, variant, region) NULLS NOT DISTINCT
  WHERE owner_partner_id IS NULL;
CREATE UNIQUE INDEX device_model_private_tuple_uq ON catalog.device_model
  (owner_partner_id, manufacturer_entry_id, model_name, variant, region) NULLS NOT DISTINCT
  WHERE owner_partner_id IS NOT NULL;

CREATE TABLE catalog.hardware_capability_kind (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  measurement_kind text NOT NULL,
  display_name text NOT NULL,
  description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT hardware_capability_kind_pkey PRIMARY KEY (entry_id),
  CONSTRAINT hardware_capability_kind_family CHECK (family = 'hardware_capability_kind'),
  CONSTRAINT hardware_capability_kind_measure CHECK (measurement_kind IN ('boolean', 'count', 'text')),
  CONSTRAINT hardware_capability_kind_text CHECK (
    char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT hardware_capability_kind_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT hardware_capability_kind_release_fk FOREIGN KEY (release_id, release_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.model_hardware_claim (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  derived_owner uuid NULL,
  model_global_id uuid NULL,
  model_global_family text NULL,
  model_private_id uuid NULL,
  model_private_owner uuid NULL,
  model_private_family text NULL,
  model_entry_id uuid GENERATED ALWAYS AS (COALESCE(model_global_id, model_private_id)) STORED,
  capability_entry_id uuid NOT NULL,
  capability_family text GENERATED ALWAYS AS ('hardware_capability_kind') STORED,
  claim_value text NOT NULL,
  availability text NOT NULL,
  option_key text NOT NULL,
  source_url text NULL,
  source_version text NOT NULL,
  checked_at timestamptz NOT NULL,
  display_name text NOT NULL,
  description text NULL,
  CONSTRAINT model_hardware_claim_pkey PRIMARY KEY (entry_id),
  CONSTRAINT model_hardware_claim_family CHECK (family = 'model_hardware_claim'),
  CONSTRAINT model_hardware_claim_path CHECK (
    (model_global_id IS NOT NULL AND model_global_family = 'device_model'
      AND model_private_id IS NULL AND model_private_owner IS NULL AND model_private_family IS NULL)
    OR
    (model_private_id IS NOT NULL AND model_private_owner IS NOT NULL AND model_private_family = 'device_model'
      AND model_global_id IS NULL AND model_global_family IS NULL)),
  CONSTRAINT model_hardware_claim_derived CHECK (derived_owner IS NOT DISTINCT FROM model_private_owner),
  CONSTRAINT model_hardware_claim_value CHECK (
    char_length(claim_value) BETWEEN 1 AND 200
    AND availability IN ('standard', 'optional', 'unknown')
    AND char_length(option_key) BETWEEN 1 AND 64
    AND char_length(source_version) BETWEEN 1 AND 200
    AND (source_url IS NULL OR char_length(source_url) BETWEEN 1 AND 2000)
    AND char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT model_hardware_claim_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT model_hardware_claim_model_global_fk FOREIGN KEY (model_global_id, model_global_family)
    REFERENCES catalog.global_device_model (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT model_hardware_claim_model_private_fk FOREIGN KEY (model_private_id, model_private_family, model_private_owner)
    REFERENCES catalog.ref_private (entry_id, family, owner_partner_id) ON DELETE RESTRICT ON UPDATE RESTRICT DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT model_hardware_claim_capability_fk FOREIGN KEY (capability_entry_id, capability_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT DEFERRABLE INITIALLY DEFERRED
);
CREATE UNIQUE INDEX model_hardware_claim_identity_uq ON catalog.model_hardware_claim
  (model_entry_id, capability_entry_id, option_key, source_version, derived_owner) NULLS NOT DISTINCT;

CREATE TABLE catalog.card_network (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  official_name text NOT NULL,
  network_owner text NULL,
  display_name text NOT NULL,
  description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT card_network_pkey PRIMARY KEY (entry_id),
  CONSTRAINT card_network_family CHECK (family = 'card_network'),
  CONSTRAINT card_network_text CHECK (
    char_length(official_name) BETWEEN 1 AND 200
    AND (network_owner IS NULL OR char_length(network_owner) BETWEEN 1 AND 200)
    AND char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT card_network_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT card_network_release_fk FOREIGN KEY (release_id, release_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.card_funding_class (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  is_classification_unknown boolean NOT NULL,
  display_name text NOT NULL,
  description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT card_funding_class_pkey PRIMARY KEY (entry_id),
  CONSTRAINT card_funding_class_family CHECK (family = 'card_funding_class'),
  CONSTRAINT card_funding_class_text CHECK (
    char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT card_funding_class_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT card_funding_class_release_fk FOREIGN KEY (release_id, release_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.payment_rail (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  processor_namespace text NOT NULL,
  rail_family text NOT NULL,
  specification_reference text NULL,
  display_name text NOT NULL,
  description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT payment_rail_pkey PRIMARY KEY (entry_id),
  CONSTRAINT payment_rail_family CHECK (family = 'payment_rail'),
  CONSTRAINT payment_rail_text CHECK (
    char_length(processor_namespace) BETWEEN 1 AND 64
    AND char_length(rail_family) BETWEEN 1 AND 64
    AND (specification_reference IS NULL OR char_length(specification_reference) BETWEEN 1 AND 200)
    AND char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT payment_rail_ns_uq UNIQUE (processor_namespace, code),
  CONSTRAINT payment_rail_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT payment_rail_release_fk FOREIGN KEY (release_id, release_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.check_instrument_kind (
  entry_id uuid NOT NULL, family text NOT NULL, code text NOT NULL,
  definition text NOT NULL, display_name text NOT NULL, description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT check_instrument_kind_pkey PRIMARY KEY (entry_id),
  CONSTRAINT check_instrument_kind_family CHECK (family = 'check_instrument_kind'),
  CONSTRAINT check_instrument_kind_text CHECK (char_length(definition) BETWEEN 1 AND 2000 AND char_length(display_name) BETWEEN 1 AND 200 AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT check_instrument_kind_reg_fk FOREIGN KEY (entry_id, family, code) REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT check_instrument_kind_release_fk FOREIGN KEY (release_id, release_family) REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);
CREATE TABLE catalog.check_drawer_class (
  entry_id uuid NOT NULL, family text NOT NULL, code text NOT NULL,
  definition text NOT NULL, display_name text NOT NULL, description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT check_drawer_class_pkey PRIMARY KEY (entry_id),
  CONSTRAINT check_drawer_class_family CHECK (family = 'check_drawer_class'),
  CONSTRAINT check_drawer_class_text CHECK (char_length(definition) BETWEEN 1 AND 2000 AND char_length(display_name) BETWEEN 1 AND 200 AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT check_drawer_class_reg_fk FOREIGN KEY (entry_id, family, code) REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT check_drawer_class_release_fk FOREIGN KEY (release_id, release_family) REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);
CREATE TABLE catalog.check_purpose (
  entry_id uuid NOT NULL, family text NOT NULL, code text NOT NULL,
  definition text NOT NULL, display_name text NOT NULL, description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT check_purpose_pkey PRIMARY KEY (entry_id),
  CONSTRAINT check_purpose_family CHECK (family = 'check_purpose'),
  CONSTRAINT check_purpose_text CHECK (char_length(definition) BETWEEN 1 AND 2000 AND char_length(display_name) BETWEEN 1 AND 200 AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT check_purpose_reg_fk FOREIGN KEY (entry_id, family, code) REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT check_purpose_release_fk FOREIGN KEY (release_id, release_family) REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.processor (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  owner_partner_id uuid NULL,
  provider_name text NOT NULL,
  service_family text NULL,
  public_url text NULL,
  parent_global_id uuid NULL,
  parent_global_family text NULL,
  parent_private_id uuid NULL,
  parent_private_family text NULL,
  parent_private_owner uuid NULL,
  parent_entry_id uuid GENERATED ALWAYS AS (COALESCE(parent_global_id, parent_private_id)) STORED,
  display_name text NOT NULL,
  description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT processor_pkey PRIMARY KEY (entry_id),
  CONSTRAINT processor_family CHECK (family = 'processor'),
  CONSTRAINT processor_text CHECK (
    char_length(provider_name) BETWEEN 1 AND 200
    AND (service_family IS NULL OR char_length(service_family) BETWEEN 1 AND 200)
    AND (public_url IS NULL OR char_length(public_url) BETWEEN 1 AND 2000)
    AND char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT processor_parent_path CHECK (
    (parent_global_id IS NULL AND parent_global_family IS NULL AND parent_private_id IS NULL AND parent_private_family IS NULL AND parent_private_owner IS NULL)
    OR (parent_global_id IS NOT NULL AND parent_global_family = 'processor'
        AND parent_private_id IS NULL AND parent_private_family IS NULL AND parent_private_owner IS NULL)
    OR (parent_global_id IS NULL AND parent_global_family IS NULL
        AND parent_private_id IS NOT NULL AND parent_private_family = 'processor' AND parent_private_owner IS NOT NULL
        AND parent_private_owner IS NOT DISTINCT FROM owner_partner_id)),
  CONSTRAINT processor_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT processor_parent_global_fk FOREIGN KEY (parent_global_id, parent_global_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT processor_parent_private_fk FOREIGN KEY (parent_private_id, parent_private_family, parent_private_owner)
    REFERENCES catalog.ref_private (entry_id, family, owner_partner_id) ON DELETE RESTRICT ON UPDATE RESTRICT DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT processor_release_fk FOREIGN KEY (release_id, release_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.transaction_status (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  state_dimension text NOT NULL,
  is_terminal boolean NOT NULL,
  display_name text NOT NULL,
  description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT transaction_status_pkey PRIMARY KEY (entry_id),
  CONSTRAINT transaction_status_family CHECK (family = 'transaction_status'),
  CONSTRAINT transaction_status_text CHECK (
    char_length(state_dimension) BETWEEN 1 AND 64
    AND char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT transaction_status_dim_uq UNIQUE (state_dimension, code),
  CONSTRAINT transaction_status_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT transaction_status_release_fk FOREIGN KEY (release_id, release_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.reason_code (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  owner_partner_id uuid NULL,
  namespace text NOT NULL,
  category text NOT NULL,
  safe_operator_message text NOT NULL,
  requires_note boolean NOT NULL,
  customer_message_key text NULL,
  display_name text NOT NULL,
  description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT reason_code_pkey PRIMARY KEY (entry_id),
  CONSTRAINT reason_code_family CHECK (family = 'reason_code'),
  CONSTRAINT reason_code_text CHECK (
    char_length(namespace) BETWEEN 1 AND 200 AND char_length(category) BETWEEN 1 AND 200
    AND char_length(safe_operator_message) BETWEEN 1 AND 500
    AND (customer_message_key IS NULL OR char_length(customer_message_key) BETWEEN 1 AND 200)
    AND char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT reason_code_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT reason_code_release_fk FOREIGN KEY (release_id, release_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);
CREATE UNIQUE INDEX reason_code_identity_uq ON catalog.reason_code
  (owner_partner_id, namespace, category, code) NULLS NOT DISTINCT;

CREATE TABLE catalog.id_document_type (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  owner_partner_id uuid NULL,
  document_family text NOT NULL,
  capture_method_key text NOT NULL,
  display_name text NOT NULL,
  description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT id_document_type_pkey PRIMARY KEY (entry_id),
  CONSTRAINT id_document_type_family CHECK (family = 'id_document_type'),
  CONSTRAINT id_document_type_text CHECK (
    char_length(document_family) BETWEEN 1 AND 64 AND char_length(capture_method_key) BETWEEN 1 AND 64
    AND char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT id_document_type_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT id_document_type_release_fk FOREIGN KEY (release_id, release_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.currency (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  iso_alpha3 text NOT NULL,
  iso_numeric3 text NOT NULL,
  minor_unit smallint NOT NULL,
  provenance text NULL,
  display_name text NOT NULL,
  description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT currency_pkey PRIMARY KEY (entry_id),
  CONSTRAINT currency_family CHECK (family = 'currency'),
  CONSTRAINT currency_iso CHECK (iso_alpha3 ~ '^[A-Z]{3}$' AND iso_numeric3 ~ '^[0-9]{3}$' AND minor_unit BETWEEN 0 AND 4),
  CONSTRAINT currency_text CHECK (
    (provenance IS NULL OR char_length(provenance) BETWEEN 1 AND 200)
    AND char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT currency_alpha_uq UNIQUE (iso_alpha3),
  CONSTRAINT currency_num_uq UNIQUE (iso_numeric3),
  CONSTRAINT currency_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT currency_release_fk FOREIGN KEY (release_id, release_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

-- Names only. No economic_role column, check, default, or dummy value.
CREATE TABLE catalog.fee_type (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  code text NOT NULL,
  owner_partner_id uuid NULL,
  definition text NOT NULL,
  display_name text NOT NULL,
  description text NULL,
  release_id uuid NULL,
  release_family text GENERATED ALWAYS AS (CASE WHEN release_id IS NULL THEN NULL ELSE 'catalog_release' END) STORED,
  CONSTRAINT fee_type_pkey PRIMARY KEY (entry_id),
  CONSTRAINT fee_type_family CHECK (family = 'fee_type'),
  CONSTRAINT fee_type_text CHECK (
    char_length(definition) BETWEEN 1 AND 2000
    AND char_length(display_name) BETWEEN 1 AND 200
    AND (description IS NULL OR char_length(description) BETWEEN 1 AND 4000)),
  CONSTRAINT fee_type_reg_fk FOREIGN KEY (entry_id, family, code)
    REFERENCES catalog.registered_entry (entry_id, family, code) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT fee_type_release_fk FOREIGN KEY (release_id, release_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.partner_switch (
  entry_id uuid NOT NULL,
  partner_id uuid NOT NULL,
  is_active boolean NOT NULL,
  row_version bigint NOT NULL,
  global_entry_id uuid NULL,
  global_family text NULL,
  private_entry_id uuid NULL,
  private_family text NULL,
  private_owner uuid NULL,
  evidence_entry_id uuid NULL,
  CONSTRAINT partner_switch_pkey PRIMARY KEY (entry_id, partner_id),
  CONSTRAINT partner_switch_version CHECK (row_version >= 1),
  CONSTRAINT partner_switch_path CHECK (
    (global_entry_id IS NOT NULL AND global_family IS NOT NULL AND global_entry_id = entry_id
      AND private_entry_id IS NULL AND private_family IS NULL AND private_owner IS NULL AND evidence_entry_id IS NULL)
    OR (private_entry_id IS NOT NULL AND private_family IS NOT NULL AND private_owner IS NOT NULL
      AND private_entry_id = entry_id AND private_owner = partner_id
      AND global_entry_id IS NULL AND global_family IS NULL AND evidence_entry_id IS NULL)
    OR (evidence_entry_id IS NOT NULL AND evidence_entry_id = entry_id
      AND global_entry_id IS NULL AND global_family IS NULL
      AND private_entry_id IS NULL AND private_family IS NULL AND private_owner IS NULL)),
  CONSTRAINT partner_switch_global_fk FOREIGN KEY (global_entry_id, global_family)
    REFERENCES catalog.ref_global (entry_id, family) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT partner_switch_private_fk FOREIGN KEY (private_entry_id, private_family, private_owner)
    REFERENCES catalog.ref_private (entry_id, family, owner_partner_id) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.location_switch (
  entry_id uuid NOT NULL,
  location_id uuid NOT NULL,
  is_active boolean NOT NULL,
  row_version bigint NOT NULL,
  CONSTRAINT location_switch_pkey PRIMARY KEY (entry_id, location_id),
  CONSTRAINT location_switch_version CHECK (row_version >= 1),
  CONSTRAINT location_switch_entry_fk FOREIGN KEY (entry_id)
    REFERENCES catalog.registered_entry (entry_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
  CONSTRAINT location_switch_location_fk FOREIGN KEY (location_id)
    REFERENCES auth.location (location_id) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.history_pending (
  entry_id uuid NOT NULL,
  family text NOT NULL,
  registry_after jsonb NOT NULL,
  subtype_after jsonb NULL,
  actor_user_id uuid NOT NULL,
  actor_admin_type text NOT NULL,
  actor_partner_id uuid NULL,
  reason text NULL,
  correlation_id text NOT NULL,
  at timestamptz NOT NULL,
  CONSTRAINT history_pending_pkey PRIMARY KEY (entry_id),
  CONSTRAINT history_pending_actor CHECK (actor_admin_type IN ('super', 'partner')),
  CONSTRAINT history_pending_entry_fk FOREIGN KEY (entry_id)
    REFERENCES catalog.registered_entry (entry_id) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE catalog.entry_change (
  change_id uuid NOT NULL,
  entry_id uuid NOT NULL,
  scope_level text NOT NULL,
  target_partner_id uuid NULL,
  target_location_id uuid NULL,
  row_version bigint NOT NULL,
  change_kind text NOT NULL,
  actor_user_id uuid NOT NULL,
  actor_admin_type text NOT NULL,
  actor_partner_id uuid NULL,
  reason text NULL,
  source text NOT NULL,
  at timestamptz NOT NULL,
  before jsonb NULL,
  after jsonb NOT NULL,
  CONSTRAINT entry_change_pkey PRIMARY KEY (change_id),
  CONSTRAINT entry_change_scope CHECK (
    (scope_level = 'entry' AND target_partner_id IS NULL AND target_location_id IS NULL)
    OR (scope_level = 'partner' AND target_partner_id IS NOT NULL AND target_location_id IS NULL)
    OR (scope_level = 'location' AND target_partner_id IS NOT NULL AND target_location_id IS NOT NULL)),
  CONSTRAINT entry_change_kind CHECK (change_kind IN ('created', 'updated', 'activated', 'deactivated')),
  CONSTRAINT entry_change_actor CHECK (
    actor_admin_type IN ('super', 'partner')
    AND ((actor_admin_type = 'super' AND actor_partner_id IS NULL) OR (actor_admin_type = 'partner' AND actor_partner_id IS NOT NULL))),
  CONSTRAINT entry_change_version CHECK (row_version >= 1),
  CONSTRAINT entry_change_reason CHECK (reason IS NULL OR (reason = btrim(reason) AND char_length(reason) BETWEEN 1 AND 500)),
  CONSTRAINT entry_change_before CHECK ((change_kind = 'created') = (before IS NULL)),
  CONSTRAINT entry_change_entry_fk FOREIGN KEY (entry_id)
    REFERENCES catalog.registered_entry (entry_id) ON DELETE RESTRICT ON UPDATE RESTRICT
);
CREATE UNIQUE INDEX entry_change_version_uq ON catalog.entry_change
  (entry_id, scope_level, target_partner_id, target_location_id, row_version) NULLS NOT DISTINCT;

CREATE TABLE catalog.edit_coord (
  entry_id uuid NOT NULL,
  txn_id bigint NOT NULL,
  expected_version bigint NOT NULL,
  before jsonb NOT NULL,
  patch jsonb NOT NULL,
  CONSTRAINT edit_coord_pkey PRIMARY KEY (entry_id, txn_id),
  CONSTRAINT edit_coord_version CHECK (expected_version >= 1),
  CONSTRAINT edit_coord_entry_fk FOREIGN KEY (entry_id)
    REFERENCES catalog.registered_entry (entry_id) ON DELETE RESTRICT ON UPDATE RESTRICT
);

-- Function-entry marker. Inserted only by apply_entry_create and apply_claim_edit.
CREATE TABLE catalog.tx_marker (
  txn_id xid8 NOT NULL,
  entry_id uuid NOT NULL,
  action_kind text NOT NULL,
  sealed boolean NOT NULL,
  CONSTRAINT tx_marker_pkey PRIMARY KEY (txn_id),
  CONSTRAINT tx_marker_kind CHECK (action_kind IN ('create', 'claim-edit'))
);
