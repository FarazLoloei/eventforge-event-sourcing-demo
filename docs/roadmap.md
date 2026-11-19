# EventForge — Implementation Roadmap

> **Status: Planning / Work in Progress**  
> The repository began with **documentation only**; implementation now proceeds task by task, and the README records what is implemented and verified. Task 1 is completed, tasks 11 and 12 are partially implemented, and the remaining tasks are planned or deferred. The MVP is not complete, and no end-to-end MVP demonstration is claimed.

## 1. Delivery approach

Build the smallest **verifiable vertical slice** of an event-sourced task workflow before adding infrastructure or architectural abstractions. Every implementation step should produce reviewable changes, explicit tests, and an updated status in the README.

- **Phase 1 — Publish the proposal:** README, architecture, roadmap, and first ADR. **No application code.**
- **Phase 2A — MVP:** Event store, aggregate, API, transactional projections, upcasting, controlled replay/diffing, projection recovery, architecture tests, engineering guardrails, and CI.
- **Phase 2B — Advanced capabilities:** Snapshots, durable request idempotency, online projection rebuilding, and deeper concurrency/failure testing.
- **Beyond the roadmap:** Event branching, safe previews, side-effect isolation, and real AI-agent integration, only after a separate design review.

The original 12 technical tasks are preserved below. Task numbers retain the original task identities; milestones below define revised delivery priority. Task 8 precedes recovery acceptance, while tasks 11 and 12 span all milestones. Foundational tests will be written alongside implementation, not postponed until task 11.

## 2. The 12 planned development tasks

| # | Task | Scope | Status | Expected deliverable / verification |
| --- | --- | --- | --- | --- |
| **1** | **Solution and architecture setup** | MVP | **Completed** (EVF-13) | Minimal .NET 10 solution; clear module dependency direction; documented commands; basic domain test scaffold. |
| **2** | **PostgreSQL and Docker configuration** | MVP | Planned | Local PostgreSQL through Docker Compose, repeatable migration/setup, configuration without committed secrets. |
| **3** | **Event Store implementation** | MVP | Planned | Append/load one aggregate stream; immutable events, event envelope, unique `(stream_type, stream_id, stream_version)` constraint, baseline expected-version checks. |
| **4** | **Domain aggregates and event persistence** | MVP | Planned | Pure Task `Decide`/`Apply` behavior; valid transitions emit events; invalid transitions do not persist. |
| **5** | **CQRS commands and queries** | MVP | Planned | REST commands for create/assign/generate/test/complete and query endpoints for task status/event history; explicit error contracts. |
| **6** | **Transactional projections** | MVP | Planned | A task overview read model updated within the same transaction as event writes; demonstrated rollback on failure. |
| **7** | **Event replay and projection rebuilding** | MVP | Planned | Write-paused rebuild into a shadow projection; deterministic diffing, healthy verification, damaged-projection recovery, and safe cutover/failure tests. |
| **8** | **Event schema versioning and upcasting** | MVP | Planned | At least one evolved event schema; explicit upcaster; historical fixtures and compatibility tests. |
| **9** | **Snapshots** | Advanced | Deferred | Optional aggregate snapshots; version-aware loading; replay-from-origin fallback; tests proving no behavior change. |
| **10** | **Concurrency and idempotency** | Advanced hardening | Deferred | Concurrent-writer tests beyond baseline version checks; durable request idempotency; safe repeated submissions and documented retry policy. |
| **11** | **Automated unit and integration tests** | MVP + Advanced | **Partially implemented** (architecture test baseline) | Incremental xUnit domain, PostgreSQL transaction/concurrency, upcaster, replay/diff/recovery, and architecture tests; expand for deferred capabilities. |
| **12** | **GitHub Actions CI and final documentation** | MVP + Advanced | **Partially implemented** (baseline CI complete and verified, EVF-5; further quality gates pending) | MVP engineering guardrails: CI builds and tests against PostgreSQL, module dependency checks, synthetic fixtures, secret hygiene, and review requirements; accurate local-run instructions and example commands; subsequent docs updated as features land. |

