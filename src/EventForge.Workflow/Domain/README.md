# EventForge.Workflow.Domain

Namespace: `EventForge.Workflow.Domain`

**Ownership (planned):** Task aggregate invariants, domain events, and the pure `Decide(state, command)` and `Apply(state, event)` logic of the Task Workflow bounded context.

**Dependency rule:** Domain code must not depend on Application code, ASP.NET Core, SQL or database libraries, or simulation types.

Status: scaffolding only. No domain types exist yet. The Domain dependency rules are checked by the static architecture tests in `tests/EventForge.UnitTests` (EVF-16); with no Domain types yet, that production check is vacuous.
