# EventForge.Workflow.Application

Namespace: `EventForge.Workflow.Application`

**Ownership:** Application orchestration of the Task Workflow bounded context. *Implemented:* the Task command handler and result types, the `TaskQueryHandler` and its results (state and event history rebuilt from the committed Event Store history), the shared `TaskHistory` fold, the explicit schema v1 event mapping, and the application-owned Event Store contracts. *Planned, not implemented:* read-model (projection) contracts for the Task 6 `task_overview`, and contracts for simulated outcomes.

**Dependency rule:** Application code may depend on Domain code. It must not depend on Infrastructure, Simulation, or the API; adapters implement its contracts, not the reverse.

Status: contains the Event Store contracts (`EventStore/`, EVF-22) and the Task command workflow (`Tasks/`, EVF-30): `TaskCommandHandler`, `TaskCommandResult` types and `TaskEventMapper` (explicit schema v1 mapping). `Tasks/` also holds `TaskQueryHandler`, `TaskQueryResult` and the internal `TaskHistory` fold shared by both handlers (EVF-34); the API composition root registers both handlers (EVF-33/EVF-34). Read-model contracts and projections do not exist yet (Task 6). Dependency rules are checked by the static architecture tests in `tests/EventForge.UnitTests` (EVF-16), which scan these sources.
