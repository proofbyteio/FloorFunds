-- floorfunds-kit preflight (K1, TL 8:32). READ-ONLY: runs before any SQL that changes anything.
-- psql variables: db, role, api_role, login_role. Output (psql -tA), one line per finding:
--   REFUSE <reason>   hard refusal; --force never overrides it
--   EXISTS <object>   a kit-marked object already exists; the kit proceeds only with --force
-- Kit objects carry COMMENT ... IS 'floorfunds-kit' (shared description). Unmarked = not created by the kit.
BEGIN TRANSACTION READ ONLY;
WITH want(name) AS (VALUES (:'role'), (:'api_role'), (:'login_role')),
ri AS (
  SELECT w.name, a.oid, coalesce(a.rolsuper, false) AS super,
         (shobj_description(a.oid, 'pg_authid') IS NOT DISTINCT FROM 'floorfunds-kit') AS marked
  FROM want w LEFT JOIN pg_roles a ON a.rolname = w.name),
d AS (
  SELECT oid, datistemplate, pg_get_userbyid(datdba)::text AS owner,
         (shobj_description(oid, 'pg_database') IS NOT DISTINCT FROM 'floorfunds-kit') AS marked
  FROM pg_database WHERE datname = :'db')
SELECT line FROM (
  SELECT 1 AS k, 'REFUSE role ' || name || ' is a superuser' AS line FROM ri WHERE super
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
  SELECT 7, 'EXISTS role ' || name FROM ri WHERE oid IS NOT NULL
  UNION ALL
  SELECT 8, 'EXISTS database ' || :'db' FROM d
) f ORDER BY k, line;
ROLLBACK;
