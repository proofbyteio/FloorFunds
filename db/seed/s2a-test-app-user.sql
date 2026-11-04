-- SSE2-FAKE-TEST-ONLY. S2a test seed for auth.app_user (INTERIM table, TLD 2026-11-04 7:45 F1). Never run in prod.
-- Mirrors src/Login.StandIn/fixtures.json (SI1 fixtures, md5 612918cc...): same user_id and status for all 7 users.
-- partner_id: admin_type partner -> its partner_id; super -> NULL; location users -> the partner owning their
-- location_slugs (SSE derivation, see src/S2A-BUILD-LOG.md). Run as the DB owner after db/migrations.sql. Idempotent.
BEGIN;
INSERT INTO auth.app_user (user_id, partner_id, status, locked_until) VALUES
  ('5f0c3e1a-0003-4000-8000-000000000c01', '5f0c3e1a-0001-4000-8000-000000000001', 'active', NULL), -- cashier_db    (pci-main)    SSE2-FAKE-TEST-ONLY
  ('5f0c3e1a-0003-4000-8000-000000000c02', '5f0c3e1a-0001-4000-8000-000000000001', 'active', NULL), -- no_role_user  (pci-main)    SSE2-FAKE-TEST-ONLY
  ('5f0c3e1a-0003-4000-8000-000000000c03', '5f0c3e1a-0002-4000-8000-000000000002', 'active', NULL), -- nevada_db     (nevada-main) SSE2-FAKE-TEST-ONLY
  ('5f0c3e1a-0003-4000-8000-000000000c04', '5f0c3e1a-0001-4000-8000-000000000001', 'active', NULL), -- partner_admin (partner pci) SSE2-FAKE-TEST-ONLY
  ('5f0c3e1a-0003-4000-8000-000000000c0a', NULL,                                   'active', NULL), -- super_admin                 SSE2-FAKE-TEST-ONLY
  ('5f0c3e1a-0003-4000-8000-000000000c05', '5f0c3e1a-0001-4000-8000-000000000001', 'active', NULL), -- cashier_pwd   (pci-main)    SSE2-FAKE-TEST-ONLY
  ('5f0c3e1a-0003-4000-8000-000000000c06', '5f0c3e1a-0001-4000-8000-000000000001', 'active', NULL)  -- cashier_wia   (pci-main)    SSE2-FAKE-TEST-ONLY
ON CONFLICT (user_id) DO NOTHING;
COMMIT;
