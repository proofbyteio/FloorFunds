-- REQ-064 H-B. Row security does not trust app.resolved_* or app.admin_type.
-- Those settings are writable by admin_api. Access is derived from the active
-- assignment rows of the request principal (app.request_iss, app.request_sub).
-- The functions are SECURITY DEFINER, owned by the database owner (not admin_api),
-- with a pinned search_path. admin_api is not a member of the owner and cannot SET ROLE to it.

CREATE OR REPLACE FUNCTION auth._request_user() RETURNS uuid
  LANGUAGE sql
  STABLE
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
  SELECT CASE WHEN count(DISTINCT c.user_id) = 1 THEN min(c.user_id::text)::uuid ELSE NULL::uuid END
    FROM (
      SELECT u.user_id
        FROM auth.app_user u
        JOIN auth.user_issuer i ON i.user_id = u.user_id
       WHERE i.issuer = current_setting('app.request_iss', true)
         AND u.user_id::text = current_setting('app.request_sub', true)
         AND u.status = 'active'
         AND (u.locked_until IS NULL OR u.locked_until <= now())
      UNION
      SELECT u.user_id
        FROM auth.external_login e
        JOIN auth.app_user u ON u.user_id = e.user_id
       WHERE e.issuer = current_setting('app.request_iss', true)
         AND e.subject::text = current_setting('app.request_sub', true)
         AND u.status = 'active'
         AND (u.locked_until IS NULL OR u.locked_until <= now())
    ) c
$fn$;

CREATE OR REPLACE FUNCTION auth.actor_is_global() RETURNS boolean
  LANGUAGE sql
  STABLE
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
  SELECT EXISTS (
    SELECT 1 FROM auth.admin_assignment a
     WHERE a.user_id = auth._request_user()
       AND a.role_id = 'super_admin' AND a.scope_kind = 'global'
       AND a.status = 'active' AND a.revoked_at IS NULL)
$fn$;

CREATE OR REPLACE FUNCTION auth.actor_is_any_admin() RETURNS boolean
  LANGUAGE sql
  STABLE
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
  SELECT auth.actor_is_global() OR EXISTS (
    SELECT 1 FROM auth.admin_assignment a
     WHERE a.user_id = auth._request_user()
       AND a.status = 'active' AND a.revoked_at IS NULL
       AND a.role_id = 'partner_admin' AND a.scope_kind = 'partner' AND a.partner_id IS NOT NULL)
$fn$;

CREATE OR REPLACE FUNCTION auth.actor_has_any_location() RETURNS boolean
  LANGUAGE sql
  STABLE
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
  SELECT EXISTS (
    SELECT 1 FROM auth.admin_assignment a
     WHERE a.user_id = auth._request_user()
       AND a.scope_kind = 'location' AND a.location_id IS NOT NULL
       AND a.status = 'active' AND a.revoked_at IS NULL)
$fn$;

CREATE OR REPLACE FUNCTION auth.actor_can_partner(p_partner uuid) RETURNS boolean
  LANGUAGE sql
  STABLE
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
  SELECT auth.actor_is_global() OR EXISTS (
    SELECT 1 FROM auth.admin_assignment a
     WHERE a.user_id = auth._request_user()
       AND a.role_id = 'partner_admin' AND a.scope_kind = 'partner'
       AND a.partner_id = p_partner
       AND a.status = 'active' AND a.revoked_at IS NULL)
$fn$;

CREATE OR REPLACE FUNCTION auth.actor_location_sees_partner(p_partner uuid) RETURNS boolean
  LANGUAGE sql
  STABLE
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
  SELECT EXISTS (
    SELECT 1 FROM auth.admin_assignment a
    JOIN auth.location l ON l.location_id = a.location_id
     WHERE a.user_id = auth._request_user()
       AND a.scope_kind = 'location'
       AND a.status = 'active' AND a.revoked_at IS NULL
       AND l.partner_id = p_partner)
$fn$;

