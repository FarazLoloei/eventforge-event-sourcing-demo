# EventForge.Workflow.Application

Namespace: `EventForge.Workflow.Application`

**Ownership:** Application orchestration of the Task Workflow bounded context. *Implemented:* the Task command handler and result types, the explicit schema v1 event mapping, and the application-owned Event Store contracts. *Planned, not implemented:* query handlers, read-model contracts, and contracts for simulated outcomes.

**Dependency rule:** Application code may depend on Domain code. It must not depend on Infrastructure, Simulation, or the API; adapters implement its contracts, not the reverse.

Status: contains the Event Store contracts (`EventStore/`, EVF-22) and the Task command workflow (`Tasks/`, EVF-30): `TaskCommandHandler`, `TaskCommandResult` types and `TaskEventMapper` (explicit schema v1 mapping). Query and read-model contracts, projections and API wiring do not exist yet. Dependency rules are checked by the static architecture tests in `tests/EventForge.UnitTests` (EVF-16), which scan these sources.
