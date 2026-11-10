-- EXAMPLE / SSE2-FAKE-TEST-ONLY / never production.
-- One synthetic global Super Admin and two synthetic partners, each with a Partner Admin.
-- These user ids are excluded from the fixture equality check (5f0c3e1a-0064-%).
-- Production audit event shape is PQ-277.11 and is not invented here.
BEGIN;
INSERT INTO auth.app_user (user_id, partner_id, status, locked_until) VALUES
  ('5f0c3e1a-0064-4000-8000-000000000001', NULL, 'active', NULL),
  ('5f0c3e1a-0064-4000-8000-000000000002', NULL, 'active', NULL),
  ('5f0c3e1a-0064-4000-8000-000000000003', NULL, 'active', NULL)
ON CONFLICT (user_id) DO NOTHING;
INSERT INTO auth.admin_assignment (
  assignment_id, user_id, role_id, scope_kind, partner_id, location_id, status, version, granted_by, granted_at, revoked_at)
VALUES
  ('5f0c3e1a-0064-4000-8000-0000000000a1', '5f0c3e1a-0064-4000-8000-000000000001', 'super_admin', 'global', NULL, NULL, 'active', 1, '5f0c3e1a-0064-4000-8000-000000000001', now(), NULL),
  ('5f0c3e1a-0064-4000-8000-0000000000a2', '5f0c3e1a-0064-4000-8000-000000000002', 'partner_admin', 'partner', '5f0c3e1a-0064-4000-8000-0000000000c1', NULL, 'active', 1, '5f0c3e1a-0064-4000-8000-000000000001', now(), NULL),
  ('5f0c3e1a-0064-4000-8000-0000000000a3', '5f0c3e1a-0064-4000-8000-000000000003', 'partner_admin', 'partner', '5f0c3e1a-0064-4000-8000-0000000000c2', NULL, 'active', 1, '5f0c3e1a-0064-4000-8000-000000000001', now(), NULL)
ON CONFLICT (assignment_id) DO NOTHING;
COMMIT;