CREATE OR REPLACE FUNCTION auth.actor_has_location(p_location uuid) RETURNS boolean
  LANGUAGE sql
  STABLE
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
  SELECT EXISTS (
    SELECT 1 FROM auth.admin_assignment a
     WHERE a.user_id = auth._request_user()
       AND a.scope_kind = 'location' AND a.location_id = p_location
       AND a.status = 'active' AND a.revoked_at IS NULL)
$fn$;

CREATE OR REPLACE FUNCTION auth.actor_can_location(p_partner uuid, p_location uuid) RETURNS boolean
  LANGUAGE sql
  STABLE
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
  SELECT auth.actor_is_global()
      OR auth.actor_can_partner(p_partner)
      OR auth.actor_has_location(p_location)
$fn$;

REVOKE ALL ON FUNCTION auth._request_user() FROM PUBLIC;
REVOKE ALL ON FUNCTION auth.actor_is_global() FROM PUBLIC;
REVOKE ALL ON FUNCTION auth.actor_is_any_admin() FROM PUBLIC;
REVOKE ALL ON FUNCTION auth.actor_has_any_location() FROM PUBLIC;
REVOKE ALL ON FUNCTION auth.actor_can_partner(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth.actor_location_sees_partner(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth.actor_has_location(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION auth.actor_can_location(uuid, uuid) FROM PUBLIC;

CREATE OR REPLACE FUNCTION auth.actor_sees_change(p_entry uuid, p_scope text, p_partner uuid, p_location uuid)
  RETURNS boolean
  LANGUAGE sql
  STABLE
  SECURITY DEFINER
  SET search_path = pg_catalog, auth, catalog
AS $fn$
  SELECT auth.actor_is_global()
      OR EXISTS (
        SELECT 1
          FROM auth.admin_assignment asn
          JOIN catalog.registered_entry e ON e.entry_id = p_entry
         WHERE asn.user_id = auth._request_user()
           AND asn.status = 'active' AND asn.revoked_at IS NULL
           AND asn.role_id = 'partner_admin' AND asn.scope_kind = 'partner'
           AND (e.owner_partner_id IS NULL OR e.owner_partner_id = asn.partner_id)
           AND (
             (p_scope = 'entry' AND p_partner IS NULL AND p_location IS NULL)
             OR (p_scope = 'partner' AND p_partner = asn.partner_id AND p_location IS NULL)
             OR (p_scope = 'location' AND p_partner = asn.partner_id AND EXISTS (
                  SELECT 1 FROM auth.location l
                   WHERE l.location_id = p_location AND l.partner_id = asn.partner_id))
           )
      )
      OR EXISTS (
        SELECT 1
          FROM auth.admin_assignment asn
          JOIN auth.location mine ON mine.location_id = asn.location_id
          JOIN catalog.registered_entry e ON e.entry_id = p_entry
         WHERE asn.user_id = auth._request_user()
           AND asn.scope_kind = 'location' AND asn.status = 'active' AND asn.revoked_at IS NULL
           AND (e.owner_partner_id IS NULL OR e.owner_partner_id = mine.partner_id)
           AND (
             (p_scope = 'entry' AND p_partner IS NULL AND p_location IS NULL)
             OR (p_scope = 'partner' AND p_partner = mine.partner_id AND p_location IS NULL)
             OR (p_scope = 'location' AND p_location = mine.location_id AND p_partner = mine.partner_id)
           )
      )
$fn$;

REVOKE ALL ON FUNCTION auth.actor_sees_change(uuid, text, uuid, uuid) FROM PUBLIC;

DO $grant$
DECLARE
  a text := current_database() || '_admin_api';
BEGIN
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.actor_is_global() TO %I', a);
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.actor_is_any_admin() TO %I', a);
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.actor_has_any_location() TO %I', a);
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.actor_can_partner(uuid) TO %I', a);
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.actor_location_sees_partner(uuid) TO %I', a);
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.actor_has_location(uuid) TO %I', a);
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.actor_can_location(uuid, uuid) TO %I', a);
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.actor_sees_change(uuid, text, uuid, uuid) TO %I', a);
END
$grant$;

CREATE OR REPLACE FUNCTION auth.list_existing_partners()
  RETURNS TABLE (partner_id uuid)
  LANGUAGE sql
  STABLE
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $partners$
  SELECT DISTINCT p.partner_id
    FROM (
      SELECT u.partner_id FROM auth.app_user u WHERE u.partner_id IS NOT NULL
      UNION
      SELECT l.partner_id FROM auth.location l
      UNION
      SELECT a.partner_id FROM auth.admin_assignment a WHERE a.partner_id IS NOT NULL
    ) p
   WHERE auth.actor_is_global() OR auth.actor_can_partner(p.partner_id)
   ORDER BY 1
$partners$;

DO $pol$
DECLARE
  a text := current_database() || '_admin_api';
  see text := $s$(auth.actor_is_global() OR (%s IS NULL AND auth.actor_is_any_admin()) OR auth.actor_can_partner(%s) OR auth.actor_location_sees_partner(%s))$s$;
  t text;
  owned text[] := ARRAY[
    'manufacturer','device_model','processor','reason_code','id_document_type','fee_type'];
  global_only text[] := ARRAY[
    'catalog_release','transaction_type','channel','device_category','hardware_capability_kind',
    'card_network','card_funding_class','payment_rail','check_instrument_kind','check_drawer_class',
    'check_purpose','transaction_status','currency','ref_global','global_device_model'];
BEGIN
  EXECUTE 'DROP POLICY IF EXISTS location_admin ON auth.location';
  EXECUTE format(
    'CREATE POLICY location_admin ON auth.location FOR ALL TO %I USING (auth.actor_can_location(partner_id, location_id)) WITH CHECK (auth.actor_can_location(partner_id, location_id))',
    a);
  -- Reads: a Partner Admin still sees every partner's bounds (DEC-111). A location
  -- assignment sees only the partner that owns that location. It cannot write bounds.
  -- M-B tightens the Partner Admin read to the partner that owns the scope.
  EXECUTE 'DROP POLICY IF EXISTS prb_admin ON auth.partner_reason_bounds';
  EXECUTE format(
    'CREATE POLICY prb_admin ON auth.partner_reason_bounds FOR ALL TO %I USING (auth.actor_is_any_admin() OR auth.actor_location_sees_partner(partner_id)) WITH CHECK (auth.actor_is_any_admin())',
    a);

  EXECUTE 'DROP POLICY IF EXISTS registered_entry_read ON catalog.registered_entry';
  EXECUTE format('CREATE POLICY registered_entry_read ON catalog.registered_entry FOR SELECT TO %I USING (%s)',
    a, format(see, 'owner_partner_id', 'owner_partner_id', 'owner_partner_id'));
  EXECUTE 'DROP POLICY IF EXISTS ref_private_read ON catalog.ref_private';
  EXECUTE format('CREATE POLICY ref_private_read ON catalog.ref_private FOR SELECT TO %I USING (%s)',
    a, format(see, 'owner_partner_id', 'owner_partner_id', 'owner_partner_id'));
  EXECUTE 'DROP POLICY IF EXISTS model_hardware_claim_read ON catalog.model_hardware_claim';
  EXECUTE format('CREATE POLICY model_hardware_claim_read ON catalog.model_hardware_claim FOR SELECT TO %I USING (%s)',
    a, format(see, 'derived_owner', 'derived_owner', 'derived_owner'));
  FOREACH t IN ARRAY owned LOOP
    EXECUTE format('DROP POLICY IF EXISTS %I ON catalog.%I', t || '_read', t);
    EXECUTE format('CREATE POLICY %I ON catalog.%I FOR SELECT TO %I USING (%s)',
      t || '_read', t, a, format(see, 'owner_partner_id', 'owner_partner_id', 'owner_partner_id'));
  END LOOP;
  FOREACH t IN ARRAY global_only LOOP
    EXECUTE format('DROP POLICY IF EXISTS %I ON catalog.%I', t || '_read', t);
    EXECUTE format(
      'CREATE POLICY %I ON catalog.%I FOR SELECT TO %I USING (auth.actor_is_global() OR auth.actor_is_any_admin() OR auth.actor_has_any_location())',
      t || '_read', t, a);
  END LOOP;

  EXECUTE 'DROP POLICY IF EXISTS partner_switch_read ON catalog.partner_switch';
  EXECUTE format($f$
    CREATE POLICY partner_switch_read ON catalog.partner_switch FOR SELECT TO %I USING (
      auth.actor_is_global()
      OR (
        (auth.actor_can_partner(partner_switch.partner_id) OR auth.actor_location_sees_partner(partner_switch.partner_id))
        AND EXISTS (SELECT 1 FROM catalog.registered_entry e WHERE e.entry_id = partner_switch.entry_id)
      )
    )
  $f$, a);
  EXECUTE 'DROP POLICY IF EXISTS location_switch_read ON catalog.location_switch';
  EXECUTE format($f$
    CREATE POLICY location_switch_read ON catalog.location_switch FOR SELECT TO %I USING (
      auth.actor_is_global()
      OR (
        (
          EXISTS (
            SELECT 1 FROM auth.location l
             WHERE l.location_id = location_switch.location_id
               AND (auth.actor_can_partner(l.partner_id) OR auth.actor_location_sees_partner(l.partner_id))
          )
          OR auth.actor_has_location(location_switch.location_id)
        )
        AND EXISTS (SELECT 1 FROM catalog.registered_entry e WHERE e.entry_id = location_switch.entry_id)
      )
    )
  $f$, a);
  EXECUTE 'DROP POLICY IF EXISTS entry_change_read ON catalog.entry_change';
  EXECUTE format(
    'CREATE POLICY entry_change_read ON catalog.entry_change FOR SELECT TO %I USING (auth.actor_sees_change(entry_id, scope_level, target_partner_id, target_location_id))',
    a);
END
$pol$;

-- C-4. The write records the principal the policies just used. The function copies
-- app.request_iss and app.request_sub. It does not take them as arguments, and
-- admin_api cannot insert the row itself. The audit event JSON is unchanged.
CREATE TABLE auth.login_write_principal (
  write_id uuid PRIMARY KEY,
  kind text NOT NULL,
  target_id uuid NOT NULL,
  issuer text NOT NULL,
  subject uuid NOT NULL,
  recorded_at timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT login_write_principal_kind CHECK (kind IN ('location_login_config', 'partner_reason_bounds')),
  CONSTRAINT login_write_principal_issuer_len CHECK (char_length(issuer) BETWEEN 1 AND 500)
);
REVOKE ALL ON TABLE auth.login_write_principal FROM PUBLIC;
DO $rev$
BEGIN
  EXECUTE format('REVOKE ALL ON TABLE auth.login_write_principal FROM %I', current_database() || '_admin_api');
  EXECUTE format('REVOKE ALL ON TABLE auth.login_write_principal FROM %I', current_database() || '_login');
EXCEPTION WHEN undefined_object THEN
  NULL;
END
$rev$;

CREATE OR REPLACE FUNCTION auth.record_login_write(p_kind text, p_target uuid) RETURNS void
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
DECLARE
  v_iss text := current_setting('app.request_iss', true);
  v_sub text := current_setting('app.request_sub', true);
BEGIN
  IF v_iss IS NULL OR char_length(v_iss) < 1 OR v_sub IS NULL
     OR v_sub !~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN
    RAISE EXCEPTION 'login_write_principal' USING ERRCODE = 'P0001';
  END IF;
  INSERT INTO auth.login_write_principal (write_id, kind, target_id, issuer, subject)
  VALUES (gen_random_uuid(), p_kind, p_target, v_iss, v_sub::uuid);
END
$fn$;
REVOKE ALL ON FUNCTION auth.record_login_write(text, uuid) FROM PUBLIC;
DO $grant$
BEGIN
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.record_login_write(text, uuid) TO %I', current_database() || '_admin_api');
END
$grant$;
