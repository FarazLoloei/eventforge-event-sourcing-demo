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
| `RecordCodeGenerated` | `CodeGenerated` | Task is assigned and not completed; associates an opaque `generationId`. |
| `RecordTestsPassed` | `TestsPassed` | Outcome refers to the **current** generation. |
| `RecordTestsFailed` | `TestsFailed` | Outcome refers to the **current** generation. |
| `CompleteTask` | `TaskCompleted` | The current generation has passed tests and task is not already completed. |

A later `CodeGenerated` event supersedes any prior test outcome. Therefore, a `TestsPassed(g1)` followed by `CodeGenerated(g2)` cannot authorize completion until `TestsPassed(g2)` is recorded.

Commands that violate a domain rule return a validation/conflict result and append **no events**. The same semantic decision should be testable without PostgreSQL or HTTP.

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

The initial design relies on **optimistic concurrency and unique constraints**, not mandatory advisory locks. Advisory transaction locks are a possible later optimization if contention or ordering requires them; they are not needed to make the initial single-stream append safe.

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

Domain logic is conceptually `Decide(state, command) -> events | error` and `Apply(state, event) -> state`. Both are deterministic for fixed inputs. Values that would otherwise be nondeterministic (clock reads, random IDs, external test results) are supplied at the command boundary and recorded in events.

**Atomicity invariant:** in the MVP, a successful event append and its corresponding task overview update are part of the same local transaction. A projection failure aborts the event append. This provides immediately consistent **local** reads without a message broker. It does not imply distributed transaction guarantees.

### Read path

`GET /tasks/{id}` (planned) queries the `task_overview` projection; it does not deserialize the full event stream on every request. `GET /tasks/{id}/events` (planned, for inspection/demo) exposes safe, synthetic event metadata. Public API shape remains provisional until the API task is implemented.

A proposed `task_overview` row contains `task_id`, `status`, `assigned_agent_id`, `current_generation_id`, `latest_test_result`, and `last_stream_version`. The projector is a deterministic reducer. A version guard prevents applying a repeated event twice and detects out-of-order application rather than silently inventing state.

## 6. Replay, rebuilding, and schema evolution

### MVP: controlled offline projection rebuild

1. Put the application in maintenance mode or stop normal command writes.
2. Create an empty replacement task overview (prefer a shadow table over destructive in-place truncation).
3. Enumerate task event streams and replay each in **stream-version order**, applying a pure projector.
4. Verify counts, final per-stream versions, and a deterministic comparison of expected read-model fields.
5. Switch the query source to the rebuilt view/table, then resume writes.
6. If verification fails, keep the original read model and report a rebuild failure.

**Replay never triggers real-world effects.** Historical `CodeGenerated` and `TestsPassed` events only update in-memory state and rebuilt projections; they never call agents, regenerate artifacts, or rerun tests.

### Future: replay under active load

An online rebuild would need shadow projections, a **commit-safe** method to capture and catch up with live events, validation before a short cutover, and a rollback strategy. A PostgreSQL sequence/identity value is **not necessarily transaction commit order**: naive “process every event with ID greater than watermark” logic can miss a late-committing transaction. The MVP deliberately avoids claiming to solve this problem.

### Event contract changes

Every stored event has `eventType` and `schemaVersion` from the start. In a later phase, explicit **upcasters** will transform old serialized contracts to a canonical in-memory representation without rewriting historical event rows. Contract fixtures and compatibility tests will verify both historical and current versions. No upcaster implementation is part of the initial documentation milestone.

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
| Old event contract | Later upcaster reads the historical version; incompatible changes must fail explicitly. |
| Replay encounters malformed event | Stop with diagnostic details; do not silently skip or mutate the authoritative log. |

**MVP baseline:** optimistic concurrency and transaction rollback. **Later hardening:** durable idempotency keys, duplicate-request result caching, tested retries, and online rebuild/cutover. The API must not claim effectively-once command behavior before idempotency has been implemented and tested.

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

## 9. Quality gates and future agent guardrails

The implementation will introduce unit tests for pure domain rules, PostgreSQL integration tests for transactions/concurrency, replay equivalence tests, and CI validation. Future real-agent contributions would be required to respect module boundaries, pass deterministic checks, avoid secrets in events, and undergo human code review. Automated agents will not be authorized to bypass domain commands or directly modify the event log.

## 10. Related documents

- [README](../README.md)
- [Implementation roadmap](roadmap.md)
- [ADR 0001 — Architectural foundation](adr/0001-architecture.md)

The technology choices and constraints in this document are **proposals pending implementation and verification**, not benchmark results or evidence of a deployed system.
