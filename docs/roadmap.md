# EventForge — Implementation Roadmap

> **Status: Planning / Work in Progress**  
> The repository begins with **documentation only**. None of the implementation tasks below is complete, and no runnable demonstration is claimed.

## 1. Delivery approach

Build the smallest **verifiable vertical slice** of an event-sourced task workflow before adding infrastructure or architectural abstractions. Every implementation step should produce reviewable changes, explicit tests, and an updated status in the README.

- **Phase 1 — Publish the proposal:** README, architecture, roadmap, and first ADR. **No application code.**
- **Phase 2A — MVP:** Event store, aggregate, API, transactional projections, controlled replay, minimal tests, and CI.
- **Phase 2B — Advanced capabilities:** Upcasting, snapshots, request idempotency, and deeper concurrency/failure testing.
- **Beyond the roadmap:** Event branching, safe previews, side-effect isolation, and real AI-agent integration, only after a separate design review.

The original 12 technical tasks are preserved below. Task numbers reflect logical delivery order rather than rigid iteration boundaries. Foundational tests will be written alongside implementation, not postponed until task 11.

## 2. The 12 planned development tasks

| # | Task | Scope | Expected deliverable / verification |
| --- | --- | --- | --- |
| **1** | **Solution and architecture setup** | MVP | Minimal .NET 10 solution; clear module dependency direction; documented commands; basic domain test scaffold. |
| **2** | **PostgreSQL and Docker configuration** | MVP | Local PostgreSQL through Docker Compose, repeatable migration/setup, configuration without committed secrets. |
| **3** | **Event Store implementation** | MVP | Append/load one aggregate stream; immutable events, event envelope, unique `(stream_type, stream_id, stream_version)` constraint, baseline expected-version checks. |
| **4** | **Domain aggregates and event persistence** | MVP | Pure Task `Decide`/`Apply` behavior; valid transitions emit events; invalid transitions do not persist. |
| **5** | **CQRS commands and queries** | MVP | REST commands for create/assign/generate/test/complete and query endpoints for task status/event history; explicit error contracts. |
| **6** | **Transactional projections** | MVP | A task overview read model updated within the same transaction as event writes; demonstrated rollback on failure. |
| **7** | **Event replay and projection rebuilding** | MVP | Write-paused/maintenance-mode rebuild into a fresh projection; deterministic verification and safe failure behavior. |
| **8** | **Event schema versioning and upcasting** | Advanced | At least one evolved event schema; explicit upcaster; historical fixtures and compatibility tests. |
| **9** | **Snapshots** | Advanced | Optional aggregate snapshots; version-aware loading; replay-from-origin fallback; tests proving no behavior change. |
| **10** | **Concurrency and idempotency** | Advanced hardening | Concurrent-writer tests beyond baseline version checks; durable request idempotency; safe repeated submissions and documented retry policy. |
| **11** | **Automated unit and integration tests** | MVP + Advanced | xUnit domain invariant tests, PostgreSQL transaction/concurrency tests, projection equality tests; expand coverage as tasks 8–10 land. |
| **12** | **GitHub Actions CI and final documentation** | MVP + Advanced | CI builds and tests against PostgreSQL; passing checks; accurate local-run instructions and example commands; subsequent docs updated as features land. |

**Clarification:** Basic optimistic stream concurrency is a correctness requirement from task **3**, not something deferred to task **10**. Task 10 deepens that guarantee and adds **request-level idempotency**, which the MVP will not claim to offer.

## 3. Incremental milestone plan

### Milestone 0 — Architecture proposal (Phase 1)

**Deliverables:** `README.md`, `docs/architecture.md`, `docs/roadmap.md`, `docs/adr/0001-architecture.md`, `.gitmessage`, and `.github/PULL_REQUEST_TEMPLATE.md`.

**Exit conditions:** All four documents are internally consistent; README states *Planning / Work in Progress*; proposed features are clearly labeled; no proprietary code, data, or employer-specific internal designs are included.

The project owner handles Git operations and Jira management manually. Preparing or validating these files does not authorize repository initialization, cloning, branch changes, staging, commits, pushes, merges, Pull Requests, Jira updates, or Phase 2 implementation. Preserve any existing `.gitignore`; its absence must be reported for owner resolution.