**Status detail.** Task 1 is delivered by EVF-13 (solution, SDK pin, module scaffolding, documented commands, test project). Task 11 so far covers xUnit architecture dependency tests only (EVF-16); domain, PostgreSQL, upcaster and replay tests are not implemented. Task 12 so far covers a baseline GitHub Actions workflow (restore, Release build, xUnit tests, TRX artifacts) that ran successfully on a pull request and on `main` on November 19, 2026 (33 tests passed, 0 warnings, 0 errors). Controlled failure and recovery were verified on November 19, 2026 on temporary review [pull request #13](https://github.com/FarazLoloei/eventforge-event-sourcing-demo/pull/13) (closed unmerged, zero changed files, `main` unchanged): the [failure run](https://github.com/FarazLoloei/eventforge-event-sourcing-demo/actions/runs/44532239727) had 33 passed, 1 intentionally failed and 0 skipped, test exit code 1, a failed CI job and a still-uploaded TRX artifact; the [recovery run](https://github.com/FarazLoloei/eventforge-event-sourcing-demo/actions/runs/44532814416) had 33 passed, 0 failed and 0 skipped with a successful job and artifact upload, and both builds had 0 warnings and 0 errors. The EVF-5 baseline CI implementation and verification are complete. PostgreSQL, event-contract, concurrency and replay quality gates and CD remain pending, so task 12 stays partially implemented. Required status checks and branch protection are not configured; they require separate project-owner approval and are not EVF-5 closure requirements.

**Clarification:** Basic optimistic stream concurrency is a correctness requirement from task **3**, not something deferred to task **10**. Task 10 deepens that guarantee and adds **request-level idempotency**, which the MVP will not claim to offer.

Security and observability fit within these existing tasks: task **2** separates runtime and maintenance privileges and protects configuration; tasks **3–6** preserve append-only access, input validation, authorization boundaries, and safe structured diagnostics; task **7** restricts replay/cutover to operator capabilities. Tasks **11–12** verify these controls incrementally through planned CI quality gates. No enforcement exists yet. See the [architecture security and operational guidance](architecture.md#security-boundaries-planned); identity, deployment exposure, and operational tooling need owner review before shared/network deployment.

## 3. Incremental milestone plan

### Milestone 0 — Architecture proposal (Phase 1)

**Deliverables:** The four public architecture documents: README, architecture proposal, roadmap, and ADR 0001.

**Exit conditions:** All four documents agree on scope and priorities, distinguish proposals from implementation, and contain no confidential data. Implementation status is tracked in the README.

### Milestone 1 — Persist one workflow (Tasks 1–5)

A locally running API can create a synthetic task, assign it, record a generation result and a test result, and complete the task when permitted. Event ordering and baseline optimistic concurrency are persisted in PostgreSQL.

**Exit conditions:** Domain transitions are unit-tested; a PostgreSQL integration test validates append/load and unique version enforcement; the API returns a conflict for stale expected versions.

### Milestone 2 — Transactional query model (Task 6)

Introduce a `task_overview` projection in PostgreSQL and update it in the **same transaction** as appended events.

**Exit conditions:** A command's committed event and the read-model update are visible together; a forced projector failure rolls both changes back; the query API returns the expected task state.

### Milestone 3 - Versioned history and deterministic recovery (Tasks 8 and 7)

First add explicit schema upcasters with historical/current fixtures. Then rebuild the task overview with writes paused and drained, without invoking simulator actions.

**Exit conditions:** Healthy-projection verification yields zero semantic differences. A fixture with missing/extra rows and altered fields produces the expected stable diff; its replacement matches authoritative history and independent reference expectations, without requiring equality with the damaged baseline. Unsupported schemas or invalid events stop cutover. Successful cutover routes both queries and inline updates to the replacement; failed validation leaves it inactive.

### Milestone 4 — First demonstrable MVP (Tasks 11–12, integrated throughout)

Complete reproducible local setup and CI. Publish an example transcript of the scenario, tests, and observed behavior; update documentation only after successful verification.

**Exit conditions:** All MVP acceptance criteria below have evidence from automated tests or reproducible local commands.

### Milestone 5 - Optional architecture hardening (Tasks 9 and 10)

Add snapshots and durable idempotent submission after MVP is proven. Online rebuilding, event branching, isolated preview environments, and controlled side effects require later design work. Each change must preserve behavior for previously recorded events.

**Exit conditions:** Snapshot equivalence, duplicate-command tests, and expanded concurrent-writer tests pass; ADRs are added if architectural guarantees change.

## 4. MVP acceptance criteria

The **first working demonstration** is accepted only when all of the following can be demonstrated:

- [ ] **Start locally:** Documented command launches API and PostgreSQL from a clean checkout, with no real AI credentials.
- [ ] **Happy path:** `TaskCreated -> TaskAssigned -> CodeGenerated(g1) -> TestsPassed(g1) -> TaskCompleted` is persisted and returned correctly by the query API.
- [ ] **Failure/recovery path:** `CodeGenerated(g1) -> TestsFailed(g1) -> CodeGenerated(g2) -> TestsPassed(g2)` is accepted and cannot complete before `g2` passes.
- [ ] **Invalid transition:** Completing a task without passing tests yields a domain error and **no** new event.
- [ ] **Stale writer:** Two commands targeting the same expected stream version cannot both commit; one receives a clearly documented conflict.
- [ ] **Atomic projection:** A simulated projection failure rolls back the event append, and successful appends yield an immediately matching read model.
- [ ] **Replay equivalence:** A controlled rebuild produces the same task status, assignment, generation, test outcome, and stream version as a healthy baseline at the same source boundary and contract, with no missing or extra rows.
- [ ] **Schema evolution:** At least one explicit old-to-current upcaster passes historical/current fixtures with equivalent aggregate state and projection output; missing steps and unknown versions fail without changing stored events.
- [ ] **Projection recovery:** Deliberately deleted, extra, and altered rows are detected. The replacement passes event-history/reference validation and can be activated without matching the damaged baseline; validation failure prevents cutover.
- [ ] **Stable diff:** Repeated comparisons of identical inputs produce identical ordered missing/extra-row and field-level reports, with explicit null handling.
- [ ] **Architecture and guardrails:** Dependency tests reject a forbidden domain-to-infrastructure dependency in a test fixture; CI runs build, domain, PostgreSQL, upcaster, replay, and architecture checks. Document synthetic-data, secret-hygiene, and human-review rules.
- [ ] **No replay side effects:** Replay tests fail if simulator, artifact, test-execution, or external-effect adapters are invoked; rebuild invokes none.
- [ ] **Automated verification:** Unit and PostgreSQL integration tests pass locally and in GitHub Actions; README contains verified run/test commands and an accurate feature-status section.

*Acceptance is evidence-based. A diagram or README statement is not proof of implementation.*

## 5. Deliberate non-goals for the MVP

- Real LLM calls, code generation, execution sandboxes, or production-grade security claims.
- Distributed microservices, internal gRPC, Kubernetes, a message broker, or cross-database transactions.
- Event branching, branch merge semantics, or isolated ephemeral deployment previews.
- Snapshots and durable request idempotency.
- Active-load / zero-downtime projection rebuilding and controlled side-effect execution.
- Request-level exactly-once processing or guaranteed safe retries without an idempotency implementation.
- Multi-tenancy and RLS tenant policies; external publication and a Transactional Outbox unless a separately approved requirement justifies them.
- Mandatory advisory locking or a dedicated observability platform; consider these only for demonstrated coordination or operational needs after owner review.
- Fully generic domain/event frameworks, cross-aggregate process orchestration, or high-scale performance benchmarks.

## 6. Engineering workflow and documentation discipline

Technical changes require a clear rationale, observable behavior, validation evidence, known limitations, and corresponding documentation. Significant design changes require project-owner approval and an authorized ADR. New infrastructure must address a demonstrated requirement.

The README will be updated from *Planning* to *Implemented / Tested* **feature by feature**, only after verification. The initial repository can be shared as an architecture proposal before any implementation begins.

Planned CI quality gates will verify least-privilege grants, authorization denial without writes, and diagnostic redaction alongside domain, persistence, and replay behavior. The baseline CI workflow is operational, but required status checks and branch protection are not configured. Deferred capabilities remain outside the MVP, and implementation still requires explicit project-owner authorization.

## 7. Links

- [README](../README.md)
- [Architecture proposal](architecture.md)
- [ADR 0001](adr/0001-architecture.md)
