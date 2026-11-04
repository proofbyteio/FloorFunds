-- floorfunds-kit: (re)create the database. psql variables: db, role, drop (0/1).
-- K1: DROP only a marked, non-template DB owned by :role; CREATE marks the new DB; grants only on a marked DB
-- owned by :role. Anything else aborts here (the preflight already refused it).
\if :drop
SELECT format('DROP DATABASE %I WITH (FORCE)', :'db') FROM pg_database
 WHERE datname = :'db' AND NOT datistemplate AND pg_get_userbyid(datdba)::text = :'role'
   AND shobj_description(oid, 'pg_database') IS NOT DISTINCT FROM 'floorfunds-kit' \gexec
\endif
SELECT NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db') AS kit_new \gset
\if :kit_new
SELECT format('CREATE DATABASE %I OWNER %I', :'db', :'role') \gexec
SELECT format('COMMENT ON DATABASE %I IS %L', :'db', 'floorfunds-kit') \gexec
\endif
SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db' AND NOT datistemplate
               AND pg_get_userbyid(datdba)::text = :'role'
               AND shobj_description(oid, 'pg_database') IS NOT DISTINCT FROM 'floorfunds-kit') AS kit_ok \gset
\if :kit_ok
SELECT format('REVOKE CONNECT ON DATABASE %I FROM PUBLIC', :'db') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'db', :'role') \gexec
\else
DO $$ BEGIN RAISE EXCEPTION 'floorfunds-kit: refusing to touch a database the kit did not create (no marker, wrong owner, or a template)'; END $$;
\endif
