-- REQ-064 M-B. A SELECT sees the partner the actor holds: global, that partner's
-- Partner Admin assignment, or a location assignment at that partner.
-- A location id or partner id in a session setting does not open every partner.
-- The write is DEC-111, project-plan L163 (Mohanraj 2026-11-05 6:51 AM: "No,
-- Partner Admin can change any partner"). Do not narrow save_partner_bounds.
-- PQ-240 is open: whether a Partner Admin may SEE another partner's setting,
-- and whether that partner's admins are told. read_partner_bounds and the
-- prb_select policy stay as they are while PQ-240(a) is open. The write path
-- reads through auth.read_partner_bounds, so a Partner Admin can still update
-- a row the SELECT policy hides.

CREATE OR REPLACE FUNCTION auth.read_partner_bounds(p_partner uuid)
  RETURNS TABLE (
    reason_min_length integer,
    reason_max_length integer,
    updated_by uuid,
    updated_at timestamptz,
    row_version bigint)
  LANGUAGE plpgsql
  VOLATILE
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
BEGIN
  -- PQ-240 is open: whether a Partner Admin may SEE another partner's setting,
  -- and whether that partner's admins are told, is not decided here. This
  -- write-path read stays any-admin. prb_select is the SELECT policy and is unchanged.
  IF NOT auth.actor_is_any_admin() THEN
    RETURN;
  END IF;
  RETURN QUERY
    SELECT b.reason_min_length, b.reason_max_length, b.updated_by, b.updated_at, b.row_version
      FROM auth.partner_reason_bounds b
     WHERE b.partner_id = p_partner
     FOR UPDATE;
END
$fn$;
REVOKE ALL ON FUNCTION auth.read_partner_bounds(uuid) FROM PUBLIC;

CREATE OR REPLACE FUNCTION auth.save_partner_bounds(
  p_partner uuid, p_expected bigint, p_min integer, p_max integer, p_by uuid, p_at timestamptz)
  RETURNS bigint
  LANGUAGE plpgsql
  VOLATILE
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
DECLARE
  v bigint;
BEGIN
  -- DEC-111, project-plan L163 (Mohanraj 2026-11-05 6:51 AM: "No, Partner Admin
  -- can change any partner"). Do not restrict this function to the actor's
  -- partner. A location-only user and a principal with no assignment are not
  -- actor_is_any_admin(), so they still cannot write. PQ-240 does not decide the write.
  IF NOT auth.actor_is_any_admin() THEN
    RAISE EXCEPTION 'partner_reason_bounds' USING ERRCODE = '42501';
  END IF;
  IF p_expected = 0 THEN
    INSERT INTO auth.partner_reason_bounds
      (partner_id, reason_min_length, reason_max_length, updated_by, updated_at, row_version)
    VALUES (p_partner, p_min, p_max, p_by, p_at, 1);
    RETURN 1;
  END IF;
  UPDATE auth.partner_reason_bounds
     SET reason_min_length = p_min, reason_max_length = p_max, updated_by = p_by, updated_at = p_at,
         row_version = row_version + 1
   WHERE partner_id = p_partner AND row_version = p_expected
   RETURNING row_version INTO v;
  RETURN v;
END
$fn$;
REVOKE ALL ON FUNCTION auth.save_partner_bounds(uuid, bigint, integer, integer, uuid, timestamptz) FROM PUBLIC;

DO $pol$
DECLARE
  a text := current_database() || '_admin_api';
  see text := $s$(auth.actor_is_global() OR auth.actor_can_partner(partner_id) OR auth.actor_location_sees_partner(partner_id))$s$;
BEGIN
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.read_partner_bounds(uuid) TO %I', a);
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.save_partner_bounds(uuid, bigint, integer, integer, uuid, timestamptz) TO %I', a);
  EXECUTE 'DROP POLICY IF EXISTS prb_admin ON auth.partner_reason_bounds';
  EXECUTE 'DROP POLICY IF EXISTS prb_select ON auth.partner_reason_bounds';
  EXECUTE 'DROP POLICY IF EXISTS prb_insert ON auth.partner_reason_bounds';
  EXECUTE 'DROP POLICY IF EXISTS prb_update ON auth.partner_reason_bounds';
  EXECUTE format('CREATE POLICY prb_select ON auth.partner_reason_bounds FOR SELECT TO %I USING %s', a, see);
  EXECUTE format('CREATE POLICY prb_insert ON auth.partner_reason_bounds FOR INSERT TO %I WITH CHECK (auth.actor_is_any_admin())', a);
  EXECUTE format('CREATE POLICY prb_update ON auth.partner_reason_bounds FOR UPDATE TO %I USING (auth.actor_is_any_admin()) WITH CHECK (auth.actor_is_any_admin())', a);
END
$pol$;
