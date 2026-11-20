-- EventForge migration 001: PostgreSQL event store table (roadmap Task 3 / EVF-22).
--
-- Run explicitly, by the project owner, as the migration role. The API never runs migrations at startup.
--
--   psql -h <host> -p <port> -U eventforge_migrator -d eventforge -v ON_ERROR_STOP=1 \
--        -f db/migrations/001-create-event-store.sql
--
-- Prerequisite: db/provisioning/001-provision.sql has created the roles, database and schema.
--
-- Safe to re-run. A first run creates eventforge.events; a later run creates nothing and instead verifies that
-- the existing table has exactly the expected columns and constraints (compared by actual definition against a
-- temporary table built from the same DDL), is owned by the migration role, and that the runtime role holds
-- only SELECT and INSERT. Any mismatch raises an error and the transaction rolls back; drift is never ignored
-- or repaired. There is no rollback script: the migration never drops or alters existing objects.
--
-- Plain SQL on purpose (no psql meta-commands) so that the integration tests can run the same file through Npgsql.

BEGIN;

DO $migration$
DECLARE
  table_ddl constant text := $ddl$
    CREATE TABLE %s (
      event_id        uuid        NOT NULL,
      stream_type     text        NOT NULL,
      stream_id       uuid        NOT NULL,
      stream_version  integer     NOT NULL,
      event_type      text        NOT NULL,
      schema_version  integer     NOT NULL,
      payload         jsonb       NOT NULL,
      metadata        jsonb       NOT NULL,
      recorded_at     timestamptz NOT NULL,
      CONSTRAINT events_pkey PRIMARY KEY (event_id),
      CONSTRAINT events_stream_version_uq UNIQUE (stream_type, stream_id, stream_version),
      CONSTRAINT events_stream_version_positive CHECK (stream_version > 0),
      CONSTRAINT events_schema_version_positive CHECK (schema_version > 0),
      CONSTRAINT events_stream_type_valid CHECK (stream_type ~ '^[A-Za-z][A-Za-z0-9._-]{0,99}$'),
      CONSTRAINT events_event_type_valid CHECK (event_type ~ '^[A-Za-z][A-Za-z0-9._-]{0,99}$'),
      CONSTRAINT events_payload_is_object CHECK (jsonb_typeof(payload) = 'object'),
      CONSTRAINT events_metadata_is_object CHECK (jsonb_typeof(metadata) = 'object')
    )
  $ddl$;
  actual_oid   oid;
  expected_oid oid;
  differences  text;
