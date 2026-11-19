# EventForge.Workflow.Application

Namespace: `EventForge.Workflow.Application`

**Ownership (planned):** Command and query handlers, application-owned contracts for persistence and simulated outcomes, and read-model contracts of the Task Workflow bounded context.

**Dependency rule:** Application code may depend on Domain code. It must not depend on Infrastructure, Simulation, or the API; adapters implement its contracts, not the reverse.

Status: scaffolding only. No application types exist yet. Dependency rules are checked by the static architecture tests in `tests/EventForge.UnitTests` (EVF-16); with no Application types yet, that production check is vacuous.
