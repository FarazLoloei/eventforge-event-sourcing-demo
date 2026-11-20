# ADR 0001 — Modular Monolith with Event Sourcing and CQRS

- **Status:** Proposed (implementation in progress; see the README for verified status)
- **Date:** 2026-11-16
- **Decision owner:** Project owner
- **Scope:** EventForge initial architecture and MVP

## Context

EventForge is an independent, noncommercial-scale architectural demonstration of an AI-assisted task workflow. We want to show how historical events, deterministic domain rules, and recoverable read models make a workflow inspectable after a test failure, process restart, or implementation change.

The engineering problem is primarily one of **correctness, auditability, and recovery**, not independent team deployments or high-scale distributed throughput. The first working demonstration must be small enough to build, explain, and test end to end.

## Decision

### 1. Use a Modular Monolith

Deploy a single ASP.NET Core application with explicit logical modules and dependency direction. The **Task Workflow** bounded context owns its commands, events, aggregate behavior, and read-model contract. A simulated agent is an external-facing adapter that submits ordinary workflow commands, not an independently persisted microservice.

Use in-process calls across application boundaries. Do not introduce internal gRPC, a broker, or Kubernetes merely to simulate distribution. If a genuinely separate bounded context becomes necessary, document its lifecycle and invariants before splitting it out.

### 2. Use .NET 10 and C#

Use .NET 10 as the application runtime and C# for domain behavior, HTTP endpoints, and test code. Strong typing, records, explicit contracts, and mature testing tools support maintainable domain models and deterministic reducers. ASP.NET Core and PostgreSQL integration provide a cohesive, testable platform for the small transactional application. A consistent language across domain, API, and tests simplifies maintenance.

The sample will target a supported .NET 10 release and pin the SDK once implementation begins.

### 3. Use PostgreSQL for events and read models

Keep the append-only event log and task overview projection in **one PostgreSQL database**, enabling event append and projection update in a **single local database transaction**. Persist domain event payloads as JSONB with explicit event names and schema versions; use relational uniqueness constraints for stream-version concurrency.

The event store will be purpose-built and small. Do not adopt a generic event-sourcing platform, dedicated event database, or message broker in the MVP without a demonstrated requirement.

### 4. Apply Event Sourcing to the Task aggregate

The authoritative representation of a task's lifecycle is its ordered event stream. Domain decisions are pure or deterministic for known inputs. Supported initial facts are `TaskCreated`, `TaskAssigned`, `CodeGenerated`, `TestsPassed`, `TestsFailed`, and `TaskCompleted`.

Event Sourcing is confined to this history-sensitive workflow; it is **not** mandated for every future table or supporting function. Replays must not invoke simulators or other side effects.

### 5. Apply CQRS with transactional inline projections

Write-side commands load the event stream, rehydrate the aggregate, check invariants, and append valid events. Read-side queries use a `task_overview` projection. The MVP projector executes in the same PostgreSQL transaction as the event append, so a failed projection update also aborts the write.

The initial rebuild procedure runs **with writes paused** and verifies a replacement projection before cutover. This is not a claim of online or zero-downtime rebuilding. Explicit schema upcasters, deterministic projection diffing, and projection recovery are MVP requirements. Healthy verification requires equivalence at the same history boundary; recovery validates against authoritative events and independent reference expectations, without requiring equality with a damaged projection. Online catch-up, shadow swaps under load, snapshots, and durable request idempotency remain later work.

### 6. Enforce baseline optimistic concurrency

Commands provide an expected stream version. A unique database constraint on `(stream_type, stream_id, stream_version)` protects against concurrent duplicate versions. Conflict responses are explicit and no partial transaction should commit. Request-level idempotency is a separate future guarantee: the initial API may not safely deduplicate a command after a response is lost.

### 7. Include architecture tests and engineering guardrails in the MVP

Introduce dependency tests and build/test gates incrementally with implementation. Require synthetic fixtures, secret hygiene, explicit module contracts, and human review for all contributions. Controlled replay must bypass side-effect dispatch and report stable row/field differences. Event branching, isolated preview environments, and controlled external side effects remain deferred.

