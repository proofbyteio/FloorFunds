-- REQ-064 assignment store, resolver, and the last-active global Super Admin guard.
-- SQLSTATE P0001, MESSAGE last_active_super_admin: the statement would remove the last
-- effective global Super Admin. Effective means role_id super_admin, scope_kind global,
-- both scope ids null, status active, revoked_at null, and the app_user is active and
-- not locked. The guard takes pg_advisory_xact_lock(814064204) inside the trigger and
-- then counts the other effective Super Admins, so two concurrent removals cannot both commit.
-- Covered statements: revoked_at, assignment status, app_user status, app_user lock,
-- role_id or scope_kind leaving global super_admin, DELETE, and TRUNCATE.
-- TRUNCATE of auth.app_user without CASCADE is already refused by foreign keys.
-- There is no revoke or assign API here.

CREATE TABLE auth.admin_assignment (
  assignment_id uuid        NOT NULL,
  user_id       uuid        NOT NULL,
  role_id       text        NOT NULL,
  scope_kind    text        NOT NULL,
  partner_id    uuid        NULL,
  location_id   uuid        NULL,
  status        text        NOT NULL,
  version       bigint      NOT NULL DEFAULT 1,
  granted_by    uuid        NOT NULL,
  granted_at    timestamptz NOT NULL,
  revoked_at    timestamptz NULL,
  CONSTRAINT admin_assignment_pkey PRIMARY KEY (assignment_id),
  CONSTRAINT admin_assignment_user_fk FOREIGN KEY (user_id) REFERENCES auth.app_user (user_id),
  CONSTRAINT admin_assignment_location_fk FOREIGN KEY (location_id) REFERENCES auth.location (location_id),
  CONSTRAINT admin_assignment_granted_by_fk FOREIGN KEY (granted_by) REFERENCES auth.app_user (user_id),
  CONSTRAINT admin_assignment_role CHECK (role_id IN (
    'super_admin', 'partner_admin', 'client_admin', 'supervisor', 'cashier', 'auditor', 'accounting', 'support')),
  CONSTRAINT admin_assignment_scope_kind CHECK (scope_kind IN ('global', 'partner', 'location')),
  CONSTRAINT admin_assignment_status CHECK (status IN ('active', 'disabled', 'revoked')),
  CONSTRAINT admin_assignment_version CHECK (version >= 1),
  CONSTRAINT admin_assignment_super CHECK (
    role_id <> 'super_admin' OR (scope_kind = 'global' AND partner_id IS NULL AND location_id IS NULL)),
  CONSTRAINT admin_assignment_partner_admin CHECK (
    role_id <> 'partner_admin' OR (scope_kind = 'partner' AND partner_id IS NOT NULL AND location_id IS NULL)),
  CONSTRAINT admin_assignment_scope_shape CHECK (
    (scope_kind = 'global' AND partner_id IS NULL AND location_id IS NULL)
    OR (scope_kind = 'partner' AND partner_id IS NOT NULL AND location_id IS NULL)
    OR (scope_kind = 'location' AND location_id IS NOT NULL AND partner_id IS NULL))
);

CREATE INDEX admin_assignment_user_ix ON auth.admin_assignment (user_id);

ALTER TABLE auth.admin_assignment ENABLE ROW LEVEL SECURITY;
ALTER TABLE auth.admin_assignment FORCE ROW LEVEL SECURITY;
CREATE POLICY admin_assignment_owner ON auth.admin_assignment FOR ALL TO CURRENT_USER USING (true) WITH CHECK (true);

CREATE SEQUENCE auth.resolver_read_seq AS bigint;

-- P0001 / last_active_super_admin. See the file header.
CREATE FUNCTION auth._last_sa_guard() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
DECLARE
  old_effective boolean := false;
  new_effective boolean := false;
  remaining bigint;
