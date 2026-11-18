---
name: clean-architecture
description: Apply EventForge's Clean Architecture, Modular Monolith, DDD and Ports-and-Adapters structure when designing or reviewing .NET 10 module layout, project references, or dependency direction.
---

# EventForge Clean Architecture

`AGENTS.md`, `docs/architecture.md` and ADR 0001 are authoritative. This skill is a working checklist, not a source of decisions. If a task needs a decision those documents do not make, stop and ask the project owner.

Implementation requires explicit owner authorization. Until then, review and plan only.

## Structure (proposed layout, not mandated assemblies)

- One deployable ASP.NET Core process, one database.
- Task Workflow is the only bounded context. Simulation is an adapter, not a second context.
- Inside Workflow, keep Domain and Application separate (folders or namespaces first). Add an assembly only when it enforces a real dependency direction.

## Dependency direction

```
Api ──► Application ──► Domain
Infrastructure ──► Application (ports) ──► Domain
Simulation     ──► Application (ports)
```

- Domain references nothing: no ASP.NET, SQL, Npgsql, simulator, or Application types.
- Application owns the ports (small interfaces for persistence and simulated outcomes) and command/query handlers.
- Adapters implement ports; they never decide domain transitions.
- Only the API project composes concrete types (composition root, constructor injection).

## Checks before accepting a change

1. Does any `using` or project reference point outward (Domain → Application, Application → Infrastructure)?
2. Does an adapter contain a business rule, or does HTTP/simulation bypass a command handler?
3. Is each new interface a real boundary with more than one plausible implementation or a test seam? If not, use the concrete type.
4. Are expected rejections and version conflicts explicit results, with exceptions kept for unexpected failures?
5. Are `CancellationToken`s propagated at application and adapter boundaries, while reducers stay synchronous?

## DDD notes

- Aggregate invariants live in the Task aggregate; handlers orchestrate, they do not decide.
- Use strongly typed commands, events and results (records, immutable state).
- Name things in the workflow's ubiquitous language (assignment, generation, test outcome), not by technical layer.

## Pragmatic simplicity (SOLID without ceremony)

- Prefer small, single-purpose types; stop there.
- Avoid generic repositories, service locators, mandatory mediator or event frameworks, and speculative abstractions.
- No new package without a provenance and licensing review and owner approval.
- Do not create projects "for completeness". This is a demonstration system.

## Verification

- Boundaries are enforced by an architecture test only once one exists and passes. Until then, state that rules are documented, not enforced.
- Report the actual restore/build/test commands you ran and their results.