Planned CI quality gates under roadmap tasks 11–12 will verify the design incrementally; no enforcement is claimed. The [architecture security guidance](../architecture.md#security-boundaries-planned) clarifies planned least-privilege runtime/maintenance access, validation and authorization boundaries, protected configuration, and safe structured diagnostics. These controls preserve domain isolation and transactional consistency without selecting an identity provider or observability platform.

## Consequences

**Positive**

- One deployable process and database reduce operational work and make transactional consistency easier to explain.
- An append-only event history supports reconstruction and root-cause inspection.
- Explicit domain transitions prevent an agent or HTTP adapter from bypassing business rules.
- Transactional projections simplify the first consistency model and keep the demo reproducible.
- The architecture and tests can be understood without knowing any particular AI vendor or infrastructure platform.

**Costs and limitations**

- Event contracts require deliberate versioning and migration discipline.
- CQRS introduces separate write and read code plus a projection rebuild procedure.
- Projection work on the write path increases command latency and couples write throughput to read-model updates.
- A single-process deployment does not provide independent scaling or fault isolation for future modules.
- Offline replay temporarily restricts writes; real online rebuilds need stronger cursor/catch-up semantics.
- Event Sourcing is more complex than CRUD and must justify itself through workflow history and recovery needs.

## Alternatives considered

| Alternative | Why not selected for MVP |
| --- | --- |
| **Microservices + event broker** | Introduces network failure modes, delivery semantics, distributed operations, and deployment overhead without a present requirement. |
| **CRUD-only task table** | Simpler initially, but does not preserve the complete sequence of workflow facts or demonstrate replay from an authoritative event history. |
| **Asynchronous projection consumer** | Useful for independent scaling, but adds lag, retry handling, and a delivery mechanism when the task overview can be updated atomically in PostgreSQL. |
| **Dedicated Event Store product/framework** | Could reduce infrastructure code, but obscures the mechanics this educational demonstration intends to make inspectable. |
| **Advisory locks for every write** | Additional locking is unnecessary for baseline aggregate-stream correctness when expected versions and unique constraints are enforced. Revisit only for a concrete contention requirement. |

## Risks and follow-up decisions

1. **Contract evolution:** Deliver explicit, deterministic upcasters and historical/current compatibility fixtures in the MVP (roadmap task 8); never rewrite historical rows during replay.
2. **Replay while live traffic continues:** Do not rely on sequence IDs as commit-order cursors; a future online projection rebuild requires a separate ADR and commit-safe catch-up approach.
3. **Side effects:** Real code-generation and test execution would require isolated execution, explicit authorization, and controlled side-effect boundaries; these are outside the MVP.
4. **Request retries:** Implement and test durable command idempotency before claiming safe repeat submissions after unknown outcomes.
5. **Module growth:** Split domain ownership only when separate invariants and release/operational needs justify it.
6. **Security and exposure:** Review identity, permissions, deployment exposure, secret handling, diagnostic retention, and database grants before shared/network deployment. Runtime credentials must not grant migration/cutover authority. Controls remain planned until implemented and verified.
7. **Conditional infrastructure:** Advisory transaction locks require a demonstrated coordination need and owner review; retain optimistic concurrency and atomic projections. Multi-tenancy/RLS and reliable external publication/Transactional Outbox require separate owner-approved designs and are not MVP requirements. Replay must remain free of external publication effects; no monitoring service or broker is adopted.

## Independence and source hygiene

All design examples, event contracts, and future code will be **original and synthetic**. No proprietary source, employer-specific schema, private operational history, confidential task data, or internal engineering manuals will be published or reconstructed. This ADR is a proposal for this repository alone.

## Decision history

- **2026-11-16:** Initial proposed decision; retained as the original decision date. The original scope deferred upcasting and treated controlled rebuild equivalence as the initial recovery check.
- **2026-11-17 - EVF-3 revision:** Promote explicit upcasting, controlled replay/diffing, projection recovery, architecture tests, and engineering guardrails to MVP. Separate healthy verification from damaged-projection recovery. Retain offline rebuilding and defer snapshots, durable request idempotency, online rebuilding, branching, isolated previews, and controlled side effects. Clarify the engineering suitability of C#/.NET. The decision remains proposed; implementation has not started.
- **2026-11-17 - EVF-9 clarification:** Document planned security boundaries, safe diagnostics, and proportionate observability; reference EVF-5 for future quality gates. Clarify conditional advisory locking, RLS, and external publication/outbox considerations without adopting those capabilities or changing the established architecture. Prior decision history and proposed/unimplemented status remain intact.
- **2026-11-19 - EVF-18 clarification:** Record the AI-assisted engineering setup delivered by PR #6: Claude Code project instructions (CLAUDE.md and AGENTS.md governance), project-level permissions, MCP boundaries (read-only use limited to this repository and Jira project EVF), and three engineering skills (clean-architecture, event-sourcing, quality-review). These are process guardrails for authorized work and do not change the architecture, module ownership, event contracts, or consistency guarantees. The MCP repository and Jira project boundaries are instruction-based; they are not server-side access restrictions, and the Jira OAuth authorization remains read-write. Runtime enforcement of the configured permissions has not been verified; the settings and instructions are documented policy, not demonstrated automation. The original architectural decisions, prior history, and Proposed status remain intact; implementation has not started.

- **2026-11-19 - EVF-21 provider decision:** Adopt **Npgsql 10.0.3** with direct parameterized SQL and no ORM as the PostgreSQL provider, confined to `EventForge.Infrastructure`, with one `NpgsqlDataSource` per process (singleton, disposed by the container, created lazily so the host starts without credentials). *Rationale:* explicit transaction and expected-version control for the append-plus-inline-projection write path, and a small, inspectable dependency surface. *Alternatives considered:* Entity Framework Core (hides SQL and transaction boundaries the demonstration must make visible, larger surface), Dapper (adds a dependency without removing the need for Npgsql), `Npgsql.DependencyInjection` (an extra package for registration the composition root performs in one line). *Provenance review:* NuGet.org repository-signed package, official `npgsql/npgsql` repository, PostgreSQL license, only Microsoft transitive packages, no known vulnerabilities and no deprecation at review time (`dotnet package list --vulnerable`). *Consequences:* the architecture checker now loads the single approved `Npgsql.dll` as metadata (fail-closed; a test keeps its version equal to the Infrastructure reference); Npgsql is forbidden in Domain, Application and Simulation and permitted only in Infrastructure. Configuration uses `ConnectionStrings:EventForge` through user secrets or environment variables; runtime, migration and maintenance roles stay separate and no table privileges, tables or startup migrations are introduced. Optional Docker and provisioning files are development aids, not production deployment. Provisioning, authentication, the Compose stack and the API container were verified once locally in an isolated stack (an author-reported local result, not CI evidence). The owner-run runtime-role check against the existing PostgreSQL 17.11 passed and a Visual Studio launch was owner-reported healthy; the VS Code launch, a second provisioning run and pull-request CI are not yet verified. Event contracts, module ownership, consistency guarantees and replay strategy are unchanged. Owner authorization of the provider decision was given by the EVF-21 task prompt; the ADR status remains Proposed. *2026-11-20 - execution-mode clarification (owner-approved development and evaluation strategy, not a production deployment decision):* two supported modes share the same application code and configuration model, with environment-specific values supplied only through standard ASP.NET Core configuration overrides (`ConnectionStrings__EventForge`, `ASPNETCORE_ENVIRONMENT`). **Mode 1, local development (default for the project owner):** the API runs natively (Visual Studio, VS Code or `dotnet run`, preserving debugging, breakpoints and hot reload where supported) against the owner's existing PostgreSQL Docker container at `127.0.0.1:5434`, database `eventforge`; the API is not required to run in Docker, and unrelated projects, databases and existing infrastructure are never modified. **Mode 2, company evaluation / fresh clone:** reviewers run the whole application with `docker compose --profile full up -d --build`, which starts Compose-managed PostgreSQL (host port `5535`) and the API (host port `8080`), both bound to loopback by default, after creating a local git-ignored `.env`; no Visual Studio or existing PostgreSQL installation is needed. Docker support is optional for development and a supported evaluation path. No separate implementation or business logic exists for Docker, and secrets stay out of source control and logs. The Mode 2 flow was run once locally (author-reported, not CI); it has not been run on another machine. Architecture, event contracts, module ownership, consistency guarantees and replay strategy are unchanged, and the earlier decision and history remain intact.
- **2026-11-20 - EVF-22 implementation and verification (no architectural change):** Task 3 delivered the PostgreSQL Event Store as already proposed: Application-owned `IEventStore` contracts, a Npgsql adapter in `EventForge.Infrastructure`, and migration `db/migrations/001-create-event-store.sql` creating `eventforge.events` (primary key on `event_id`, unique `(stream_type, stream_id, stream_version)`, whose index also serves ordered loading) with `SELECT`/`INSERT` only for the runtime role. Append uses expected versions in one `READ COMMITTED` transaction with the unique constraint as the final race safeguard, no advisory locks and no retries; stale writers receive an explicit conflict result. *Evidence:* merged in [PR #16](https://github.com/FarazLoloei/eventforge-event-sourcing-demo/pull/16); PR CI ([run 44706915605](https://github.com/FarazLoloei/eventforge-event-sourcing-demo/actions/runs/44706915605)) and post-merge `main` CI ([run 44707030609](https://github.com/FarazLoloei/eventforge-event-sourcing-demo/actions/runs/44707030609)) passed 42 unit/architecture tests, 21 non-database tests and 15 PostgreSQL 17 integration tests with zero failures and zero skips; the migration (first run and re-run) and event store behavior were verified against an isolated disposable PostgreSQL. *Limits:* the migration has not been executed against the owner's existing development database (`127.0.0.1:5434`); the API workflow, Task aggregate and events, inline projections, replay and recovery, and upcasting remain unimplemented, so the MVP is not complete. Technology, module ownership, event contracts, consistency guarantees and replay strategy are unchanged, and the original decisions and prior history remain intact; the ADR status remains Proposed.
- **2026-11-20 - EVF-30 first persisted Task event contract (no architectural change):** Task 4 subtask EVF-30 connected the pure Task aggregate to the Event Store through an Application command handler and records the first persisted **schema version 1** contract for the six approved events, using stream type `Task` and minimal camelCase JSON-object payloads: `TaskCreated {taskId}`, `TaskAssigned {agentId}`, `CodeGenerated {generationId, artifactRef}`, `TestsPassed {generationId}`, `TestsFailed {generationId}` and `TaskCompleted {}`. *Strict deserialization policy:* reads fail closed. Every stored event must belong to the requested stream, `TaskCreated.taskId` must be the canonical lowercase GUID of that stream, and unknown event types, other schema versions, non-object payloads, case-mismatched, missing, extra, null or non-string properties and duplicate properties are rejected as invalid history. An additive or breaking payload change requires a new schema version and an explicit upcaster (Task 8); none exists. Duplicate JSON keys are detectable only while a payload still contains them, because PostgreSQL JSONB keeps the last duplicate key. Event ids and metadata are supplied outside the pure aggregate, and `recordedAt` stays store-assigned. The handler checks the caller's expected version before deciding, appends the decided events in one Event Store call (one PostgreSQL transaction, as in EVF-22), and returns conflicts, duplicate event ids, invalid history and unknown commit outcomes as explicit results without retrying; this is not durable request idempotency. *Evidence:* local Release build, unit/architecture tests and PostgreSQL integration tests against a disposable PostgreSQL 17 container (author-reported local run); pull-request CI for EVF-30 has not run. Technology, module ownership, consistency guarantees and the replay strategy are unchanged, and the original decisions and prior history remain intact; the ADR status remains Proposed.

## References within the repository

- [Project README](../../README.md)
- [Architecture proposal](../architecture.md)
- [Delivery roadmap](../roadmap.md)