BEGIN
  IF TG_TABLE_NAME = 'admin_assignment' THEN
    old_effective := OLD.role_id = 'super_admin'
      AND OLD.scope_kind = 'global'
      AND OLD.partner_id IS NULL
      AND OLD.location_id IS NULL
      AND OLD.status = 'active'
      AND OLD.revoked_at IS NULL
      AND EXISTS (
        SELECT 1 FROM auth.app_user u
         WHERE u.user_id = OLD.user_id
           AND u.status = 'active'
           AND (u.locked_until IS NULL OR u.locked_until <= now()));
    IF TG_OP = 'UPDATE' THEN
      new_effective := NEW.role_id = 'super_admin'
        AND NEW.scope_kind = 'global'
        AND NEW.partner_id IS NULL
        AND NEW.location_id IS NULL
        AND NEW.status = 'active'
        AND NEW.revoked_at IS NULL
        AND EXISTS (
          SELECT 1 FROM auth.app_user u
           WHERE u.user_id = NEW.user_id
             AND u.status = 'active'
             AND (u.locked_until IS NULL OR u.locked_until <= now()));
    END IF;
    IF NOT old_effective OR new_effective THEN
      RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
    END IF;
  ELSE
    old_effective := OLD.status = 'active'
      AND (OLD.locked_until IS NULL OR OLD.locked_until <= now())
      AND EXISTS (
        SELECT 1 FROM auth.admin_assignment a
         WHERE a.user_id = OLD.user_id
           AND a.role_id = 'super_admin'
           AND a.scope_kind = 'global'
           AND a.partner_id IS NULL
           AND a.location_id IS NULL
           AND a.status = 'active'
           AND a.revoked_at IS NULL);
    IF TG_OP = 'UPDATE' THEN
      new_effective := NEW.status = 'active'
        AND (NEW.locked_until IS NULL OR NEW.locked_until <= now())
        AND EXISTS (
          SELECT 1 FROM auth.admin_assignment a
           WHERE a.user_id = NEW.user_id
             AND a.role_id = 'super_admin'
             AND a.scope_kind = 'global'
             AND a.partner_id IS NULL
             AND a.location_id IS NULL
             AND a.status = 'active'
             AND a.revoked_at IS NULL);
    END IF;
    IF NOT old_effective OR (TG_OP = 'UPDATE' AND new_effective) THEN
      RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
    END IF;
  END IF;

  -- The lock is held until this transaction ends. The count is its own statement, so in
  -- READ COMMITTED it sees a removal the other session already committed.
  -- The row this statement is changing is excluded: it has not been updated yet.
  PERFORM pg_advisory_xact_lock(814064204);
  IF TG_TABLE_NAME = 'admin_assignment' THEN
    SELECT count(DISTINCT a.user_id) INTO remaining
      FROM auth.admin_assignment a
      JOIN auth.app_user u ON u.user_id = a.user_id
     WHERE a.assignment_id IS DISTINCT FROM OLD.assignment_id
       AND a.role_id = 'super_admin'
       AND a.scope_kind = 'global'
       AND a.partner_id IS NULL
       AND a.location_id IS NULL
       AND a.status = 'active'
       AND a.revoked_at IS NULL
       AND u.status = 'active'
       AND (u.locked_until IS NULL OR u.locked_until <= now());
  ELSE
    SELECT count(DISTINCT a.user_id) INTO remaining
      FROM auth.admin_assignment a
      JOIN auth.app_user u ON u.user_id = a.user_id
     WHERE a.user_id IS DISTINCT FROM OLD.user_id
       AND a.role_id = 'super_admin'
       AND a.scope_kind = 'global'
       AND a.partner_id IS NULL
       AND a.location_id IS NULL
       AND a.status = 'active'
       AND a.revoked_at IS NULL
       AND u.status = 'active'
       AND (u.locked_until IS NULL OR u.locked_until <= now());
  END IF;
  IF remaining < 1 THEN
    RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'last_active_super_admin';
  END IF;
  RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END
$fn$;

CREATE TRIGGER admin_assignment_last_sa
  BEFORE UPDATE OR DELETE ON auth.admin_assignment
  FOR EACH ROW EXECUTE FUNCTION auth._last_sa_guard();

CREATE TRIGGER app_user_last_sa
  BEFORE UPDATE OR DELETE ON auth.app_user
  FOR EACH ROW EXECUTE FUNCTION auth._last_sa_guard();

-- TRUNCATE does not fire the row trigger. It would remove every active global Super Admin.
CREATE FUNCTION auth._last_sa_truncate_guard() RETURNS trigger
  LANGUAGE plpgsql
  SECURITY DEFINER
  SET search_path = pg_catalog, auth
AS $fn$
DECLARE
  remaining bigint;
BEGIN
  PERFORM pg_advisory_xact_lock(814064204);
  SELECT count(DISTINCT a.user_id) INTO remaining
    FROM auth.admin_assignment a
    JOIN auth.app_user u ON u.user_id = a.user_id
   WHERE a.role_id = 'super_admin'
     AND a.scope_kind = 'global'
     AND a.partner_id IS NULL
     AND a.location_id IS NULL
     AND a.status = 'active'
     AND a.revoked_at IS NULL
     AND u.status = 'active'
     AND (u.locked_until IS NULL OR u.locked_until <= now());
  IF remaining >= 1 THEN
    RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'last_active_super_admin';
  END IF;
  RETURN NULL;
