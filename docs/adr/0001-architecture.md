# ADR 0001 — Modular Monolith with Event Sourcing and CQRS

- **Status:** Proposed (not yet implemented)
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

Use .NET 10 as the application runtime and C# for domain behavior, HTTP endpoints, and test code. This is the project's strongest authoring stack and supports concise, testable domain modeling. This sample demonstrates **transferable architectural reasoning**, not a Go implementation or a claim of production compatibility with any specific external application.

The sample will target a supported .NET 10 release and pin the SDK once implementation begins.

### 3. Use PostgreSQL for events and read models

Keep the append-only event log and task overview projection in **one PostgreSQL database**, enabling event append and projection update in a **single local database transaction**. Persist domain event payloads as JSONB with explicit event names and schema versions; use relational uniqueness constraints for stream-version concurrency.

The event store will be purpose-built and small. Do not adopt a generic event-sourcing platform, dedicated event database, or message broker in the MVP without a demonstrated requirement.

### 4. Apply Event Sourcing to the Task aggregate

The authoritative representation of a task's lifecycle is its ordered event stream. Domain decisions are pure or deterministic for known inputs. Supported initial facts are `TaskCreated`, `TaskAssigned`, `CodeGenerated`, `TestsPassed`, `TestsFailed`, and `TaskCompleted`.

Event Sourcing is confined to this history-sensitive workflow; it is **not** mandated for every future table or supporting function. Replays must not invoke simulators or other side effects.

### 5. Apply CQRS with transactional inline projections

Write-side commands load the event stream, rehydrate the aggregate, check invariants, and append valid events. Read-side queries use a `task_overview` projection. The MVP projector executes in the same PostgreSQL transaction as the event append, so a failed projection update also aborts the write.

The initial rebuild procedure runs **with writes paused** and verifies a replacement projection before cutover. This is not a claim of online or zero-downtime rebuilding. Online catch-up, shadow swaps under load, schema upcasters, snapshots, and request idempotency are separate, later work.

### 6. Enforce baseline optimistic concurrency

Commands provide an expected stream version. A unique database constraint on `(stream_type, stream_id, stream_version)` protects against concurrent duplicate versions. Conflict responses are explicit and no partial transaction should commit. Request-level idempotency is a separate future guarantee: the initial API may not safely deduplicate a command after a response is lost.

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
| **Go implementation** | A valid alternative, but C#/.NET allows the project to demonstrate tested architecture faster without suggesting expertise not being demonstrated here. |
| **Advisory locks for every write** | Additional locking is unnecessary for baseline aggregate-stream correctness when expected versions and unique constraints are enforced. Revisit only for a concrete contention requirement. |

## Risks and follow-up decisions

1. **Contract evolution:** Define fixtures and upcasting policy before changing persisted event payloads (roadmap task 8).
2. **Replay while live traffic continues:** Do not rely on sequence IDs as commit-order cursors; a future online projection rebuild requires a separate ADR and commit-safe catch-up approach.
3. **Side effects:** Real code-generation and test execution would require isolated execution, explicit authorization, and controlled side-effect boundaries; these are outside the MVP.
4. **Request retries:** Implement and test durable command idempotency before claiming safe repeat submissions after unknown outcomes.
5. **Module growth:** Split domain ownership only when separate invariants and release/operational needs justify it.

## Independence and source hygiene

All design examples, event contracts, and future code will be **original and synthetic**. No proprietary source, employer-specific schema, private operational history, confidential task data, or internal engineering manuals will be published or reconstructed. This ADR is a proposal for this repository alone.

## References within the repository

- [Project README](../../README.md)
- [Architecture proposal](../architecture.md)
- [Delivery roadmap](../roadmap.md)
