# EventForge.Workflow.Domain

Namespace: `EventForge.Workflow.Domain`

**Ownership:** Task aggregate invariants, domain events, and the pure `Decide(state, command)` and `Apply(state, event)` logic of the Task Workflow bounded context.

**Dependency rule:** Domain code must not depend on Application code, ASP.NET Core, SQL or database libraries, or simulation types.

## Status

**Implemented (EVF-28, contracts only):**

- Identifiers `TaskId`, `AgentId`, `GenerationId` and `ArtifactRef` with constructor validation.
- `TaskState` (initial state), `WorkflowStatus` and `TestOutcome`.
- Six commands and the six approved events (`TaskCreated`, `TaskAssigned`, `CodeGenerated`, `TestsPassed`, `TestsFailed`, `TaskCompleted`) with `TaskEventContract` (stream type `Task`, schema version 1).
- `Decision`, `Accepted`, `Rejected` and `RejectionCode` result types.
- Focused contract tests in `tests/EventForge.UnitTests/Domain`.

**Implemented (EVF-29, pure aggregate logic):**

- `TaskAggregate.Decide(state, command)`: validates the command, then returns `Accepted` with the new event or `Rejected(RejectionCode)` and no events. Malformed commands throw `ArgumentException`.
- `TaskAggregate.Apply(state, event)`: folds a recorded event without calling `Decide`; throws `InvalidEventHistoryException` (message: event type and 1-based position only) for null or malformed events and for histories that violate the schema v1 invariants.
- `TaskAggregate.Rehydrate(history)`: folds an ordered history from `TaskState.Initial`.
- Unit tests for transitions, rejections, invalid history and malformed input in `tests/EventForge.UnitTests/Domain`.

**Not implemented:** command handlers, event serialization and Event Store mapping (EVF-30), REST API, projections and replay. Nothing calls the aggregate yet.

The Domain dependency rules are checked by the static architecture tests in `tests/EventForge.UnitTests` (EVF-16), which now scan these Domain sources.
