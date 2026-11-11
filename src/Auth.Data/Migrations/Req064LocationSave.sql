-- SA L2. admin_api no longer holds UPDATE on auth.location_login_config.
-- The write goes through a definer function that checks the actor can see the
-- location. The column list and the keep-copy CASE match the previous UPDATE.
CREATE OR REPLACE FUNCTION auth.save_location_login_config(
  p_location uuid,
  p_expected bigint,
  p_provider_kind text,
  p_display_name text,
  p_tenants uuid[],
  p_oidc_authority text,
  p_oidc_client_id text,
  p_keep_copy boolean,
  p_secret_ref text,
  p_allow_db_fallback boolean,
  p_mfa_required boolean,
  p_updated_by uuid,
  p_updated_at timestamptz
) RETURNS integer
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
DECLARE
  n integer;
BEGIN
  IF auth._request_user() IS NULL OR NOT EXISTS (
    SELECT 1 FROM auth.location l
     WHERE l.location_id = p_location
       AND auth.actor_can_location(l.partner_id, l.location_id)
  ) THEN
    RAISE EXCEPTION 'location_login_config' USING ERRCODE = '42501';
  END IF;
  UPDATE auth.location_login_config SET
    provider_kind = p_provider_kind,
    display_name = p_display_name,
    entra_allowed_tenant_ids = p_tenants,
    oidc_authority = CASE WHEN p_keep_copy THEN oidc_authority ELSE p_oidc_authority END,
    oidc_client_id = CASE WHEN p_keep_copy THEN oidc_client_id ELSE p_oidc_client_id END,
    secret_ref = CASE WHEN p_keep_copy THEN secret_ref ELSE p_secret_ref END,
    allow_db_fallback = p_allow_db_fallback,
    mfa_required = p_mfa_required,
    updated_by = p_updated_by,
    updated_at = p_updated_at,
    row_version = row_version + 1
  WHERE location_id = p_location AND row_version = p_expected;
  GET DIAGNOSTICS n = ROW_COUNT;
  RETURN n;
END
$fn$;

REVOKE ALL ON FUNCTION auth.save_location_login_config(uuid, bigint, text, text, uuid[], text, text, boolean, text, boolean, boolean, uuid, timestamptz) FROM PUBLIC;

DO $l2$
DECLARE
  a text := current_database() || '_admin_api';
  r text := current_database() || '_auth_definer';
BEGIN
  EXECUTE format(
    'REVOKE UPDATE (provider_kind, display_name, entra_allowed_tenant_ids, oidc_authority, oidc_client_id, secret_ref, allow_db_fallback, mfa_required, updated_by, updated_at, row_version) ON auth.location_login_config FROM %I',
    a);
  EXECUTE format(
    'GRANT EXECUTE ON FUNCTION auth.save_location_login_config(uuid, bigint, text, text, uuid[], text, text, boolean, text, boolean, boolean, uuid, timestamptz) TO %I',
    a);
  IF EXISTS (
    SELECT 1 FROM pg_roles
     WHERE rolname = r AND NOT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls
  ) THEN
    EXECUTE format('GRANT UPDATE ON auth.location_login_config TO %I', r);
    EXECUTE 'DROP POLICY IF EXISTS llc_definer ON auth.location_login_config';
    EXECUTE format(
      'CREATE POLICY llc_definer ON auth.location_login_config FOR UPDATE TO %I USING (true) WITH CHECK (true)',
      r);
  END IF;
END
$l2$;
