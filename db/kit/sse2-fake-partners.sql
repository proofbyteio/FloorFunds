-- SSE2-FAKE-TEST-ONLY. Never production.
-- Planted by db/setup.sh --seed as the DB owner (partner_owner_write), after migrations
-- (including 20261120130000 FK) and before db/seed/*.sql as _test_seed.
-- Supplies:
--   5f0c3e1a-0001-4000-8000-000000000001  PCI fixture partner (s2a-test-app-user.sql)
--   5f0c3e1a-0064-4000-8000-000000000011  Req064 suite Pa (Admin.Api.Checks Seed())
--   5f0c3e1a-0064-4000-8000-000000000012  Req064 suite Pb (Admin.Api.Checks Seed())
-- No DEFINER helper. Idempotent.

INSERT INTO auth.partner (
  partner_id, name, address_line_1, address_line_2, city, country, region, postal_code, phone, email, time_zone, status)
VALUES
  ('5f0c3e1a-0001-4000-8000-000000000001', 'SSE2 Fake Partner PCI', '100 Fake Street', '',
   'Sampletown', 'US', 'NV', '89101', '+17025551001', 'sse2-fake-partner-pci@example.com',
   'America/Los_Angeles', 'active'),
  ('5f0c3e1a-0064-4000-8000-000000000011', 'SSE2 Fake Partner Req064 A', '101 Fake Street', '',
   'Sampletown', 'US', 'NV', '89101', '+17025551011', 'sse2-fake-partner-req064-a@example.com',
   'America/Los_Angeles', 'active'),
  ('5f0c3e1a-0064-4000-8000-000000000012', 'SSE2 Fake Partner Req064 B', '102 Fake Street', '',
   'Sampletown', 'US', 'NV', '89101', '+17025551012', 'sse2-fake-partner-req064-b@example.com',
   'America/Los_Angeles', 'active')
ON CONFLICT (partner_id) DO NOTHING;