END
$fn$;

CREATE TRIGGER admin_assignment_last_sa_truncate
  BEFORE TRUNCATE ON auth.admin_assignment
  FOR EACH STATEMENT EXECUTE FUNCTION auth._last_sa_truncate_guard();

CREATE TRIGGER app_user_last_sa_truncate
  BEFORE TRUNCATE ON auth.app_user
  FOR EACH STATEMENT EXECUTE FUNCTION auth._last_sa_truncate_guard();

REVOKE ALL ON FUNCTION auth._last_sa_guard() FROM PUBLIC;
REVOKE ALL ON FUNCTION auth._last_sa_truncate_guard() FROM PUBLIC;
REVOKE ALL ON SEQUENCE auth.resolver_read_seq FROM PUBLIC;

-- Inputs are the verified issuer and subject only. Exactly one active, unlocked user
-- or the outcome is deny. A stand-in subject that is the app user id matches user_id
-- because a stand-in issuer cannot be stored in external_login. An Entra subject
-- matches external_login. The same user from both paths is one user. Two users is deny.
-- nextval is the proof the store was read. It is not rolled back.
CREATE FUNCTION auth.resolve_grant(p_issuer text, p_subject uuid)
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
       WHERE u.user_id = p_subject
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
         coalesce(array_agg(DISTINCT a.partner_id) FILTER (WHERE a.scope_kind = 'partner' AND a.partner_id IS NOT NULL), ARRAY[]::uuid[]),
         coalesce(array_agg(DISTINCT a.location_id) FILTER (WHERE a.scope_kind = 'location' AND a.location_id IS NOT NULL), ARRAY[]::uuid[]),
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
     WHERE a.user_id = v_user AND a.status = 'active' AND a.revoked_at IS NULL AND a.role_id = 'partner_admin'
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

REVOKE ALL ON FUNCTION auth.resolve_grant(text, uuid) FROM PUBLIC;

-- Existing partners for the location-create picker. The caller sees a partner only
-- through the resolved grant: global Super Admin, or a partner id on that grant.
-- PQ-284: this list is the reversible default. The shell does not accept a typed partner id.
CREATE FUNCTION auth.list_existing_partners()
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
   WHERE current_setting('app.resolved_global_admin', true) = 'on'
      OR p.partner_id::text = ANY (string_to_array(coalesce(current_setting('app.resolved_partner_ids', true), ''), ','))
   ORDER BY 1;
$partners$;

REVOKE ALL ON FUNCTION auth.list_existing_partners() FROM PUBLIC;

DO $req064$
DECLARE
  a text := current_database() || '_admin_api';
  scope text := $p$(current_setting('app.resolved_global_admin', true) = 'on' OR partner_id::text = ANY (string_to_array(coalesce(current_setting('app.resolved_partner_ids', true), ''), ',')) OR location_id::text = ANY (string_to_array(coalesce(current_setting('app.resolved_location_ids', true), ''), ',')))$p$;
  any_admin text := $p$(current_setting('app.resolved_global_admin', true) = 'on' OR EXISTS (SELECT 1 FROM unnest(string_to_array(coalesce(current_setting('app.resolved_partner_ids', true), ''), ',')) p(id) WHERE p.id ~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') OR EXISTS (SELECT 1 FROM unnest(string_to_array(coalesce(current_setting('app.resolved_location_ids', true), ''), ',')) loc(id) WHERE loc.id ~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'))$p$;
BEGIN
  EXECUTE format('REVOKE ALL ON auth.admin_assignment FROM PUBLIC, %I', a);
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.resolve_grant(text, uuid) TO %I', a);
  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.list_existing_partners() TO %I', a);
  EXECUTE 'DROP POLICY IF EXISTS location_admin ON auth.location';
  EXECUTE format('CREATE POLICY location_admin ON auth.location FOR ALL TO %I USING %s WITH CHECK %s', a, scope, scope);
  EXECUTE 'DROP POLICY IF EXISTS prb_admin ON auth.partner_reason_bounds';
  EXECUTE format('CREATE POLICY prb_admin ON auth.partner_reason_bounds FOR ALL TO %I USING %s WITH CHECK %s', a, any_admin, any_admin);
END
$req064$;
