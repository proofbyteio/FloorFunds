-- floorfunds-kit preflight (K1, TL 8:32). READ-ONLY: runs before any SQL that changes anything.
-- psql variables: db, role, api_role, login_role, issuer_role. Output (psql -tA), one line per finding:
--   REFUSE <reason>   hard refusal; --force never overrides it
--   EXISTS <object>   a kit-marked object already exists; the kit proceeds only with --force
--   END floorfunds-kit-preflight   always the LAST line (KF3). setup.sh/setup.ps1 fail closed (exit 5) without it,
--                                  so empty or truncated output is never read as "no findings".
-- KF1: a kit role (--role, <db>_admin_api, <db>_login, <db>_issuer) is refused when it has BYPASSRLS, CREATEROLE, CREATEDB or
-- REPLICATION, or when it is a member (directly or through other roles) of any role outside this run's kit-marked
-- roles: pg_* roles, superuser/privileged roles, unmarked roles. The kit grants no role memberships at all and its
-- roles need none (db/migrations.sql grants only object privileges), so any membership was added by someone else.
-- TLD 9:19: refused first of all when the admin connection (current_user) is not a superuser.
-- KF-C: likewise refused when a role outside this run's kit-marked roles is a member OF a kit role.
-- KF-G (TLD 9:42): and when two of this run's kit roles are members of each other: no pg_auth_members row may involve a kit role.
-- Kit objects carry COMMENT ... IS 'floorfunds-kit' (shared description). Unmarked = not created by the kit.
BEGIN TRANSACTION READ ONLY;
WITH RECURSIVE want(name) AS (VALUES (:'role'), (:'api_role'), (:'login_role'), (:'issuer_role')),
ri AS (
  SELECT w.name, a.oid, coalesce(a.rolsuper, false) AS super,
         concat_ws(', ', CASE WHEN a.rolbypassrls THEN 'BYPASSRLS' END, CASE WHEN a.rolcreaterole THEN 'CREATEROLE' END,
                   CASE WHEN a.rolcreatedb THEN 'CREATEDB' END, CASE WHEN a.rolreplication THEN 'REPLICATION' END) AS attrs,
         (shobj_description(a.oid, 'pg_authid') IS NOT DISTINCT FROM 'floorfunds-kit') AS marked
  FROM want w LEFT JOIN pg_roles a ON a.rolname = w.name),
kit AS (SELECT oid FROM ri WHERE oid IS NOT NULL AND marked),
mem AS (  -- every role a kit role is a member of, transitively
  SELECT ri.name, m.roleid FROM ri JOIN pg_auth_members m ON m.member = ri.oid
  UNION
  SELECT mem.name, m.roleid FROM mem JOIN pg_auth_members m ON m.member = mem.roleid),
d AS (
  SELECT oid, datistemplate, pg_get_userbyid(datdba)::text AS owner,
         (shobj_description(oid, 'pg_database') IS NOT DISTINCT FROM 'floorfunds-kit') AS marked
  FROM pg_database WHERE datname = :'db')
SELECT line FROM (
  -- TLD 9:19: the admin connection must be a superuser (role.sql's catalog LOCK and the role checks need it).
  SELECT 0 AS k, 'REFUSE the admin connection must be a PostgreSQL superuser (e.g. postgres); ' || current_user || ' is not.' AS line
    FROM pg_roles WHERE rolname = current_user AND NOT rolsuper
  UNION ALL
  SELECT 1, 'REFUSE role ' || name || ' is a superuser' AS line FROM ri WHERE super
  UNION ALL
  SELECT 2, 'REFUSE role ' || name || ' exists but was not created by the kit (no floorfunds-kit marker)' FROM ri WHERE oid IS NOT NULL AND NOT marked
  UNION ALL
  SELECT 3, 'REFUSE role ' || ri.name || ' owns database ' || x.datname || ' (a kit role may own only its --db)'
    FROM ri JOIN pg_database x ON x.datdba = ri.oid WHERE x.datname <> :'db'
  UNION ALL
  SELECT 4, 'REFUSE database ' || :'db' || ' is a template database' FROM d WHERE datistemplate
  UNION ALL
  SELECT 5, 'REFUSE database ' || :'db' || ' exists but was not created by the kit (no floorfunds-kit marker)' FROM d WHERE NOT marked
  UNION ALL
  SELECT 6, 'REFUSE database ' || :'db' || ' is owned by ' || owner || ', not by --role ' || :'role' FROM d WHERE owner <> :'role'
  UNION ALL
  SELECT 9, 'REFUSE role ' || name || ' has ' || attrs || ' (a kit role must have none of BYPASSRLS, CREATEROLE, CREATEDB, REPLICATION)' FROM ri WHERE attrs <> ''
  UNION ALL
  SELECT 10, 'REFUSE role ' || mem.name || ' is a member of role ' || g.rolname || ' (a kit role may belong to no role outside the kit)'
    FROM mem JOIN pg_roles g ON g.oid = mem.roleid WHERE mem.roleid NOT IN (SELECT oid FROM kit)
  UNION ALL
  SELECT 11, 'REFUSE role ' || g.rolname || ' is a member of kit role ' || ri.name || ' (KF-C: no role outside the kit may belong to a kit role)'
    FROM ri JOIN pg_auth_members m ON m.roleid = ri.oid JOIN pg_roles g ON g.oid = m.member WHERE m.member NOT IN (SELECT oid FROM kit)
  UNION ALL
  SELECT 12, 'REFUSE role ' || g.rolname || ' is a member of kit role ' || ri.name || ' (KF-G: the kit uses no role memberships, not even between its own roles)'
    FROM ri JOIN pg_auth_members m ON m.roleid = ri.oid JOIN pg_roles g ON g.oid = m.member WHERE m.member IN (SELECT oid FROM kit)
  UNION ALL
  SELECT 7, 'EXISTS role ' || name FROM ri WHERE oid IS NOT NULL
  UNION ALL
  SELECT 8, 'EXISTS database ' || :'db' FROM d
  UNION ALL
  SELECT 99, 'END floorfunds-kit-preflight'
) f ORDER BY k, line;
ROLLBACK;
