# EventForge — Architecture Proposal

> **Status: Proposed / Not implemented**  
> This document specifies an intended architecture. It does not describe a running system. Decisions are deliberately scoped to a small, auditable proof of concept.

## 1. Goals, scope, and constraints

EventForge models a task lifecycle in an AI-assisted software factory. The objective is to demonstrate **correct, deterministic workflow state transitions** and **recoverable projections** when actions are performed by a simulated agent.

**Architecture goals**

1. Preserve an immutable record of task lifecycle decisions.
2. Enforce domain invariants at the write boundary.
3. Make an immediately readable task overview available after successful writes.
4. Reproduce aggregate state and query projections from historical events.
5. Detect conflicting writes and make failures explicit.
6. Keep the initial deployment and developer environment simple.

**MVP constraints**

- One deployable ASP.NET Core process and one PostgreSQL database.
- A single main bounded context: **Task Workflow**.
- Simulated agent/test behavior; no model API calls or external code execution.
- No broker, orchestration cluster, cross-service transactions, or generic workflow engine.
- Replay in a controlled environment where normal writes are paused. Online rebuilds are a later design problem.

## 2. Modular Monolith and ownership boundaries

```mermaid
flowchart TB
    subgraph Process[Single .NET process]
        API[API adapter / HTTP endpoints]
        Simulator[Execution simulation adapter]
        subgraph Workflow[Task Workflow — domain boundary]
            Handlers[Command & query handlers]
            Aggregate[Task aggregate + pure decision/apply logic]
            Projection[Task overview projector]
        end
        Infra[PostgreSQL event-store adapter]
    end
    API --> Handlers
    Simulator --> Handlers
    Handlers --> Aggregate
    Handlers --> Projection
    Handlers --> Infra
    Infra --> PG[(PostgreSQL: event streams + read model)]
    Projection --> PG
```

### Ownership rules

| Logical component | Owns | Must not do |
| --- | --- | --- |
| **Task Workflow** | Commands, event contracts, aggregate invariants, workflow read-model shape | Call an LLM, run generated code, or depend on HTTP/SQL details inside the domain |
| **Execution Simulation** | Synthetic agent actions and predefined test outcomes | Write directly to the event store or bypass workflow commands |
| **Persistence infrastructure** | Database transactions, event serialization, concurrency enforcement | Decide domain transitions |
| **API adapter** | Request/response contracts, status codes, routing | Duplicate aggregate rules or update read-model tables directly |

`Task Workflow` is the bounded context; `Execution Simulation` is intentionally an adapter, **not** an artificially independent bounded context. Read models remain owned by the Workflow domain while their storage implementation stays in infrastructure. Modules will communicate through explicit application contracts rather than through one another's tables or implementation types.

### Future code organization (not yet created)

```text
src/
  EventForge.Api/              # HTTP composition root and REST endpoints
  EventForge.Workflow/         # Commands, domain events, aggregate, projection contracts
  EventForge.Infrastructure/   # PostgreSQL event store and transaction adapters
  EventForge.Simulation/       # Simulated agent outcome provider

tests/
  EventForge.UnitTests/
  EventForge.IntegrationTests/
```

This is a possible implementation layout, not a requirement to create four assemblies immediately. Assembly boundaries should be introduced only where they preserve meaningful dependency direction.

## 3. Domain model and event contract

### Task aggregate

The `Task` aggregate is the write-side consistency boundary. It accepts explicit commands and emits events only when its invariants permit a transition.

| Command | Proposed event | Rule |
| --- | --- | --- |
| `CreateTask` | `TaskCreated` | A task identifier must not already exist. |
| `AssignTask` | `TaskAssigned` | Task exists and is not yet assigned or completed. |
| `RecordCodeGenerated` | `CodeGenerated` | Task exists, is assigned, and is not completed; `generationId` must be new within this task. |
| `RecordTestsPassed` | `TestsPassed` | Task exists and is not completed; outcome identifies the **current**, previously recorded generation. |
| `RecordTestsFailed` | `TestsFailed` | Task exists and is not completed; outcome identifies the **current**, previously recorded generation. |
| `CompleteTask` | `TaskCompleted` | Task exists, is assigned, and is not completed; the latest test outcome for its current generation is passed. |

