-- floorfunds-kit: create or update ONE login role. psql variables: r (name), p (password; set on stdin, never argv).
-- K1: a new role is created and marked in one transaction; an existing role is altered only when it carries the
-- floorfunds-kit marker and is not a superuser. Anything else aborts here (the preflight already refused it).
SELECT NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'r') AS kit_new \gset
\if :kit_new
BEGIN;
SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', :'r', :'p') \gexec
SELECT format('COMMENT ON ROLE %I IS %L', :'r', 'floorfunds-kit') \gexec
COMMIT;
\else
SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'r' AND NOT rolsuper
               AND shobj_description(oid, 'pg_authid') IS NOT DISTINCT FROM 'floorfunds-kit') AS kit_ok \gset
\if :kit_ok
SELECT format('ALTER ROLE %I LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', :'r', :'p') \gexec
\else
DO $$ BEGIN RAISE EXCEPTION 'floorfunds-kit: refusing to alter a role the kit did not create (no marker, or a superuser)'; END $$;
\endif
\endif
