-- REQ-064 M-A. Both the user_id branch and the external_login branch require the issuer.
-- auth.user_issuer is not an EF entity. Stand-in issuers do not match the external_login
-- issuer check, so the verified (iss, sub) pair is stored here. admin_api cannot write it.
CREATE TABLE auth.user_issuer (
  user_id uuid PRIMARY KEY REFERENCES auth.app_user (user_id),
  issuer text NOT NULL,
  CONSTRAINT user_issuer_issuer_len CHECK (char_length(issuer) BETWEEN 1 AND 500)
);
REVOKE ALL ON TABLE auth.user_issuer FROM PUBLIC;
DO $rev$
BEGIN
  EXECUTE format('REVOKE ALL ON TABLE auth.user_issuer FROM %I', current_database() || '_admin_api');
  EXECUTE format('REVOKE ALL ON TABLE auth.user_issuer FROM %I', current_database() || '_login');
EXCEPTION WHEN undefined_object THEN
  NULL;
END
$rev$;

CREATE OR REPLACE FUNCTION auth.resolve_grant(p_issuer text, p_subject uuid)
  RETURNS TABLE (
    outcome text,
    user_id uuid,
    role_wire text,
    global_admin boolean,
    partner_ids uuid[],
    location_ids uuid[],
    perm_version bigint)
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
DECLARE
  v_user uuid;
  v_count int;
  v_global boolean;
  v_partners uuid[];
  v_locations uuid[];
  v_version bigint;
  v_role text;
BEGIN
  PERFORM nextval('auth.resolver_read_seq');
  SELECT count(DISTINCT c.user_id), min(c.user_id::text)::uuid
    INTO v_count, v_user
    FROM (
      SELECT u.user_id
        FROM auth.app_user u
        JOIN auth.user_issuer i ON i.user_id = u.user_id
       WHERE i.issuer = p_issuer
         AND u.user_id = p_subject
         AND u.status = 'active'
         AND (u.locked_until IS NULL OR u.locked_until <= now())
      UNION
      SELECT u.user_id
        FROM auth.external_login e
        JOIN auth.app_user u ON u.user_id = e.user_id
       WHERE e.issuer = p_issuer
         AND e.subject = p_subject
         AND u.status = 'active'
         AND (u.locked_until IS NULL OR u.locked_until <= now())
    ) c;
  IF v_count IS DISTINCT FROM 1 THEN
    outcome := 'deny'; user_id := NULL; role_wire := 'none'; global_admin := false;
    partner_ids := ARRAY[]::uuid[]; location_ids := ARRAY[]::uuid[]; perm_version := 0;
    RETURN NEXT;
    RETURN;
  END IF;

  SELECT coalesce(bool_or(a.role_id = 'super_admin' AND a.scope_kind = 'global'), false),
         coalesce(array_agg(DISTINCT a.partner_id) FILTER (
           WHERE a.scope_kind = 'partner' AND a.role_id = 'partner_admin' AND a.partner_id IS NOT NULL
         ), ARRAY[]::uuid[]),
         coalesce(array_agg(DISTINCT a.location_id) FILTER (
           WHERE a.scope_kind = 'location' AND a.location_id IS NOT NULL
         ), ARRAY[]::uuid[]),
         coalesce(max(a.version), 0)
    INTO v_global, v_partners, v_locations, v_version
    FROM auth.admin_assignment a
   WHERE a.user_id = v_user
     AND a.status = 'active'
     AND a.revoked_at IS NULL;

  IF v_version = 0 AND NOT v_global AND v_partners = ARRAY[]::uuid[] AND v_locations = ARRAY[]::uuid[] THEN
    outcome := 'deny'; user_id := v_user; role_wire := 'none'; global_admin := false;
    partner_ids := ARRAY[]::uuid[]; location_ids := ARRAY[]::uuid[]; perm_version := 0;
    RETURN NEXT;
    RETURN;
  END IF;

  IF v_global THEN
    v_role := 'super_admin';
  ELSIF EXISTS (
    SELECT 1 FROM auth.admin_assignment a
     WHERE a.user_id = v_user AND a.status = 'active' AND a.revoked_at IS NULL
       AND a.role_id = 'partner_admin' AND a.scope_kind = 'partner' AND a.partner_id IS NOT NULL
  ) THEN
    v_role := 'partner_admin';
  ELSE
    SELECT a.role_id INTO v_role
      FROM auth.admin_assignment a
     WHERE a.user_id = v_user AND a.status = 'active' AND a.revoked_at IS NULL
     ORDER BY a.role_id
     LIMIT 1;
    v_role := coalesce(v_role, 'none');
  END IF;

  outcome := 'grant';
  user_id := v_user;
  role_wire := v_role;
  global_admin := v_global;
  partner_ids := v_partners;
  location_ids := v_locations;
  perm_version := v_version;
  RETURN NEXT;
END
$fn$;
