---
name: event-sourcing
description: Apply EventForge's Event Sourcing and CQRS rules when designing or reviewing the Task aggregate, event store, inline projections, upcasting, replay, projection diffing or recovery.
---

# EventForge Event Sourcing and CQRS

`AGENTS.md`, `docs/architecture.md` (sections 3-6) and ADR 0001 are authoritative and hold the full rules. This skill is a review aid that points at where defects usually hide. It adds no decisions.

## Write path

1. Load the stream, upcast, and fold events with `Apply`.
2. Run `Decide(state, command)`. It returns new events or an explicit rejection. A rejection appends nothing.
3. In one PostgreSQL transaction: append with the expected version, update the inline projection, commit.
4. Report success only after commit.

Queries read projections only and never call `Decide`.

## Pure functions

- `Decide` and `Apply` take everything as arguments: time, IDs, and external outcomes such as a simulated test result.
- No I/O, clocks, randomness or ambient state. Persist any fact a later replay would need.
- `Apply` folds recorded facts. It must not re-validate against current command rules.
- Quick test: the same inputs give the same outputs on any machine and any day.

## Concurrency

- Append requires an expected version and contiguous new versions. The unique `(stream_type, stream_id, stream_version)` constraint is the backstop.
- A stale writer gets a conflict and the whole command rolls back. Do not retry the decision silently.
- This is not idempotency. Durable request idempotency is deferred.

## Versioning and upcasting

- Events have a stable type name and an explicit schema version, and stored events are never rewritten.
- Upcasters are pure, single-step, chained, and each has a fixture from the old version.
- Unknown versions, missing steps, malformed events and stream gaps fail explicitly. No default-and-continue.

## Replay and projection diffing

- MVP replay is offline: writers paused, shadow projection built from upcast history in per-stream order.
- Replay only reduces facts. Use adapters that throw if invoked to prove no agent, test, notification or artifact work runs.
- A diff compares full key sets and the business fields named in the architecture, including final stream version. Canonicalize, treat nulls explicitly, sort deterministically, and report missing, extra and changed rows.
- A count or hash is not evidence of equivalence.

## Recovery

- Validate the candidate against authoritative history and an independently written expected result. Never require equality with the damaged baseline.
- Stop if history is damaged or unsupported.
- Activate only a validated candidate, redirect query and inline-write targets together, and keep rollback data.

## Out of scope

Snapshots, online rebuilding, event branching, isolated previews, controlled side effects and durable idempotency are deferred. A sequence value is not commit order. Don't build any of these without a separate approved design.