BEGIN
  IF current_user <> 'eventforge_migrator' THEN
    RAISE EXCEPTION 'Run this migration as eventforge_migrator (current role: %)', current_user;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_namespace n
                 WHERE n.nspname = 'eventforge' AND pg_get_userbyid(n.nspowner) = 'eventforge_migrator') THEN
    RAISE EXCEPTION 'Schema eventforge is missing or not owned by eventforge_migrator; run the provisioning script first';
  END IF;

  IF to_regclass('eventforge.events') IS NULL THEN
    EXECUTE format(table_ddl, 'eventforge.events');
  ELSE
    -- Existing table: compare its actual definition with a temporary table created from the same DDL.
    EXECUTE format(table_ddl, 'pg_temp.eventforge_events_expected');
    actual_oid   := 'eventforge.events'::regclass;
    expected_oid := 'pg_temp.eventforge_events_expected'::regclass;

    IF (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = actual_oid) <> 'eventforge_migrator' THEN
      RAISE EXCEPTION 'Table eventforge.events is not owned by eventforge_migrator';
    END IF;

    IF (SELECT relkind FROM pg_class WHERE oid = actual_oid) <> 'r'
       OR (SELECT relrowsecurity OR relforcerowsecurity FROM pg_class WHERE oid = actual_oid) THEN
      RAISE EXCEPTION 'Table eventforge.events is not an ordinary table without row-level security';
    END IF;

    -- Columns: name, type, nullability, default, identity and generation must match exactly.
    SELECT string_agg(d.description, E'\n' ORDER BY d.description) INTO differences
    FROM (
      SELECT 'column only in existing table: ' || c.col AS description
      FROM (
        SELECT a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull, a.atthasdef, a.attidentity, a.attgenerated,
               a.attname::text AS col
        FROM pg_attribute a
        WHERE a.attrelid = actual_oid AND a.attnum > 0 AND NOT a.attisdropped
        EXCEPT
        SELECT a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull, a.atthasdef, a.attidentity, a.attgenerated,
               a.attname::text
        FROM pg_attribute a
        WHERE a.attrelid = expected_oid AND a.attnum > 0 AND NOT a.attisdropped
      ) c
      UNION ALL
      SELECT 'column missing or different in existing table: ' || c.col
      FROM (
        SELECT a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull, a.atthasdef, a.attidentity, a.attgenerated,
               a.attname::text AS col
        FROM pg_attribute a
        WHERE a.attrelid = expected_oid AND a.attnum > 0 AND NOT a.attisdropped
        EXCEPT
        SELECT a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull, a.atthasdef, a.attidentity, a.attgenerated,
               a.attname::text
        FROM pg_attribute a
        WHERE a.attrelid = actual_oid AND a.attnum > 0 AND NOT a.attisdropped
      ) c
    ) d;
    IF differences IS NOT NULL THEN
      RAISE EXCEPTION E'Schema drift in eventforge.events (columns):\n%', differences;
    END IF;

    -- Constraints (primary key, unique, check): name, kind and definition must match exactly.
    SELECT string_agg(d.description, E'\n' ORDER BY d.description) INTO differences
    FROM (
      SELECT 'constraint only in existing table or different: ' || c.conname AS description
      FROM (
        SELECT conname, contype, pg_get_constraintdef(oid) AS def
        FROM pg_constraint WHERE conrelid = actual_oid AND contype <> 'n'
        EXCEPT
        SELECT conname, contype, pg_get_constraintdef(oid)
        FROM pg_constraint WHERE conrelid = expected_oid AND contype <> 'n'
      ) c
      UNION ALL
      SELECT 'constraint missing in existing table: ' || c.conname
      FROM (
        SELECT conname, contype, pg_get_constraintdef(oid) AS def
        FROM pg_constraint WHERE conrelid = expected_oid AND contype <> 'n'
        EXCEPT
        SELECT conname, contype, pg_get_constraintdef(oid)
        FROM pg_constraint WHERE conrelid = actual_oid AND contype <> 'n'
      ) c
    ) d;
    IF differences IS NOT NULL THEN
      RAISE EXCEPTION E'Schema drift in eventforge.events (constraints):\n%', differences;
    END IF;

    DROP TABLE pg_temp.eventforge_events_expected;
  END IF;

  -- Runtime role: SELECT and INSERT only. Grants are idempotent.
  REVOKE ALL ON TABLE eventforge.events FROM PUBLIC, eventforge_app;
  GRANT SELECT, INSERT ON TABLE eventforge.events TO eventforge_app;

  IF NOT has_table_privilege('eventforge_app', 'eventforge.events', 'SELECT')
     OR NOT has_table_privilege('eventforge_app', 'eventforge.events', 'INSERT')
     OR has_any_column_privilege('eventforge_app', 'eventforge.events', 'UPDATE')
     OR has_table_privilege('eventforge_app', 'eventforge.events', 'DELETE')
     OR has_table_privilege('eventforge_app', 'eventforge.events', 'TRUNCATE')
     OR has_table_privilege('eventforge_app', 'eventforge.events', 'REFERENCES')
     OR has_table_privilege('eventforge_app', 'eventforge.events', 'TRIGGER')
     OR has_schema_privilege('eventforge_app', 'eventforge', 'CREATE') THEN
    RAISE EXCEPTION 'eventforge_app must hold exactly SELECT and INSERT on eventforge.events and no schema CREATE';
  END IF;
END
$migration$;

COMMIT;
