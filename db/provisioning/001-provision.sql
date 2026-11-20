-- EventForge PostgreSQL provisioning for FRESH environments (roadmap Task 2 / EVF-21).
--
-- Designed to be repeatable and non-destructive: it only creates what is missing and never drops, truncates,
-- resets passwords of existing roles, or alters existing objects. Initial provisioning (empty volume) is
-- verified; re-execution has not yet been validated. It creates NO tables and grants NO table
-- privileges; table grants belong with the migrations that create the tables (Tasks 3, 6, 7).
-- Do NOT run it against a server that already hosts EventForge unless you have reviewed it first.
--
-- Requires psql 15+ (\getenv) and a PostgreSQL 17 administrative connection to the maintenance database
-- ("postgres"). Passwords are read from the environment, never from this file:
--
--   EVENTFORGE_APP_PASSWORD        runtime role   eventforge_app
--   EVENTFORGE_MIGRATOR_PASSWORD   migration role eventforge_migrator (owns the schema)
--   EVENTFORGE_OPERATOR_PASSWORD   maintenance role eventforge_operator (replay/cutover, Task 7)
--
-- Example: psql -h <host> -p <port> -U <admin> -d postgres -v ON_ERROR_STOP=1 -f db/provisioning/001-provision.sql
-- Note: the server logs CREATE ROLE statements if log_statement is 'ddl' or 'all'; passwords then appear in
-- the server log. Use throwaway passwords for local development.

\set ON_ERROR_STOP on

\getenv app_password EVENTFORGE_APP_PASSWORD
\getenv migrator_password EVENTFORGE_MIGRATOR_PASSWORD
\getenv operator_password EVENTFORGE_OPERATOR_PASSWORD

\if :{?app_password}
\else
  DO $$ BEGIN RAISE EXCEPTION 'EVENTFORGE_APP_PASSWORD is not set'; END $$;
\endif
\if :{?migrator_password}
\else
  DO $$ BEGIN RAISE EXCEPTION 'EVENTFORGE_MIGRATOR_PASSWORD is not set'; END $$;
\endif
\if :{?operator_password}
\else
  DO $$ BEGIN RAISE EXCEPTION 'EVENTFORGE_OPERATOR_PASSWORD is not set'; END $$;
\endif

SELECT (:'app_password' = '' OR :'migrator_password' = '' OR :'operator_password' = '') AS empty_password \gset
\if :empty_password
  DO $$ BEGIN RAISE EXCEPTION 'EventForge role passwords must not be empty'; END $$;
\endif

-- Roles: login-only, no elevated attributes, no memberships. Existing roles are left untouched.
SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD %L',
              'eventforge_migrator', :'migrator_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'eventforge_migrator') \gexec

SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD %L',
              'eventforge_app', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'eventforge_app') \gexec

SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD %L',
              'eventforge_operator', :'operator_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'eventforge_operator') \gexec

-- Database, owned by the migration role. An existing database is left as it is.
SELECT 'CREATE DATABASE eventforge OWNER eventforge_migrator'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'eventforge') \gexec

REVOKE ALL ON DATABASE eventforge FROM PUBLIC;
GRANT CONNECT ON DATABASE eventforge TO eventforge_migrator, eventforge_app, eventforge_operator;

\connect eventforge

-- Schema owned by the migration role; runtime and maintenance roles may only resolve names in it.
CREATE SCHEMA IF NOT EXISTS eventforge AUTHORIZATION eventforge_migrator;
REVOKE ALL ON SCHEMA eventforge FROM PUBLIC;
GRANT USAGE ON SCHEMA eventforge TO eventforge_app, eventforge_operator;

-- Fail closed: refuse to report success if existing roles or the schema do not match the separation model.
DO $$
DECLARE
  bad text;
BEGIN
  SELECT string_agg(rolname, ', ') INTO bad
  FROM pg_roles
  WHERE rolname IN ('eventforge_app', 'eventforge_migrator', 'eventforge_operator')
    AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls OR rolreplication);
  IF bad IS NOT NULL THEN
    RAISE EXCEPTION 'Roles with elevated attributes: %', bad;
  END IF;

  IF (SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'eventforge') <> 'eventforge_migrator' THEN
    RAISE EXCEPTION 'Schema eventforge is not owned by eventforge_migrator';
  END IF;

  IF EXISTS (SELECT 1 FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member
             WHERE r.rolname IN ('eventforge_app', 'eventforge_migrator', 'eventforge_operator')) THEN
    RAISE EXCEPTION 'An EventForge role is a member of another role';
  END IF;
END $$;

\echo EventForge provisioning finished: roles, database and schema are in place; no tables were created.