### Milestone 1 — Persist one workflow (Tasks 1–5)

A locally running API can create a synthetic task, assign it, record a generation result and a test result, and complete the task when permitted. Event ordering and baseline optimistic concurrency are persisted in PostgreSQL.

**Exit conditions:** Domain transitions are unit-tested; a PostgreSQL integration test validates append/load and unique version enforcement; the API returns a conflict for stale expected versions.

### Milestone 2 — Transactional query model (Task 6)

Introduce a `task_overview` projection in PostgreSQL and update it in the **same transaction** as appended events.

**Exit conditions:** A command's committed event and the read-model update are visible together; a forced projector failure rolls both changes back; the query API returns the expected task state.

### Milestone 3 — Deterministic recovery (Task 7)

Build the task overview from an existing event history with writes paused, without invoking simulator actions.

**Exit conditions:** A rebuilt view matches the live projection on the chosen deterministic fields, including the latest generation/test status; an invalid event stops the rebuild without replacing the live read model.

### Milestone 4 — First demonstrable MVP (Tasks 11–12, integrated throughout)

Complete reproducible local setup and CI. Publish an example transcript of the scenario, tests, and observed behavior; update documentation only after successful verification.

**Exit conditions:** All MVP acceptance criteria below have evidence from automated tests or reproducible local commands.

### Milestone 5 — Optional architecture hardening (Tasks 8–10)

Add upcasters, snapshots, and idempotent submission after MVP is proven. Each change must preserve behavior for previously recorded events.

**Exit conditions:** Historical-version fixtures, snapshot equivalence, duplicate-command tests, and concurrent-writer tests pass; ADRs are added if architectural guarantees change.

## 4. MVP acceptance criteria

The **first working demonstration** is accepted only when all of the following can be demonstrated:

- [ ] **Start locally:** Documented command launches API and PostgreSQL from a clean checkout, with no real AI credentials.
- [ ] **Happy path:** `TaskCreated -> TaskAssigned -> CodeGenerated(g1) -> TestsPassed(g1) -> TaskCompleted` is persisted and returned correctly by the query API.
- [ ] **Failure/recovery path:** `CodeGenerated(g1) -> TestsFailed(g1) -> CodeGenerated(g2) -> TestsPassed(g2)` is accepted and cannot complete before `g2` passes.
- [ ] **Invalid transition:** Completing a task without passing tests yields a domain error and **no** new event.
- [ ] **Stale writer:** Two commands targeting the same expected stream version cannot both commit; one receives a clearly documented conflict.
- [ ] **Atomic projection:** A simulated projection failure rolls back the event append, and successful appends yield an immediately matching read model.
- [ ] **Replay equivalence:** A controlled rebuild produces the same task status, assignment, generation, test outcome, and stream version as the original projection.
- [ ] **No replay side effects:** Rebuild does not invoke the agent simulator or regenerate artifacts.
- [ ] **Automated verification:** Unit and PostgreSQL integration tests pass locally and in GitHub Actions; README contains verified run/test commands and an accurate feature-status section.

*Acceptance is evidence-based. A diagram or README statement is not proof of implementation.*

## 5. Deliberate non-goals for the MVP

- Real LLM calls, code generation, execution sandboxes, or production-grade security claims.
- Distributed microservices, internal gRPC, Kubernetes, a message broker, or cross-database transactions.
- Event branching, branch merge semantics, or isolated ephemeral deployment previews.
- Active-load / zero-downtime projection rebuilding.
- Request-level exactly-once processing or guaranteed safe retries without an idempotency implementation.
- Fully generic domain/event frameworks, cross-aggregate process orchestration, or high-scale performance benchmarks.

## 6. Engineering workflow and documentation discipline

Each implementation change should state: **objective**, **architectural impact**, **observable behavior**, **tests**, **known limitations**, and **documentation updates**. The project owner approves significant design changes. New infrastructure must solve a demonstrated requirement; architectural choices should be documented by focused ADRs rather than hidden in commits.

The README will be updated from *Planning* to *Implemented / Tested* **feature by feature**, only after verification. The initial repository can be shared as an architecture proposal before any implementation begins.

## 7. Links

- [README](../README.md)
- [Architecture proposal](architecture.md)
- [ADR 0001](adr/0001-architecture.md)
