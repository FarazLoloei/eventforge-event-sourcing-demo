# EventForge AI Agent Governance

## Scope and authority

EventForge is documentation-only; implementation has not started. These are engineering rules for authorized work, not evidence of implemented safeguards. No application, automated tests, architecture checks, CI gates, commit linting, or Jira integration is claimed to exist. Git templates provide guidance, not enforcement.

- Read the [architecture proposal](docs/architecture.md), [roadmap](docs/roadmap.md), and [ADR 0001](docs/adr/0001-architecture.md) before making changes. Follow their current proposed baseline and preserve decision history.
- Inspect repository context and edit/validate only files authorized by the active task. Keep changes focused, preserve unrelated work, and report scope conflicts before expanding edits.
- Do not start Phase 2 or add application code without explicit project-owner authorization. EVF-4 authorizes only this file and a short README reference.
- Obtain explicit project-owner approval before adopting significant architectural changes: technology, module ownership, event contracts/semantics, consistency guarantees, or replay/cutover strategy. Prepare rationale, alternatives, impacts, validation, and migration/rollback needs for review; record the approved decision in an authorized ADR with a dated history entry.
- Never create or switch branches, stage, commit, push, merge, create Pull Requests, or modify Jira. Read-only Git inspection is permitted. Human approval of a design does not grant these operations.
- Preserve [.gitmessage](.gitmessage), the [Pull Request template](.github/PULL_REQUEST_TEMPLATE.md), and [.gitignore](.gitignore).

## Architecture and domain rules

- Retain C#/.NET 10, ASP.NET Core, PostgreSQL, Modular Monolith, DDD, Event Sourcing, and CQRS. Use one deployable process and one database for the MVP; introduce infrastructure only for an approved requirement.
- Task Workflow owns commands, events, aggregate invariants, and read-model contracts. HTTP, persistence, and execution simulation are adapters. Communicate through explicit application contracts; do not access another module's tables or implementation types. Keep HTTP/SQL/simulator dependencies outside the domain.
- Separate commands from queries. Commands enforce invariants through the Task aggregate; queries read projections. Neither HTTP nor simulation may bypass command handling or duplicate domain decisions.
- Keep Decide(state, command) and Apply(state, event) pure and deterministic. Supply time, IDs, and external outcomes explicitly and persist relevant facts. Apply folds recorded facts without re-running current command validation; neither function performs I/O or reads ambient state.
- Preserve immutable, append-only events. Invalid commands append nothing. Assignment precedes generation; tests refer to the current generation; completion requires that generation to pass. A new generation invalidates the previous test outcome.
- Persist stable event types and explicit schema versions. Use pure, explicit, tested upcaster chains to read historical contracts without rewriting stored events. Unknown versions, missing steps, malformed events, and stream gaps fail explicitly.
- Require expected stream versions, contiguous appended versions, and the unique (stream_type, stream_id, stream_version) constraint. Reject stale writers with a conflict and roll back the whole command; do not silently retry decisions. Optimistic concurrency is not durable request idempotency.
- Append events and update inline task projections in one PostgreSQL transaction. Projection failures roll back both; report success after commit. Require contiguous projector versions and use committed primary-database reads for immediate local consistency.

## Replay, verification, and recovery

- MVP replay pauses all writers and drains in-flight transactions. Build a fresh shadow projection from supported, upcast history in per-stream order; keep writes paused through validation and cutover.
- Replay only reduces historical facts: never invoke agents, regenerate artifacts, execute tests, send notifications, or dispatch external work. Keep maintenance activation separate from event application.
- Diff complete task key sets and business fields specified in the architecture, including final stream versions. Canonicalize representations, handle nulls explicitly, sort rows/fields consistently, and report missing/extra rows and changed values. Counts or hashes alone do not establish equivalence.
- Verification of a healthy projection requires zero semantic differences at the same history boundary and projection contract, plus history validation.
- Recovery reports discrepancies in the damaged baseline and validates the replacement against authoritative event history and independent reference expectations. Never require equality with a corrupted projection. Stop if authoritative history is damaged or unsupported.
- Activate only a validated candidate, atomically redirect query and inline-write targets, and retain rollback data. Failed validation leaves the candidate inactive; do not present a damaged baseline as trusted.
- Online rebuilding remains advanced work requiring a separate design. PostgreSQL sequence allocation is not transaction commit order; a naive sequence watermark can miss late commits.

## Incremental tests and review

These requirements apply when implementation is authorized; do not create scaffolding to satisfy a documentation-only task.

- Add unit tests alongside domain rules, deterministic reducers, and historical/current upcaster fixtures.
- Add PostgreSQL integration tests for append/load, racing expected versions, multi-event atomicity, projection failure rollback, and committed read behavior.
- Test healthy verification, corrupted-projection recovery, stable row/field diffs, invalid-history failure, cutover routing, and absence of replay effects using adapters that fail if invoked. Use independent reference expectations to expose projector defects.
- Add architecture tests for allowed dependencies and domain isolation, including a forbidden-dependency fixture. Build and test gates belong to the MVP; do not claim enforcement until implemented and verified.
- Treat AI-generated changes as untrusted contributions. Human reviewers must inspect correctness, invariants, dependencies, security, schema compatibility, replay behavior, and test evidence before acceptance. Generated tests do not replace review or independent expectations.
- Keep all 12 roadmap tasks. Upcasting, controlled replay/diffing, recovery, architecture tests, and engineering guardrails are MVP. Snapshots, durable request idempotency, online rebuilding, event branching, isolated previews, and controlled side effects remain deferred.

## Security and intellectual property

- Author original work and synthetic fixtures. Never publish or reconstruct proprietary code, private schemas, employer documentation, customer data, recruitment correspondence, or confidential operational history.
- Keep credentials, tokens, personal data, and secrets out of events, artifacts, examples, logs, and tool output. Use synthetic artifact references; do not embed source trees in events. Ignore rules alone are not a security boundary.
- Validate untrusted inputs at adapters and domain boundaries. Do not execute generated code or enable real AI/external integrations in the MVP. Review dependency provenance and licensing before proposing additions.

## Human-owned commit and Pull Request standards

Agents may prepare review text in their response; the project owner performs Git/Jira operations.

- Follow the existing commit template: `type(scope): imperative summary [JIRA-KEY]`, with lowercase type/scope, optional scope, and preferably at most 72 subject characters. Use the actual assigned Jira key; template examples are placeholders. Explain breaking changes and migration impact in a `BREAKING CHANGE:` footer.
- Follow the existing Pull Request template. Include the real Jira key and direct issue link, roadmap task and ADR where relevant, concrete behavior/design changes, validation commands/results, risks, and follow-up. Recommend the same Conventional Commit format for the title; do not invent issue keys or URLs.
- Report only checks actually run; explain skipped checks. Review scope, invariants, event compatibility, transactions, replay, documentation status, and confidential-data hygiene. Automatic Jira linking and required-check enforcement depend on future configuration.
- Keep public documentation in English. At completion, report changed files, validation evidence, limitations, and implementation status. Preserve the distinction between documented policy and functioning automation.
