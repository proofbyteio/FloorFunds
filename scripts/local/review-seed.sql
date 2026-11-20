-- REQ-068 local review seed. SSE2-FAKE-TEST-ONLY / never production.
-- Loaded ONLY by scripts/local/* after db/setup --seed. Not part of kit seed.
-- User ids 5f0c3e1a-0068-… are outside Auth.Data.Checks fixture equality.
-- Stand-in usernames (review-fixtures.json): local_sa, local_pa, local_ca, local_la, local_la_one.
-- CA/LA use admin_type=partner in the stand-in so /admin + Issuer accept them; role_wire comes from DB.
-- Must be applied as a Postgres superuser with row_security off (FORCE RLS on org tables).

SET row_security = off;
BEGIN;

-- Partners
INSERT INTO auth.partner (
  partner_id, name, address_line_1, address_line_2, city, country, region, postal_code, phone, email, time_zone, status)
VALUES
  ('aaaaaaaa-0068-4000-8000-000000000001', 'Local Review Partner A', '10 Review Way', '',
   'Austin', 'US', 'TX', '78701', '+15125550101', 'local-pa@example.com', 'America/Chicago', 'active'),
  ('aaaaaaaa-0068-4000-8000-000000000002', 'Local Review Partner B', '20 Review Way', '',
   'Austin', 'US', 'TX', '78701', '+15125550102', 'local-pb@example.com', 'America/Chicago', 'active')
ON CONFLICT (partner_id) DO NOTHING;

-- Clients (active + inactive under Partner A; one under B)
INSERT INTO auth.client (
  client_id, partner_id, name, address_line_1, address_line_2, city, country, region, postal_code, phone, email, time_zone, status)
VALUES
  ('bbbbbbbb-0068-4000-8000-000000000001', 'aaaaaaaa-0068-4000-8000-000000000001', 'Local Review Client A1',
   '11 Client St', '', 'Austin', 'US', 'TX', '78701', '+15125550111', NULL, 'America/Chicago', 'active'),
  ('bbbbbbbb-0068-4000-8000-000000000002', 'aaaaaaaa-0068-4000-8000-000000000001', 'Local Review Client A2 Inactive',
   '12 Client St', '', 'Austin', 'US', 'TX', '78701', '+15125550112', NULL, 'America/Chicago', 'inactive'),
  ('bbbbbbbb-0068-4000-8000-000000000003', 'aaaaaaaa-0068-4000-8000-000000000002', 'Local Review Client B1',
   '21 Client St', '', 'Austin', 'US', 'TX', '78701', '+15125550113', NULL, 'America/Chicago', 'active')
ON CONFLICT (client_id) DO NOTHING;

-- Locations: 2+ active under Client A1 (DEC-334 picker), 1 under A1 for optional single-LA,
-- inactive client locations, Partner B location
INSERT INTO auth.location (
  location_id, partner_id, client_id, slug, name,
  address_line_1, address_line_2, city, country, region, postal_code, phone, email, time_zone, status)
VALUES
  ('cccccccc-0068-4000-8000-000000000001', 'aaaaaaaa-0068-4000-8000-000000000001', 'bbbbbbbb-0068-4000-8000-000000000001',
   'local-review-a1', 'Local Review Loc A1',
   '1 Loc', '', 'Austin', 'US', 'TX', '78701', '+15125550121', NULL, 'America/Chicago', 'active'),
  ('cccccccc-0068-4000-8000-000000000002', 'aaaaaaaa-0068-4000-8000-000000000001', 'bbbbbbbb-0068-4000-8000-000000000001',
   'local-review-a1b', 'Local Review Loc A1b',
   '2 Loc', '', 'Austin', 'US', 'TX', '78701', '+15125550122', NULL, 'America/Chicago', 'active'),
  ('cccccccc-0068-4000-8000-000000000003', 'aaaaaaaa-0068-4000-8000-000000000001', 'bbbbbbbb-0068-4000-8000-000000000001',
   'local-review-a1c', 'Local Review Loc A1c (single-LA)',
   '3 Loc', '', 'Austin', 'US', 'TX', '78701', '+15125550123', NULL, 'America/Chicago', 'active'),
  ('cccccccc-0068-4000-8000-000000000004', 'aaaaaaaa-0068-4000-8000-000000000001', 'bbbbbbbb-0068-4000-8000-000000000002',
   'local-review-a2-inact', 'Local Review Loc under inactive client',
   '4 Loc', '', 'Austin', 'US', 'TX', '78701', '+15125550124', NULL, 'America/Chicago', 'inactive'),
  ('cccccccc-0068-4000-8000-000000000005', 'aaaaaaaa-0068-4000-8000-000000000002', 'bbbbbbbb-0068-4000-8000-000000000003',
   'local-review-b1', 'Local Review Loc B1',
   '5 Loc', '', 'Austin', 'US', 'TX', '78701', '+15125550125', NULL, 'America/Chicago', 'active')
ON CONFLICT (location_id) DO NOTHING;

-- Users (0068 ids)
INSERT INTO auth.app_user (user_id, partner_id, status, locked_until) VALUES
  ('5f0c3e1a-0068-4000-8000-0000000000a1', NULL, 'active', NULL), -- local_sa
  ('5f0c3e1a-0068-4000-8000-0000000000a2', 'aaaaaaaa-0068-4000-8000-000000000001', 'active', NULL), -- local_pa
  ('5f0c3e1a-0068-4000-8000-0000000000a3', NULL, 'active', NULL), -- local_ca
  ('5f0c3e1a-0068-4000-8000-0000000000a4', NULL, 'active', NULL), -- local_la (2+ locs)
  ('5f0c3e1a-0068-4000-8000-0000000000a5', NULL, 'active', NULL)  -- local_la_one
ON CONFLICT (user_id) DO NOTHING;

-- Issuer binding: replaced at load time by local-run scripts via psql -v iss=
-- Placeholder below is overwritten; scripts always pass -v iss=...
INSERT INTO auth.user_issuer (user_id, issuer)
SELECT u.user_id, :'iss'
  FROM auth.app_user u
 WHERE u.user_id IN (
   '5f0c3e1a-0068-4000-8000-0000000000a1',
   '5f0c3e1a-0068-4000-8000-0000000000a2',
   '5f0c3e1a-0068-4000-8000-0000000000a3',
   '5f0c3e1a-0068-4000-8000-0000000000a4',
   '5f0c3e1a-0068-4000-8000-0000000000a5')
ON CONFLICT (user_id) DO UPDATE SET issuer = EXCLUDED.issuer;

-- Assignments (granted_by = SA). Disable session-revoke trigger: it UPDATEs
-- request_session under FORCE RLS and refuses the superuser seed path.
ALTER TABLE auth.admin_assignment DISABLE TRIGGER admin_assignment_revoke_sessions;
ALTER TABLE auth.admin_assignment DISABLE TRIGGER admin_assignment_last_sa;
INSERT INTO auth.admin_assignment (
  assignment_id, user_id, role_id, scope_kind, partner_id, client_id, location_id, status, version, granted_by, granted_at)
VALUES
  ('dddddddd-0068-4000-8000-0000000000b1', '5f0c3e1a-0068-4000-8000-0000000000a1', 'super_admin', 'global',
   NULL, NULL, NULL, 'active', 1, '5f0c3e1a-0068-4000-8000-0000000000a1', now()),
  ('dddddddd-0068-4000-8000-0000000000b2', '5f0c3e1a-0068-4000-8000-0000000000a2', 'partner_admin', 'partner',
   'aaaaaaaa-0068-4000-8000-000000000001', NULL, NULL, 'active', 1, '5f0c3e1a-0068-4000-8000-0000000000a1', now()),
  ('dddddddd-0068-4000-8000-0000000000b3', '5f0c3e1a-0068-4000-8000-0000000000a3', 'client_admin', 'client',
   NULL, 'bbbbbbbb-0068-4000-8000-000000000001', NULL, 'active', 1, '5f0c3e1a-0068-4000-8000-0000000000a1', now()),
  ('dddddddd-0068-4000-8000-0000000000b4', '5f0c3e1a-0068-4000-8000-0000000000a4', 'supervisor', 'location',
   NULL, NULL, 'cccccccc-0068-4000-8000-000000000001', 'active', 1, '5f0c3e1a-0068-4000-8000-0000000000a1', now()),
  ('dddddddd-0068-4000-8000-0000000000b5', '5f0c3e1a-0068-4000-8000-0000000000a4', 'supervisor', 'location',
   NULL, NULL, 'cccccccc-0068-4000-8000-000000000002', 'active', 1, '5f0c3e1a-0068-4000-8000-0000000000a1', now()),
  ('dddddddd-0068-4000-8000-0000000000b6', '5f0c3e1a-0068-4000-8000-0000000000a5', 'supervisor', 'location',
   NULL, NULL, 'cccccccc-0068-4000-8000-000000000003', 'active', 1, '5f0c3e1a-0068-4000-8000-0000000000a1', now())
ON CONFLICT (assignment_id) DO NOTHING;
ALTER TABLE auth.admin_assignment ENABLE TRIGGER admin_assignment_last_sa;
ALTER TABLE auth.admin_assignment ENABLE TRIGGER admin_assignment_revoke_sessions;

COMMIT;
