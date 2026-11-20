# EventForge.Workflow.Domain

Namespace: `EventForge.Workflow.Domain`

**Ownership (planned):** Task aggregate invariants, domain events, and the pure `Decide(state, command)` and `Apply(state, event)` logic of the Task Workflow bounded context.

**Dependency rule:** Domain code must not depend on Application code, ASP.NET Core, SQL or database libraries, or simulation types.

## Status

**Implemented (EVF-28, contracts only):**

- Identifiers `TaskId`, `AgentId`, `GenerationId` and `ArtifactRef` with constructor validation.
- `TaskState` (initial state), `WorkflowStatus` and `TestOutcome`.
- Six commands and the six approved events (`TaskCreated`, `TaskAssigned`, `CodeGenerated`, `TestsPassed`, `TestsFailed`, `TaskCompleted`) with `TaskEventContract` (stream type `Task`, schema version 1).
- `Decision`, `Accepted`, `Rejected` and `RejectionCode` result types.
- Focused contract tests in `tests/EventForge.UnitTests/Domain`.

**Not implemented:** `Decide`/`Apply` and invariant enforcement (EVF-29), event serialization and Event Store mapping (EVF-30), REST API, projections and replay.

The Domain dependency rules are checked by the static architecture tests in `tests/EventForge.UnitTests` (EVF-16), which now scan these Domain sources.