Completion is terminal: after `TaskCompleted`, reject every workflow command, including repeated completion, assignment, generation, and test-result commands. Queries remain available. Assignment requires an existing, unassigned, incomplete task; generation requires that assignment first. No reassignment or reopening command is defined.

Generation identifiers are opaque and unique within a task, including superseded generations. Reusing an identifier is invalid even if the artifact reference differs. The aggregate reconstructs the set of used identifiers from history. Reject test results before any generation exists, for unknown identifiers, or for a superseded generation.

A later `CodeGenerated` event makes its generation current and resets the current test outcome to absent; earlier outcomes remain immutable history. Therefore, `TestsPassed(g1)` followed by `CodeGenerated(g2)` cannot authorize completion until `TestsPassed(g2)` is recorded. Completion requires the latest outcome to be passed, not merely a pass somewhere in history.

Commands that violate a domain rule return a validation/conflict result and append **no events**. The same semantic decision should be testable without PostgreSQL or HTTP.

These invariant clarifications are requested by EVF-8. Any further domain policy, such as restricting repeated test results for the current generation or adding reassignment/reopening, requires explicit owner approval and an authorized ADR before adoption. This task does not introduce those policies.

### Illustrative event envelope

This is a **planned contract example**, not a sample extracted from any real system or an existing table:

```json
{
  "eventId": "e8643c80-7949-4f15-99ec-0d31c28d21c5",
  "streamId": "9d56311e-acda-47c9-8a1c-f8e5d5605f4c",
  "streamType": "Task",
  "streamVersion": 3,
  "eventType": "CodeGenerated",
  "schemaVersion": 1,
  "payload": {
    "generationId": "g2",
    "artifactRef": "synthetic://artifacts/task-123/g2"
  },
  "metadata": {
    "correlationId": "demo-run-001",
    "actorType": "SimulatedAgent"
  },
  "recordedAt": "2026-11-16T12:00:00Z"
}
```

The stable `eventType` and numeric `schemaVersion` identify a logical event contract. `recordedAt` is a persisted fact, **not** something recomputed during replay. Event payloads contain synthetic references, not embedded source trees or secrets.

## 4. Event Store design (PostgreSQL)

### Proposed minimal storage schema

| Column | Intent |
| --- | --- |
| `event_id UUID` | Unique immutable identity for each event. |
| `stream_type TEXT` | Aggregate type, initially `Task`. |
| `stream_id UUID` | Task aggregate identifier. |
| `stream_version INTEGER` | Contiguous position within the aggregate stream, starting at 1. |
| `event_type TEXT` | Stable event name. |
| `schema_version INTEGER` | Serialization contract version. |
| `payload JSONB` | Versioned event body. |
| `metadata JSONB` | Causation/correlation information and actor type. |
| `recorded_at TIMESTAMPTZ` | Server-assigned or explicitly persisted recording time. |

**Required constraints:** primary key on `event_id`; unique constraint on `(stream_type, stream_id, stream_version)`; supporting index for loading one ordered stream. A JSONB payload is flexible but does **not** replace explicit contract validation and versioned deserialization.

### Expected-version semantics

- `LoadStream(taskId)` returns events in ascending `stream_version`.
- A command declares `expectedVersion` (0 for a new stream).
- If the current stream version differs, the append is rejected. The API will map this to **HTTP 409 Conflict**.
- A unique database constraint on the stream/version tuple is the final write-race safeguard, even if two writers observed the same old version.
- For a multi-event command, all appended versions and projection changes succeed or fail **as one PostgreSQL transaction**.

Append assigns contiguous versions starting at expectedVersion + 1. Stale commands are rejected without automatic retries: callers must reload and reassess intent. Stream concurrency does not provide durable request deduplication.

Optimistic concurrency answers whether the stream has changed since the caller's expected version. Durable request idempotency would recognize the same request across retries and return its recorded result without executing it again. A retry after a lost response may encounter a version conflict or domain rejection; neither establishes the original request's outcome. Generation-ID uniqueness is a domain invariant, not request deduplication. Durable request idempotency remains deferred.

