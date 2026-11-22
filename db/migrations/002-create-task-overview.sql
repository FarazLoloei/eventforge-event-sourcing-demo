-- EventForge migration 002: PostgreSQL task overview read model (roadmap Task 6 / EVF-25, subtask EVF-36).
--
-- Run explicitly, by the project owner, as the migration role, after 001-create-event-store.sql. The API never
-- runs migrations at startup.
--
--   psql -h <host> -p <port> -U eventforge_migrator -d eventforge -v ON_ERROR_STOP=1 \
--        -f db/migrations/002-create-task-overview.sql
--
-- Prerequisite: db/provisioning/001-provision.sql has created the roles, database and schema.
--
-- This migration creates the TABLE (its columns and constraints) and the runtime PRIVILEGES on it only; the
-- eventforge schema itself comes from the provisioning script. Nothing writes to the table yet: the inline projector
-- that keeps it in step with the event store (EVF-37) and the projection-backed state query (EVF-38) are separate
-- tasks. The table is a derived read model; eventforge.events stays the source of truth and is not touched here.
--
-- Existing databases: this migration creates an EMPTY table and does not backfill it. A database that already holds
-- Task events has no overview rows for them until a later, separately approved step (replay, Task 7) builds them.
-- Do not deploy the projector (EVF-37) against such a database without that step.
--
-- Safe to re-run. A first run creates eventforge.task_overview; a later run creates nothing and instead verifies
-- that the existing table has exactly the expected columns and constraints (compared by actual definition against
-- a temporary table built from the same DDL), has only its primary-key index and no triggers, and is owned by the
-- migration role. The runtime role's privileges are then re-asserted and verified from the catalog. Any mismatch
-- raises an error and the transaction rolls back; schema drift is never ignored, repaired or replaced, and rows are
-- never deleted or rewritten. There is no rollback script: the migration never drops or alters existing objects.
--
-- Plain SQL on purpose (no psql meta-commands) so that the integration tests can run the same file through Npgsql.

BEGIN;

DO $migration$
DECLARE
  -- Values and nullability follow the Task Domain (WorkflowStatus, TestOutcome, AgentId and GenerationId rules) and the
  -- REST state response. 'created' is the first status a stored task can have; NotCreated has no row.
  --  * created:   no agent, generation or result (TaskCreated sets none of them).
  --  * assigned:  an agent; a generation only after CodeGenerated; a result only for a generation.
  --  * completed: an agent, a generation and a passed result (TaskCompleted requires the current generation to pass).
  -- Identifier checks mirror the Domain: 1 to 100 characters, not blank, no control characters. PostgreSQL counts code
  -- points where the Domain counts UTF-16 units, so the database is at most as strict as the Domain, never stricter.
  table_ddl constant text := $ddl$
    CREATE TABLE %s (
      task_id               uuid    NOT NULL,
      status                text    NOT NULL,
      assigned_agent_id     text,
      current_generation_id text,
      latest_test_result    text,
      last_stream_version   integer NOT NULL,
      CONSTRAINT task_overview_pkey PRIMARY KEY (task_id),
      CONSTRAINT task_overview_status_valid CHECK (status IN ('created', 'assigned', 'completed')),
      CONSTRAINT task_overview_test_result_valid CHECK (latest_test_result IS NULL OR latest_test_result IN ('passed', 'failed')),
      CONSTRAINT task_overview_version_positive CHECK (last_stream_version > 0),
      CONSTRAINT task_overview_agent_id_valid CHECK (
        assigned_agent_id IS NULL
        OR (char_length(assigned_agent_id) BETWEEN 1 AND 100
            AND btrim(assigned_agent_id) <> ''
            AND assigned_agent_id !~ '[\u0001-\u001f\u007f-\u009f]')),
      CONSTRAINT task_overview_generation_id_valid CHECK (
        current_generation_id IS NULL
        OR (char_length(current_generation_id) BETWEEN 1 AND 100
            AND btrim(current_generation_id) <> ''
            AND current_generation_id !~ '[\u0001-\u001f\u007f-\u009f]')),
      CONSTRAINT task_overview_created_has_no_assignment CHECK (
        status <> 'created'
        OR (assigned_agent_id IS NULL AND current_generation_id IS NULL AND latest_test_result IS NULL)),
      CONSTRAINT task_overview_assigned_has_agent CHECK (status = 'created' OR assigned_agent_id IS NOT NULL),
      CONSTRAINT task_overview_result_requires_generation CHECK (
        latest_test_result IS NULL OR current_generation_id IS NOT NULL),
      CONSTRAINT task_overview_completed_passed CHECK (
        status <> 'completed'
        OR (current_generation_id IS NOT NULL AND latest_test_result IS NOT DISTINCT FROM 'passed'))
    )
  $ddl$;
  -- Columns the projector may change. task_id is never updatable; the runtime role has no DELETE or TRUNCATE.
  updatable_columns constant text :=
    'assigned_agent_id,current_generation_id,last_stream_version,latest_test_result,status';
  actual_oid   oid;
  expected_oid oid;
  app_oid      oid;
  differences  text;
  granted      text;
