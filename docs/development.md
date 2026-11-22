# EventForge — Development Environment (EVF-21)

How to run EventForge in the two supported modes (native development, and a Docker Compose evaluation stack), and how PostgreSQL is configured and provisioned. For what is implemented and verified, see the [README status tables](../README.md#current-implementation); this guide adds no architectural decisions (the Npgsql decision is recorded in [ADR 0001](adr/0001-architecture.md#decision-history)).

> **Status:** PostgreSQL connectivity code, native launch configuration, Docker files and provisioning SQL exist. Mode 2 and the native check against the Compose PostgreSQL were run successfully in an isolated stack (author-reported, not CI). Mode 1: the runtime-role check against the owner's existing PostgreSQL 17.11 on `127.0.0.1:5434` passed (owner-run) and a Visual Studio launch reported `/health` Healthy (owner-reported); the VS Code launch is **not independently verified**. CI for EVF-21 is verified (`main` run: 42 tests passed, 0 failed, 0 skipped, 0 build warnings or errors). EVF-22 adds the event store migration and PostgreSQL integration tests, verified against an isolated disposable PostgreSQL 17 in CI ([PR #16](https://github.com/FarazLoloei/eventforge-event-sourcing-demo/pull/16)); the migration has **not** been run against the owner's development database. Since EVF-24 the API uses the event store for the local Task workflow routes (see the README section "Task workflow API"); it still never runs migrations. Since EVF-36 migration `002` defines the `task_overview` table. EVF-37 (implemented and locally verified, not yet committed or verified in GitHub CI) updates that table atomically with appended Task events in the same PostgreSQL transaction; EVF-38 projection-backed reads are not implemented, so no query reads the table yet.

## Execution modes

Two supported modes. Both run the **same application code and configuration model**; only values supplied through standard ASP.NET Core overrides differ (`ConnectionStrings__EventForge`, `ASPNETCORE_ENVIRONMENT`). There is no Docker-specific implementation or business logic. This is an owner-approved development and evaluation strategy, not a production deployment decision.

| Mode | Audience | Database | API | Needs Docker |
| --- | --- | --- | --- | --- |
| 1. Local development (default) | Project owner | Owner's existing PostgreSQL Docker container, `127.0.0.1:5434`, database `eventforge` | Native: Visual Studio, VS Code or `dotnet run` | Only for the owner's existing database container |
| 2. Company evaluation / fresh clone | Reviewers | Compose `postgres` (host `127.0.0.1:5535`) | Compose `api` (host `127.0.0.1:8080`) | Yes (Docker Engine with Compose v2) |

Mode 1 never requires the API to run in Docker, preserves native debugging, breakpoints and hot reload (where supported), and never modifies unrelated projects, databases or existing infrastructure. The API starts and serves `/health` with **no database configuration at all**; the database is only touched by the explicit verification command below. An optional helper, `docker compose --profile db up -d`, starts only the Compose PostgreSQL for a native API; it is not a separate mode.

## Prerequisites

- Mode 1: .NET SDK 10.0.404 (`global.json`) and access to the existing PostgreSQL container. Visual Studio or VS Code is optional.
- Mode 2: Docker Engine with Compose v2 only. No .NET SDK, Visual Studio or PostgreSQL installation is needed (the image build uses the SDK inside Docker).
- Provisioning a fresh server manually (not needed in Mode 2): `psql` 15 or later.

## Configuration and secrets

The API reads the standard ASP.NET Core setting **`ConnectionStrings:EventForge`** (environment variable form `ConnectionStrings__EventForge`). Nothing in `appsettings*.json` or launch profiles contains a connection string, host or credential. Use the **runtime role** (`eventforge_app`) in it.

Connection string shape (placeholders only; use your own host, port and password):

```text
Host=<host>;Port=<port>;Database=eventforge;Username=eventforge_app;Password=<password>;Search Path=eventforge
```

### Local development: .NET User Secrets

`EventForge.Api` has a `UserSecretsId`; user secrets load automatically when `ASPNETCORE_ENVIRONMENT=Development` (set by the launch profiles and VS Code configuration).

```powershell
dotnet user-secrets set "ConnectionStrings:EventForge" "<connection string>" --project src/EventForge.Api
dotnet user-secrets list --project src/EventForge.Api   # prints the value: do not run it in a shared terminal
```

The value appears on the command line and in shell history. Visual Studio's **Manage User Secrets** (right-click the `EventForge.Api` project) edits the same store without that exposure. The secrets file lives outside the repository.

### Environment variable alternative

```powershell
$env:ConnectionStrings__EventForge = "<connection string>"
```

Configuration precedence is the ASP.NET Core default: command line, then environment variables, then user secrets (Development), then `appsettings*.json`.

## Native execution

### dotnet CLI

```powershell
dotnet restore EventForge.slnx
dotnet build EventForge.slnx --no-restore
dotnet run --project src/EventForge.Api --launch-profile http     # http://localhost:5080, Development
curl.exe -i http://localhost:5080/health                            # 200 OK, body "Healthy"
```

### Visual Studio

Open `EventForge.slnx`, set `EventForge.Api` as the startup project and choose the **http** profile (`src/EventForge.Api/Properties/launchSettings.json`). The **verify-database** profile runs the verification below.

### Visual Studio Code

Open the repository folder. `.vscode/launch.json` provides **EventForge.Api (http://localhost:5080)** and **EventForge.Api: verify database**; `.vscode/tasks.json` provides `build` and `test`. The C# Dev Kit or the C# extension (debugger) is required. These configurations are credential-free.

## Database connectivity verification (read-only)

```powershell
dotnet run --project src/EventForge.Api --launch-profile verify-database
# or, without a launch profile:
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/EventForge.Api --no-launch-profile -- --verify-database
```

The process does not start the web server. It opens one connection with the configured credentials, starts a `READ ONLY` transaction (never committed) and runs a single `SELECT`. It checks that:

1. the connection and authentication succeed (reports database, role and server version);
2. the role has no `SUPERUSER`, `CREATEROLE`, `CREATEDB`, `BYPASSRLS` or `REPLICATION` attribute;
3. schema `eventforge` exists;
4. the role has `USAGE` on it.

Exit code `0` means every check passed; `1` means a check failed, the connection could not be made (reason printed, for example the SQLSTATE or "Failed to connect to host:port"), the connection string is missing or invalid, or the 30-second limit expired. Output never includes the connection string or password. The check does not prove table privileges, migrations or event-store behavior; none exist yet.

## Mode 2: company evaluation / fresh clone

> **Verified locally (not CI).** This flow was run once in an isolated stack on the author's machine: provisioning, image build, start, `/health` and database verification passed (details in [Verification](#verification)). It has not been run on another machine or in CI.

Files: `Dockerfile` (multi-stage .NET 10 build, non-root runtime user), `.dockerignore`, `compose.yaml`, `.env.example`, `db/provisioning/001-provision.sql`. Nothing starts unless you select a profile.

### Prerequisites

Docker Engine with Compose v2, a running Docker daemon, free host ports `5535` and `8080` (override with `EVENTFORGE_POSTGRES_PORT` / `EVENTFORGE_API_PORT` in `.env`), and network access to pull `postgres:17` and the .NET 10 images. Run every command from the repository root.

### Local `.env` setup

```powershell
Copy-Item .env.example .env      # .env is git-ignored; never commit it
```

Fill in the blank values locally: `EVENTFORGE_POSTGRES_ADMIN_PASSWORD`, `EVENTFORGE_APP_PASSWORD`, `EVENTFORGE_MIGRATOR_PASSWORD`, `EVENTFORGE_OPERATOR_PASSWORD`. Use simple (letters and digits) throwaway passwords because the API one is embedded in a connection string. Compose refuses every command (including `down`) while a required value is empty. Do not paste `.env` contents into logs, issues or screenshots. Validate the configuration with `docker compose config --quiet` (it prints nothing on success); plain `docker compose config` prints the resolved values, including passwords. The API container always runs with `ASPNETCORE_ENVIRONMENT=Production`; it does not inherit that variable from your shell.

### Start

```powershell
docker compose --profile full up -d --build
```

Both ports are bound to `127.0.0.1` only by default.

| Service | Address | Notes |
| --- | --- | --- |
| API | http://127.0.0.1:8080 (`EVENTFORGE_API_PORT`) | `GET /health`; the container listens on 8080 |
| PostgreSQL (from the host) | 127.0.0.1:5535 (`EVENTFORGE_POSTGRES_PORT`) | loopback only |
| PostgreSQL (from the API container) | `postgres:5432` | Compose network; set through `ConnectionStrings__EventForge` |

On the first start of an empty volume, `001-provision.sql` creates the roles, database and schema (see [Provisioning](#database-provisioning)).

### Readiness checks

```powershell
docker compose --profile full ps                  # postgres: "healthy"; api: "running"
curl.exe -i http://127.0.0.1:8080/health          # 200 OK, body "Healthy"
```

The PostgreSQL check is `pg_isready` over TCP plus a query confirming that schema `eventforge` exists, so "healthy" means provisioned, and the API container waits for it. The API image has no container health check; `/health` is a liveness check and does not touch the database.

### Verification steps

1. `docker compose --profile full ps` shows `postgres` healthy and `api` running.
2. `/health` returns `200 OK` as above.
3. Optional database check with the runtime role, from a host that has the .NET SDK (the Mode 2 flow itself does not need one):
   ```powershell
   $env:ConnectionStrings__EventForge = "Host=127.0.0.1;Port=5535;Database=eventforge;Username=eventforge_app;Password=<EVENTFORGE_APP_PASSWORD>;Search Path=eventforge"
   dotnet run --project src/EventForge.Api --no-launch-profile -- --verify-database
   ```
   Expected: `PostgreSQL verification: PASSED`, exit code 0. Clear the variable afterwards (`Remove-Item Env:ConnectionStrings__EventForge`).
4. If something fails, read `docker compose --profile full logs postgres` or `logs api`. Logs must not contain the connection string or passwords; report any occurrence as a defect.

The Compose stack does not run the event store migration (migrations are explicit and owner-run), so the Task workflow routes cannot persist anything until you run the migration yourself; by default it demonstrates startup, configuration and provisioning. The Task REST API and Swagger UI (Development only) are described in the README section "Task workflow API".

### Non-destructive shutdown

```powershell
docker compose --profile full down        # stops and removes this stack's containers; KEEPS the data volume
```

Data lives in the Docker volume `eventforge_eventforge_pgdata` (Compose project `eventforge`, volume key `eventforge_pgdata`) and survives `down` and restarts. Deleting data is a separate, deliberate step: `docker compose --profile full down -v` also **deletes** that volume. Run these only from this repository (project name `eventforge`). Do not use `docker system prune` or `docker volume prune`, which can touch other projects.

### Optional helper: PostgreSQL in Compose, native API

```powershell
docker compose --profile db up -d
docker compose --profile db ps          # wait for "healthy"
```

PostgreSQL listens on `127.0.0.1:5535`. Point a native API at it with `Host=127.0.0.1;Port=5535;Database=eventforge;Username=eventforge_app;Password=<EVENTFORGE_APP_PASSWORD>;Search Path=eventforge`. This does not touch the owner's server on port 5434.

### Docker limits

Not intended for production or shared networks: no TLS, loopback binding only, role passwords pass through container environment variables (visible to `docker inspect`) and may appear in the server log if `log_statement` is `ddl` or `all`. **Preserved state:** the isolated stack's named volume `eventforge_eventforge_pgdata` (Compose project `eventforge`, holding the provisioned, table-less database) and the local git-ignored `.env` were deliberately kept after verification and are not removed automatically. Remove them only deliberately (`docker compose --profile full down -v` deletes the volume).

**Loopback trust is not password authentication.** In the official PostgreSQL image, `pg_hba.conf` trusts connections from `127.0.0.1` inside the container, so a `psql -h 127.0.0.1` test run inside the container succeeds with *any* password. Authenticate from the host through the published port (the `--verify-database` command) or from another container over the Compose network; the healthcheck uses loopback only to test readiness.

**Known nonfatal warning:** the API container prints `Cannot load library libgssapi_krb5.so.2` once when it first connects (including during `--verify-database`). Npgsql probes for Kerberos/GSS support that the slim .NET runtime image does not contain. The connection still succeeds and password authentication is unaffected; no suppression was applied. Do not install packages into the image merely to silence it without owner review.

Provisioning runs only against an empty data directory; changing a password in `.env` later does not change an existing role. If first-time provisioning fails, the data directory already exists and the script is not re-run automatically: the container stays `unhealthy` (the API will not start). Read the error with `docker compose logs postgres`, fix the cause, then either remove this stack's volume (`down -v`, which deletes its data) or run the script manually against the container.

## Database provisioning

`db/provisioning/001-provision.sql` prepares a **fresh** PostgreSQL 17 server. It is designed to be repeatable and non-destructive: it creates only missing objects, never drops, truncates or alters existing ones, and never resets an existing role's password. **Designed to be repeatable; initial provisioning verified. Re-execution has not yet been validated.**

| Object | Name | Purpose |
| --- | --- | --- |
| Role | `eventforge_migrator` | Owns the database and schema; used for future migrations |
| Role | `eventforge_app` | Runtime (API) role; `CONNECT` and schema `USAGE` only |
| Role | `eventforge_operator` | Maintenance role for future replay/cutover; `CONNECT` and schema `USAGE` only |
| Database | `eventforge` | `PUBLIC` access revoked |
| Schema | `eventforge` | Owned by the migrator; `PUBLIC` access revoked |

All roles are `LOGIN` without `SUPERUSER`, `CREATEDB`, `CREATEROLE`, `REPLICATION` or `BYPASSRLS` and are members of no other role. The script ends with checks that fail the run if existing roles or the schema owner contradict this. It creates **no tables, grants no table privileges and runs no migrations**; the API never runs migrations at startup. Table-level grants are added with the migrations that create the tables: EVF-22 grants the runtime role `SELECT` and `INSERT` on `eventforge.events` (see [Event Store migration](#event-store-migration-evf-22)); projection access and operator replay rights come later.

Manual run against a fresh server (passwords from the environment, never from the file):

```powershell
$env:EVENTFORGE_APP_PASSWORD = "<new password>"
$env:EVENTFORGE_MIGRATOR_PASSWORD = "<new password>"
$env:EVENTFORGE_OPERATOR_PASSWORD = "<new password>"
psql -h <host> -p <port> -U <admin role> -d postgres -v ON_ERROR_STOP=1 -f db/provisioning/001-provision.sql
```

The Compose `postgres` service runs the same file automatically on first start. It was executed once, by the Compose first start, on a fresh empty volume (PostgreSQL 17.12): all three roles, the database and the schema were created without errors and the catalog matched the model above. A second execution (re-run on an existing volume) has not been performed. It has never been run against any existing server, including the owner's server on port 5434, and must not be without review.

## Event Store migration (EVF-22)

> **Status: implemented and verified in CI.** Merged in [PR #16](https://github.com/FarazLoloei/eventforge-event-sourcing-demo/pull/16). PR CI ([run 44706915605](https://github.com/FarazLoloei/eventforge-event-sourcing-demo/actions/runs/44706915605)) and post-merge `main` CI ([run 44707030609](https://github.com/FarazLoloei/eventforge-event-sourcing-demo/actions/runs/44707030609)) passed: 42 unit/architecture tests, 21 non-database tests and 15 PostgreSQL 17 integration tests, zero failures and zero skips. The migration (first run and re-run) and the event store behavior were verified against an isolated, disposable PostgreSQL service. **The migration has not been executed against the owner's existing development database (`127.0.0.1:5434`).** Running it there is a separate owner action.

`db/migrations/001-create-event-store.sql` creates `eventforge.events` (columns, `events_pkey`, `events_stream_version_uq` and the `CHECK` constraints are described in [architecture.md](architecture.md#4-event-store-design-postgresql)) and grants the runtime role `eventforge_app` only `SELECT` and `INSERT`. Migrations are run explicitly by the owner and never by the API.

```powershell
# Prerequisite: the provisioning script has run. Prompts for the eventforge_migrator password.
psql -h <host> -p <port> -U eventforge_migrator -d eventforge -v ON_ERROR_STOP=1 -f db/migrations/001-create-event-store.sql
```

- **Transactional and re-runnable.** The file runs in one transaction. On a first run it creates the table. On a later run it creates nothing, compares the existing table's columns and constraint definitions with a temporary table built from the same DDL, checks ownership, and re-asserts and verifies the runtime role's privileges (no `UPDATE`, `DELETE`, `TRUNCATE`, schema `CREATE`). Any mismatch raises an error and rolls everything back; drift is never ignored or repaired.
- **No rollback script.** The migration never drops or alters existing objects. To discard a disposable environment, drop that environment.
- **Not covered:** the operator role is untouched; there is no migration history table; the check compares columns and constraints but not extra indexes, triggers or table options.

### Integration tests (isolated PostgreSQL only)

`tests/EventForge.IntegrationTests` contains contract/argument/guard tests that never connect (they run in the normal `dotnet test`) and `PostgreSql`-category tests (store behaviour, migration re-run and drift, runtime privileges). The PostgreSQL tests are **skipped unless explicitly enabled**, and the fixture creates and drops its own roles (`eventforge_migrator`, `eventforge_app`) and a throwaway database, so they must only ever target a dedicated, disposable PostgreSQL 17:

| Variable | Value |
| --- | --- |
| `EVENTFORGE_IT_ENABLED` | `disposable-postgres` |
| `EVENTFORGE_IT_ADMIN_CONNECTION` | Npgsql connection string of a superuser on that server (maintenance database) |

A guard refuses to run when the host is not loopback, the port is 5434 or 5535, the database or user name starts with `eventforge`, the server is not PostgreSQL 17, or the server already has any `eventforge*` database or role. **The guard is a safety net, not proof that a server is disposable**: never point the variables at the owner's PostgreSQL (`127.0.0.1:5434`), the preserved Compose database (`127.0.0.1:5535`) or any shared server. Example for the owner, with a new, empty container on another port (not run by the author):

```powershell
docker run --rm -d --name eventforge-it -p 127.0.0.1:55432:5432 -e POSTGRES_PASSWORD=<throwaway> postgres:17
$env:EVENTFORGE_IT_ENABLED = "disposable-postgres"
$env:EVENTFORGE_IT_ADMIN_CONNECTION = "Host=127.0.0.1;Port=55432;Database=postgres;Username=postgres;Password=<throwaway>"
dotnet test tests/EventForge.IntegrationTests --filter "Category=PostgreSql"
docker stop eventforge-it
```

In CI the `postgres-integration` job does the same with an ephemeral service container and fails unless PostgreSQL tests executed, all passed and none were skipped.

### Known limitations

`CurrentVersion` is `null` for race-based conflicts and duplicate ids (callers reload). The gap check on append compares `count(*)` with `max(stream_version)`. `recorded_at` is one value per append batch. JSONB normalises payload formatting. A commit whose outcome is unknown throws and requires a reload; there is no request idempotency. Versions are 32-bit. Database errors other than the two mapped unique violations propagate as `PostgresException`; its `Detail` (for example on a check violation) can contain row data, so callers must not log whole exceptions, and a payload containing `\u0000` is rejected by JSONB. The drift check does not compare column order. The CI service image `postgres:17` is a floating tag, not digest-pinned. The tests execute the migration through Npgsql as a single multi-statement command; the `psql -f` procedure above is the owner procedure for real databases.

## Task overview migration (EVF-36)

> **Status: implemented and verified locally; committed by the owner (`dcc4799`); GitHub CI has not run for EVF-36.** On November 22, 2026, migrations 001 and 002 were executed through the test fixture in new, disposable PostgreSQL 17.11 containers (the run was made twice, on loopback ports 55432 and 55433, with identical results; both containers have been removed) and all 92 PostgreSQL integration tests passed (56 of them for migration 002), with the Release build at 0 warnings and 0 errors, 254 unit/architecture and 102 non-PostgreSQL integration tests passing, and 0 failed and 0 skipped. **Migration 002 has not been applied to the owner's existing development database (`127.0.0.1:5434`)** or any other existing database; running it there is a separate owner action. EVF-37 (the transactional projector) is implemented and locally verified only: it is not committed, not merged and not verified in remote CI. EVF-38 (projection-backed queries) is not implemented, and EVF-25 as a whole remains in progress. The figures above are the EVF-36 evidence; they were not re-run for EVF-37.

`db/migrations/002-create-task-overview.sql` creates the derived read model `eventforge.task_overview` (`task_id` uuid primary key; `status` created/assigned/completed; nullable `assigned_agent_id`, `current_generation_id` and `latest_test_result` passed/failed; `last_stream_version` positive) with check constraints for the stable Task invariants (a `created` task has no agent, generation or result; an assigned or completed task has an agent; a result needs a generation; a completed task has a passed result; identifiers are 1-100 characters, not blank, without control characters). It has no index besides the primary key. Run it after `001-create-event-store.sql`, as the migration role, in the same way (`psql ... -f db/migrations/002-create-task-overview.sql`); the API never runs it.

- **Runtime role `eventforge_app`:** `SELECT`, `INSERT`, and `UPDATE` only on `status`, `assigned_agent_id`, `current_generation_id`, `latest_test_result` and `last_stream_version`. No `UPDATE(task_id)`, `DELETE`, `TRUNCATE`, ownership or schema `CREATE`; nothing for `PUBLIC`. The migration verifies these effective privileges from the catalog and fails otherwise. The event store table and its privileges are not touched.
- **Re-runnable, never destructive:** like 001, a re-run compares columns, constraints, indexes and triggers with the expected definition, checks ownership and re-asserts privileges; drift raises an error and rolls back. It never repairs drift, replaces data or deletes rows.
- **Not part of EVF-36:** migration 002 itself only creates the table. The inline projector (EVF-37) is implemented and locally verified only: `PostgreSqlEventStore` updates the table in the append transaction for every Task append and is fail-closed (no configuration skips it; a refused projection rolls the events back). The projection-backed state query (EVF-38) is not implemented, so `GET /api/tasks/{id}` still folds the event history.
- **Existing databases (legacy data and cutover):** the migration creates an empty table and does not backfill existing Task streams. Existing Event Store data is never deleted or reset automatically, and the development database must not be reset for this. A database that already holds Task events therefore has no overview rows for them, and **a missing row does not mean the Task does not exist**: the events remain authoritative. With the EVF-37 projector, a command on such a task is refused (missing row) and its event is rolled back; nothing is backfilled. EVF-38 must distinguish a genuinely unknown Task from an existing event stream that lacks a projection row, and must not simply report the latter as not found. Deploying EVF-37/EVF-38 against a database that already contains Task events needs a separately approved compatibility and cutover procedure (for example a fresh database or an approved replay or backfill); that procedure is **not** implemented or designed here.

## Verification

### Reported local verification (not CI evidence)

Reported local verification on the author's Windows machine with Docker Desktop (PostgreSQL 17.12 image, .NET SDK 10.0.404), run on November 19-20, 2026. This is **not CI evidence**; the GitHub Actions workflow builds no containers (CI for EVF-21 separately passed on `main`: 42 tests, 0 failed, 0 skipped, 0 warnings, 0 errors).

| Check | Result |
| --- | --- |
| `dotnet restore`, Release build | 0 warnings, 0 errors |
| Automated tests (Release) | 42 passed, 0 failed, 0 skipped (includes the Application and Simulation Npgsql architecture regressions) |
| Dependency vulnerability audit (`dotnet package list --vulnerable --include-transitive`, 5 projects) | No findings |
| `docker compose config --quiet`, `db` and `full` profiles | Valid |
| Native start without credentials | `/health` returned 200; `--verify-database` failed clearly when unconfigured, invalid or unreachable |
| Isolated Compose PostgreSQL 17.12 first start | Healthy on `127.0.0.1:5535`; `001-provision.sql` created 3 roles, the database and the schema without errors |
| Roles and privileges (catalog queries) | All roles `LOGIN`, no superuser/createdb/createrole/bypassrls/replication, no memberships; migrator owns database and schema; app and operator roles hold `CONNECT` and schema `USAGE` only; 0 tables; app role lacks schema `CREATE` |
| Host-port password authentication | Wrong password rejected (SQLSTATE `28P01`); correct `eventforge_app` credentials accepted |
| Native `--verify-database` against the Compose PostgreSQL (port 5535) | `PASSED`: connect, role unprivileged, schema exists, `USAGE` granted |
| Full-stack image build and start (`--profile full up -d --build`) | Succeeded; API ran as a non-root user |
| API `/health` on `127.0.0.1:8080` | HTTP 200, body `Healthy` |
| `--verify-database` inside the Compose network (`run --rm api`) | `PASSED` (same four checks) |
| Log hygiene | The runtime password did not appear in the API or PostgreSQL logs |
| `docker compose --profile full down` | Containers and network removed; the data volume was preserved |

### Mode 1 verification status

| Check | Status |
| --- | --- |
| `--verify-database` with the runtime role against the owner's existing PostgreSQL 17.11 (`127.0.0.1:5434`) | **Passed** (owner-run, November 20, 2026; not re-run by the author) |
| Visual Studio native API launch, `/health` | **Healthy** (owner-reported) |
| Visual Studio Code native launch | **Not independently verified** |
| Provisioning script re-execution | **Not validated** (see [Database provisioning](#database-provisioning)) |
| CI for EVF-21 | **Verified** on `main` (42 passed, 0 failed, 0 skipped; 0 build warnings and errors) |

### Owner-run checklist for the remaining check

VS Code launch (credentials are not needed for steps 1-2; step 3 needs the user secret already set for Visual Studio):

1. Open the repository folder in VS Code (C# Dev Kit or C# extension installed) and run **EventForge.Api (http://localhost:5080)** from Run and Debug.
2. Request `curl.exe -i http://localhost:5080/health` (expect `200 OK`, `Healthy`) and, optionally, set a breakpoint in `Program.cs`.
3. Run **EventForge.Api: verify database**; expect `PostgreSQL verification: PASSED` and exit code 0.

Do not run `dotnet user-secrets list` in a shared terminal; the connection string never needs to be pasted into logs or issues.