The initial design relies on **optimistic concurrency and unique constraints**, not mandatory advisory locks. Advisory transaction locks are a possible later optimization if contention or ordering requires them; they are not needed to make the initial single-stream append safe.

If measured hot-stream contention or an approved coordination requirement justifies advisory locks, prefer transaction-scoped locks that release at commit/rollback. All participating writers must use the same stable lock-key mapping and acquisition order, with bounded waits and deadlock handling. Acquire before the protected read/decision within that transaction; a lock acquired after deciding cannot make stale state current. Advisory locks are cooperative: retain expected-version checks, uniqueness constraints, and atomic event/projection writes. Do not silently retry domain decisions or treat locks as request idempotency. Adoption requires owner review; session-scoped locks are unsuitable as a default for pooled connections. See [PostgreSQL advisory locks](https://www.postgresql.org/docs/current/explicit-locking.html#ADVISORY-LOCKS).

## 5. CQRS flows

### Write path: commit the event and read model together

```mermaid
sequenceDiagram
    participant C as API Client / Simulator
    participant A as Command Handler
    participant D as Task Aggregate
    participant P as PostgreSQL
    participant R as Inline Projector
    C->>A: Command + expectedVersion
    A->>P: Load ordered task events
    P-->>A: Existing event stream
    A->>D: Rehydrate state; decide command
    D-->>A: New domain event(s) or rejection
    A->>P: BEGIN TRANSACTION
    A->>P: Append events with stream-version constraint
    A->>R: Apply new events to task overview
    R->>P: Upsert read model in same transaction
    A->>P: COMMIT (or ROLLBACK on any error)
    A-->>C: Success / domain error / HTTP 409
```

Domain logic is conceptually `Decide(state, command) -> events | error` and `Apply(state, event) -> state`. Both are pure and deterministic for fixed inputs. Decide validates current intent; Apply folds historical facts without re-running command validation against current rules. Neither reads ambient state or performs I/O. Values that would otherwise be nondeterministic (clock reads, random IDs, external test results) are supplied at the command boundary and recorded in events.

**Atomicity invariant:** in the MVP, a successful event append and its corresponding task overview update are part of the same local transaction. A projection failure aborts the event append. This provides immediately consistent **local** reads without a message broker. It does not imply distributed transaction guarantees.

### Read path

`GET /tasks/{id}` (planned) queries the `task_overview` projection; it does not deserialize the full event stream on every request. `GET /tasks/{id}/events` (planned, for inspection/demo) exposes safe, synthetic event metadata. Public API shape remains provisional until the API task is implemented.

A proposed `task_overview` row contains `task_id`, `status`, `assigned_agent_id`, `current_generation_id`, `latest_test_result`, and `last_stream_version`. The projector is a deterministic reducer. A version guard requires the next contiguous stream version and rejects duplicates or gaps, rolling back the transaction. Rebuilds start each stream at version zero. Task-local projections require no ordering across streams. Success is returned only after commit; immediately consistent reads assume the primary database, not a lagging replica.

## 6. Replay, rebuilding, and schema evolution

### MVP: controlled offline projection rebuild

1. Pause all command writers and drain in-flight transactions. Keep writes paused through validation and cutover.
2. Create an empty shadow projection. Record the projector/upcaster versions and authoritative stream inventory with terminal stream versions.
3. Upcast supported contracts and replay each stream in contiguous stream-version order. Reject gaps, unknown contracts, malformed events, or reducer failures.
4. Validate the candidate against history: one row per task stream, no extra rows, matching terminal versions, and expected status, assignment, generation, and test outcome derived from recorded facts. Independent reference folds and known-history fixtures must test these expectations; repeating the same projector alone cannot detect its own logic defects.
5. Produce a deterministic diff and apply the scenario criteria below. Cut over only after candidate validation succeeds, using an atomic table/view switch and retaining the previous table for rollback.
6. Check that command-side projection updates and queries target the replacement before resuming writes. On failure, leave the candidate inactive and report diagnostics. A damaged baseline remains unavailable for trusted queries until recovery succeeds.

**Verification:** For a healthy projection, compare the candidate against the baseline at the same source boundary and projection contract. Require zero semantic differences and successful history validation. This demonstrates equivalence for the tested history.

**Recovery:** For a damaged projection, report discrepancies against the baseline, then validate the replacement against authoritative history and reference expectations before cutover. Equality with the corrupted projection is not a recovery condition. If history itself is damaged or unsupported, stop: rebuilding a projection cannot repair the source of truth.

### Deterministic projection diffing

Compare the complete key set sorted by task_id and these fields: status, assigned_agent_id, current_generation_id, latest_test_result, and last_stream_version. Use canonical UUID/text representations, ordinal comparisons, explicit nulls, and stable field/report ordering. Exclude only operational metadata such as rebuild timestamps, never business fields. Report missing/extra rows and per-field before/after values; counts or hashes alone are insufficient. Repeating the diff on identical inputs must produce identical output. Projection contract changes require an explicit comparison mapping before equivalence checks.

**Replay never triggers real-world effects.** Historical events update state and projections only; replay never calls agents, regenerates artifacts, reruns tests, sends notifications, or dispatches external work. A dedicated replay path excludes side-effect handlers and is tested using adapters that fail if invoked. Maintenance operations such as cutover are outside event application.

### Future: replay under active load

An online rebuild would need shadow projections, a **commit-safe** method to capture and catch up with live events, validation before a short cutover, and a rollback strategy. A PostgreSQL sequence/identity value is **not necessarily transaction commit order**: naive “process every event with ID greater than watermark” logic can miss a late-committing transaction. The MVP deliberately avoids claiming to solve this problem.

### Event contract changes

Every stored event has `eventType` and `schemaVersion` from the start. In the MVP, explicit **upcasters** will transform old serialized contracts to a canonical in-memory representation without rewriting historical event rows. Contract fixtures and compatibility tests will verify both historical and current versions. Upcasters are pure, deterministic transformations explicitly chained by event type and schema version. Unknown versions or missing steps fail closed. Historical rows remain unchanged. Old/current fixtures must yield equivalent aggregate state and projections. Implementation has not started.

#### Synthetic example: `CodeGenerated` v1 to v2

This illustrative schema evolution renames `artifactRef` to `artifactReference` without changing its meaning. It is a proposed compatibility example, not an existing migration or an adopted production contract. The v1 envelope in section 3 remains a valid historical example.

Stored v1 contract (identity and other envelope fields omitted here only for brevity):

```json
{
  "eventType": "CodeGenerated",
  "schemaVersion": 1,
  "payload": {
    "generationId": "g2",
    "artifactRef": "synthetic://artifacts/task-123/g2"
  }
}
```

Canonical v2 representation in memory:

```json
{
  "eventType": "CodeGenerated",
  "schemaVersion": 2,
  "payload": {
    "generationId": "g2",
    "artifactReference": "synthetic://artifacts/task-123/g2"
  }
}
```

The pure transformation copies `generationId` unchanged and maps `artifactReference := v1.payload.artifactRef` exactly. It changes only the in-memory schema version and field name, preserving event ID, stream identity/version, metadata, and recorded time. It performs no I/O, clock reads, random generation, artifact lookup, or mutation of its input. Identical valid inputs produce identical outputs. Stored v1 bytes remain unchanged; upcasting never appends a replacement event.

Validate the declared source shape before conversion and the target shape afterward. Missing or malformed required fields fail explicitly; never generate a missing generation ID, infer an artifact, or add an unrecorded timestamp, agent, or test outcome. A future required fact unavailable in old history needs a separately approved compatibility design; an upcaster cannot invent it. In this example the supported reader accepts v1 through the explicit v1-to-v2 step and validates v2 directly. Reject every other version, unknown event type, or missing chain step with event identity and version diagnostics; stop load/replay and leave any rebuild candidate inactive.

**Fixture equivalence (planned):** Pair a complete synthetic v1 history containing this event with an otherwise identical history using its v2 representation, preserving envelope identities and stream positions. Both must normalize to the same canonical events and fold to identical aggregate state (including used generation IDs and current test outcome) and all task overview business fields and final stream versions. Check against independently authored expected results at matching history boundaries, including after `g2` clears a prior `g1` pass and after a `g2` result permits completion. Fixtures must also show deterministic repeated conversion, unchanged source data, and explicit failure for malformed payloads, unsupported versions, and missing upcaster steps. These are acceptance requirements, not implemented tests.

### Snapshots

Snapshots are a **planned optimization** for aggregate hydration: `(stream_id, last_applied_stream_version, serialized_state, snapshot_schema_version)`. They must never become the source of truth, and a bad or old snapshot must be bypassable by replaying events. Snapshot implementation is deferred until correctness is demonstrated without it.

## 7. Reliability and failure behavior

| Failure case | Intended behavior |
| --- | --- |
| Invalid state transition | Return a domain error; write nothing. |
| Concurrent appends to same stream/version | Unique constraint rejects the loser; whole transaction rolls back; return conflict. |
| Projection write fails | Roll back event append and projection changes together. |
| API crashes before commit | No partial committed command. |
| API crashes after commit but before response | Caller may be uncertain; later request-id idempotency will address safe retries. |
| Old event contract | MVP upcaster reads the supported historical version; incompatible changes must fail explicitly. |
| Replay encounters malformed event | Stop with diagnostic details; do not silently skip or mutate the authoritative log. |

**MVP baseline:** optimistic concurrency and transaction rollback. **Later hardening:** durable idempotency keys, duplicate-request result caching, tested retries, and online rebuild/cutover. The API must not claim effectively-once command behavior before idempotency has been implemented and tested.

### Security boundaries (planned)

- **Database privileges:** Separate runtime credentials from schema-owner/migration and controlled replay/cutover credentials. The runtime role must not own tables, be superuser, have `BYPASSRLS`, create roles/databases, or inherit administrative roles. Grant only required connection/schema access, event `SELECT`/`INSERT`, and projection read/write privileges; deny event `UPDATE`, `DELETE`, `TRUNCATE`, and DDL. Review `PUBLIC` and default grants. Maintenance privileges are explicit and unavailable through normal requests; replay reads history without changing it. Privileges reduce accidental damage but do not enforce aggregate semantics against a compromised writer. See [PostgreSQL privileges](https://www.postgresql.org/docs/current/ddl-priv.html).
- **Validation and authorization:** HTTP adapters validate shape, size, identifier formats, and allowed values; application entry points enforce the caller's permission for each command, query, and event-history inspection before access. Deny access without an explicit applicable policy. The simulator uses the same command boundary and has no privileged bypass. The aggregate independently enforces domain invariants; persistence uses parameterized SQL and validates stored event contracts. Replay/cutover is a separate operator capability, never an ordinary task command. Caller-supplied actor metadata is not proof of identity. Identity mechanism, permissions, and deployment exposure remain owner-reviewed design work before any shared/network deployment; no authentication system is claimed today.
- **Secrets and data:** Supply credentials through protected configuration outside source control (local developer secrets or environment configuration; deployment secret storage chosen when needed). Do not embed them in command lines, examples, events, artifacts, or logs. Keep fixtures and event facts synthetic; artifact references must not contain credentials, personal data, or confidential source. Restrict access to database backups, projection diffs, and diagnostics as well as live data. Review rotation and encrypted transport/storage needs for the actual deployment; no secret-management service or production security guarantee is adopted here.

### Observability and safe diagnostics (planned)

Use structured logs with stable operation/result codes, safe correlation IDs, stream/event identifiers and versions where appropriate, elapsed time, and transaction outcome. Distinguish validation rejection, authorization denial, version conflict, rollback, unsupported history, replay validation, and cutover outcomes. Record committed success only after commit. Bound and sanitize untrusted diagnostic fields; exclude credentials, connection strings, raw request/event payloads, generated source, and SQL parameter values. Public errors expose a safe code and correlation ID; restricted operator diagnostics may include exception details after redaction, never raw database errors in HTTP responses.

Observe command latency, conflict/rollback counts, projection failures, and replay progress/diff totals using in-process instrumentation and local structured output initially. Deterministic business diffs remain separate from operational timestamps and timings. Protect diagnostic access and define retention before deployment. A collector, dashboard service, distributed tracing backend, or broker is not an MVP prerequisite; select additional tooling only for an approved operational need.

### Conditional future isolation and publication

**Row-Level Security (RLS):** PostgreSQL policies can restrict visible rows and permitted writes in addition to table grants; enabled RLS with no applicable policy defaults to deny. Superusers and `BYPASSRLS` roles bypass it, and owners normally do unless forced under RLS. RLS does not protect whole-table operations such as `TRUNCATE`. It could support future tenant isolation, but cannot replace application authorization. Any tenant design must define trusted tenant identity, event/projection ownership, transaction-local context safe under connection pooling, and cross-tenant denial tests covering queries, writes, replay, and maintenance. Multi-tenancy, tenant columns, and RLS policies are **not MVP requirements**; adoption needs a separate owner-approved design. See [PostgreSQL row security](https://www.postgresql.org/docs/current/ddl-rowsecurity.html).

**Transactional Outbox:** Only if reliable external event publication becomes necessary, evaluate storing publication intent in the same transaction as events and inline projections, then relaying committed messages. Duplicate delivery remains possible, so consumer deduplication, ordering, retries, and monitoring need explicit contracts. Replay must not recreate publication intent or dispatch messages. No outbox, relay, broker, or external publication is required for the MVP; a separate owner-approved design must justify them. See the [Transactional Outbox pattern](https://microservices.io/patterns/data/transactional-outbox.html).

## 8. Architectural trade-offs

| Choice | Advantage | Cost / limitation |
| --- | --- | --- |
| Modular Monolith | Easy local reasoning, direct calls, one deployable unit | Must enforce module boundaries deliberately |
| Event Sourcing for task workflow | Complete task history, replay, auditable transitions | More complex schema evolution and debugging than plain CRUD |
| CQRS read projection | Fast task-list queries, explicit read contracts | Additional projector code and rebuild procedure |
| PostgreSQL inline projections | Atomic local write/read consistency; no broker required | Projection work increases command latency; coupled database deployment |
| Optimistic concurrency | Simple and safe stream version enforcement | Conflicts require caller retries or user-visible handling |
| Simulated agent | Reproducible and safe tests | Does not prove live AI integration or sandbox security |
| Offline initial rebuild | Straightforward, demonstrably correct first step | Requires a write pause; not an online migration solution |

Event Sourcing is intentionally applied to a **workflow with meaningful history**, not to every table indiscriminately. If future requirements demand independent scaling, external integrations, or cross-module workflows, those decisions should be justified with measured evidence and separate ADRs.

## 9. MVP quality gates and engineering guardrails

The implementation will introduce unit tests for pure domain rules, PostgreSQL integration tests for transactions/concurrency, replay equivalence tests, and CI validation. MVP architecture tests enforce domain independence from HTTP, persistence, and simulation, plus allowed module dependencies. MVP engineering guardrails require build/test checks, synthetic fixtures, no secrets in events, and human review, including agent-produced changes. Tests and checks land alongside each capability without requiring real-agent integration. Automated agents will not be authorized to bypass domain commands or directly modify the event log.

EVF-5 is the reference for future CI and automated quality gates (roadmap tasks 11–12), not evidence of existing enforcement. As implementation is authorized, verification must cover denied database operations, rejected unauthorized/invalid requests with no appended events, and redacted diagnostics alongside existing domain, concurrency, and replay checks. Human review remains required; no pipeline or security controls are implemented by this proposal.

## 10. Related documents

- [README](../README.md)
- [Implementation roadmap](roadmap.md)
- [ADR 0001 — Architectural foundation](adr/0001-architecture.md)

The technology choices and constraints in this document are **proposals pending implementation and verification**, not benchmark results or evidence of a deployed system.