BEGIN
  IF current_user <> 'eventforge_migrator' THEN
    RAISE EXCEPTION 'Run this migration as eventforge_migrator (current role: %)', current_user;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_namespace n
                 WHERE n.nspname = 'eventforge' AND pg_get_userbyid(n.nspowner) = 'eventforge_migrator') THEN
    RAISE EXCEPTION 'Schema eventforge is missing or not owned by eventforge_migrator; run the provisioning script first';
  END IF;

  SELECT oid INTO app_oid FROM pg_roles WHERE rolname = 'eventforge_app';
  IF app_oid IS NULL THEN
    RAISE EXCEPTION 'Role eventforge_app is missing; run the provisioning script first';
  END IF;

  IF to_regclass('eventforge.task_overview') IS NULL THEN
    EXECUTE format(table_ddl, 'eventforge.task_overview');
  ELSE
    -- Existing table: compare its actual definition with a temporary table created from the same DDL.
    EXECUTE format(table_ddl, 'pg_temp.eventforge_task_overview_expected');
    actual_oid   := 'eventforge.task_overview'::regclass;
    expected_oid := 'pg_temp.eventforge_task_overview_expected'::regclass;

    IF (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = actual_oid) <> 'eventforge_migrator' THEN
      RAISE EXCEPTION 'Table eventforge.task_overview is not owned by eventforge_migrator';
    END IF;

    IF (SELECT relkind FROM pg_class WHERE oid = actual_oid) <> 'r'
       OR (SELECT relrowsecurity OR relforcerowsecurity FROM pg_class WHERE oid = actual_oid) THEN
      RAISE EXCEPTION 'Table eventforge.task_overview is not an ordinary table without row-level security';
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
      RAISE EXCEPTION E'Schema drift in eventforge.task_overview (columns):\n%', differences;
    END IF;

    -- Constraints (primary key and check): name, kind and definition must match exactly.
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
      RAISE EXCEPTION E'Schema drift in eventforge.task_overview (constraints):\n%', differences;
    END IF;

    -- Only the primary-key index and no user triggers: an extra index or trigger is drift too.
    IF (SELECT count(*) FROM pg_index WHERE indrelid = actual_oid) <> 1
       OR EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = actual_oid AND NOT tgisinternal) THEN
      RAISE EXCEPTION 'Schema drift in eventforge.task_overview (indexes or triggers): expected only the primary-key index and no triggers';
    END IF;

    DROP TABLE pg_temp.eventforge_task_overview_expected;
  END IF;

  -- Runtime role: SELECT, INSERT and UPDATE on the projector columns only. Grants are idempotent; revoking first also
  -- clears any column-level privilege. Nothing is granted to PUBLIC.
  REVOKE ALL ON TABLE eventforge.task_overview FROM PUBLIC, eventforge_app;
  GRANT SELECT, INSERT ON TABLE eventforge.task_overview TO eventforge_app;
  GRANT UPDATE (status, assigned_agent_id, current_generation_id, latest_test_result, last_stream_version)
    ON TABLE eventforge.task_overview TO eventforge_app;

  -- Verify the effective privileges from the catalog, not the GRANT text above.
  actual_oid := 'eventforge.task_overview'::regclass;

  -- Table ACL: only the owner and the runtime role appear, with exactly INSERT and SELECT for the runtime role
  -- (no PUBLIC entry, no grant option, no UPDATE/DELETE/TRUNCATE/REFERENCES/TRIGGER/MAINTAIN at table level).
  SELECT string_agg(x.privilege_type, ',' ORDER BY x.privilege_type COLLATE "C") INTO granted
  FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) x
  WHERE c.oid = actual_oid AND x.grantee = app_oid;

  IF granted IS DISTINCT FROM 'INSERT,SELECT'
     OR EXISTS (SELECT 1 FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) x
                WHERE c.oid = actual_oid AND (x.grantee NOT IN (c.relowner, app_oid) OR x.is_grantable))
     OR NOT has_table_privilege('eventforge_app', 'eventforge.task_overview', 'SELECT')
     OR NOT has_table_privilege('eventforge_app', 'eventforge.task_overview', 'INSERT')
     OR has_table_privilege('eventforge_app', 'eventforge.task_overview', 'UPDATE')
     OR has_table_privilege('eventforge_app', 'eventforge.task_overview', 'DELETE')
     OR has_table_privilege('eventforge_app', 'eventforge.task_overview', 'TRUNCATE')
     OR has_table_privilege('eventforge_app', 'eventforge.task_overview', 'REFERENCES')
     OR has_table_privilege('eventforge_app', 'eventforge.task_overview', 'TRIGGER')
     OR has_schema_privilege('eventforge_app', 'eventforge', 'CREATE') THEN
    RAISE EXCEPTION 'eventforge_app must hold exactly SELECT, INSERT and column UPDATE on eventforge.task_overview and no schema CREATE';
  END IF;

  -- Column ACLs: only the runtime role, only UPDATE, on exactly the projector columns and never task_id.
  SELECT string_agg(a.attname::text, ',' ORDER BY a.attname::text COLLATE "C") INTO granted
  FROM pg_attribute a CROSS JOIN LATERAL aclexplode(a.attacl) x
  WHERE a.attrelid = actual_oid AND a.attnum > 0 AND NOT a.attisdropped
    AND x.grantee = app_oid AND x.privilege_type = 'UPDATE';

  IF granted IS DISTINCT FROM updatable_columns
     OR EXISTS (SELECT 1 FROM pg_attribute a CROSS JOIN LATERAL aclexplode(a.attacl) x
                WHERE a.attrelid = actual_oid AND a.attnum > 0 AND NOT a.attisdropped
                  AND (x.grantee <> app_oid OR x.privilege_type <> 'UPDATE' OR x.is_grantable))
     OR has_column_privilege('eventforge_app', 'eventforge.task_overview', 'task_id', 'UPDATE')
     OR NOT has_column_privilege('eventforge_app', 'eventforge.task_overview', 'status', 'UPDATE')
     OR NOT has_column_privilege('eventforge_app', 'eventforge.task_overview', 'assigned_agent_id', 'UPDATE')
     OR NOT has_column_privilege('eventforge_app', 'eventforge.task_overview', 'current_generation_id', 'UPDATE')
     OR NOT has_column_privilege('eventforge_app', 'eventforge.task_overview', 'latest_test_result', 'UPDATE')
     OR NOT has_column_privilege('eventforge_app', 'eventforge.task_overview', 'last_stream_version', 'UPDATE') THEN
    RAISE EXCEPTION 'eventforge_app must hold exactly SELECT, INSERT and column UPDATE on eventforge.task_overview and no schema CREATE';
  END IF;
END
$migration$;

COMMIT;
