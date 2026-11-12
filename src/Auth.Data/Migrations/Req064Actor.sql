-- The catalog actor is the bound session principal. Resolved GUCs are not read.
-- correlation_id and reason are the write label, not the principal.

DROP FUNCTION IF EXISTS catalog._actor();

CREATE OR REPLACE FUNCTION catalog._actor(
  OUT admin_type text,
  OUT partner_id uuid,
  OUT actor_user_id uuid,
  OUT correlation_id text,
  OUT reason text,
  OUT resolved_global boolean,
  OUT resolved_partner_ids uuid[])
  LANGUAGE plpgsql
  SET search_path = pg_catalog
AS $fn$
DECLARE
  v_outcome text;
  v_user uuid;
  v_global boolean;
  v_partners uuid[];
BEGIN
  SELECT g.outcome, g.user_id, g.global_admin, g.partner_ids
    INTO v_outcome, v_user, v_global, v_partners
    FROM auth.resolve_grant('', '00000000-0000-0000-0000-000000000000'::uuid) g;
  IF v_outcome IS DISTINCT FROM 'grant' OR v_user IS NULL THEN
    PERFORM catalog._err('P0001', 'catalog_actor_context');
  END IF;
  actor_user_id := v_user;
  resolved_global := coalesce(v_global, false);
  resolved_partner_ids := coalesce(v_partners, ARRAY[]::uuid[]);
  IF resolved_global THEN
    admin_type := 'super';
    partner_id := NULL;
  ELSIF coalesce(cardinality(resolved_partner_ids), 0) >= 1 THEN
    admin_type := 'partner';
    partner_id := resolved_partner_ids[1];
  ELSE
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
