# EventForge — Task Workflow REST API Contracts (EVF-32)

> **Status: Owner-approved design. Documentation only; implementation pending in EVF-33 to EVF-35.** Nothing in this document is implemented. No endpoint, DTO, handler registration, Swagger/OpenAPI integration or API test exists; `Program.cs` still maps only `/health`. The existing Domain and Application contracts stay authoritative; where this document and the code disagree, the code wins and the gap is listed in [Section 11](#11-task-6-compatibility-discrepancies-and-limitations).

## 1. Scope and non-goals

**In scope (Roadmap Task 5, parent EVF-24):** seven REST routes over the existing Task workflow, explicit request/response/error contracts, a Swagger UI approach, and a verification plan.

**Educational goal:** show CQRS over an event-sourced aggregate, optimistic concurrency and honest failure reporting, using *Minimum Sufficient Architecture*. Every item below exists for a correctness or educational reason.

**Non-goals:** authentication/authorization, rate limiting, multi-tenancy, API versioning, pagination, projections or new tables (Task 6), upcasting (Task 8), replay (Task 7), durable request idempotency (deferred), MediatR, EF Core, brokers, extra services or layers, new Domain commands/events/fields, production observability, public deployment.

## 2. Current implementation reuse

| Existing element | Reused for |
| --- | --- |
| `TaskCommand`s `CreateTask`, `AssignTask`, `RecordCodeGenerated`, `RecordTestsPassed`, `RecordTestsFailed`, `CompleteTask` | One command per route (the test-results route chooses Passed/Failed by `outcome`). No new commands. |
| Value types `TaskId` (non-empty GUID), `AgentId`/`GenerationId` (non-blank, ≤100, no control chars), `ArtifactRef` (≤500, no control chars) | The single source of identifier validation; the API constructs them and maps `ArgumentException` to `400`. |
| `TaskAggregate.Decide` / `Apply` / `Rehydrate`, `TaskState` | Decisions (via the handler) and state reconstruction for queries. |
| `TaskCommandHandler.HandleAsync(command, expectedVersion, correlationId, ct)` | All six commands. |
| `TaskCommandResult` family: `CommandSucceeded`, `CommandRejected(RejectionCode)`, `VersionConflict(int?)`, `DuplicateEventId`, `InvalidHistory(reason, position?)`, `CommitUnknown` | Mapped 1:1 to HTTP in [Section 5](#5-http-error-and-concurrency-mapping). |
| `TaskEventMapper.FromStored` (strict schema v1, fail-closed) | Event-history and state reads; no second validator. |
| `IEventStore.LoadStreamAsync` (throws `EventStreamCorruptedException` on gaps) | Query reads. |
| `PostgreSqlEventStore`, `PostgreSqlDatabase` | Persistence; `--verify-database`; lazy singleton so startup needs no credentials. |
| Integration-test patterns (`PostgresFact`, `PostgresFixture`, `TaskWorkflowPostgresTests`) | EVF-34/35 test style. |

**Handler facts the HTTP layer must respect (verified in code):**

1. Order of evaluation: validate command → load stream (`EventStreamCorruptedException` → `InvalidHistory`) → **compare `expectedVersion` with the loaded version (`VersionConflict(loaded.Version)`)** → verify event count/positions/stream identity and fold (`InvalidHistory`) → `Decide` (`CommandRejected`) → append.
2. Consequently a **stale `expectedVersion` always wins over a domain rule and over unreadable history** (except non-contiguous stored versions, detected at load). Do not reorder.
3. Consequences: `CreateTask` on an existing task returns `VersionConflict` when `expectedVersion` differs from the current stream version, and `CommandRejected(TaskAlreadyExists)` when `expectedVersion` equals it and the existing history is valid (the version check runs first; `Decide` runs only after it). Both map to `409`. The approved `POST /api/tasks` route has no `expectedVersion` field and always passes 0, so a duplicate create through that route (stream version ≥ 1) is reported as `VersionConflict`; `TaskAlreadyExists` is a handler-level result that the mapping still covers. A non-create command on a missing task returns `CommandRejected(TaskNotFound)` only when `expectedVersion` is 0, otherwise `VersionConflict(0)`.
4. `VersionConflict.CurrentVersion` is `null` when a concurrent writer won the append race. The API must omit `currentVersion` then, never guess it.
5. The handler throws `ArgumentException` for a negative `expectedVersion` or a bad correlation id (non-blank, ≤100, no control characters); the API validates these first so they become `400`, not `500`.
6. `CommandSucceeded` carries `NewVersion` and the emitted Domain events. The HTTP response uses only `NewVersion`; emitted events are not exposed in command responses (history is read through `GET /api/tasks/{id}/events`). It carries no resulting state, event ids or timestamps.

## 3. Architectural boundaries and request flows

ASP.NET Core 10 **Minimal APIs**, endpoints grouped under `MapGroup("/api/tasks")`, in `EventForge.Api` only. HTTP is an adapter: it translates, validates shape and maps results; it makes no domain decisions.

```text
Command: HTTP endpoint -> request DTO validation -> TaskCommandHandler -> TaskAggregate -> IEventStore -> PostgreSQL
Query:   HTTP endpoint -> TaskQueryHandler -> IEventStore.LoadStreamAsync -> TaskEventMapper + TaskAggregate.Apply -> response DTO
```

- **Placement.** `TaskQueryHandler` (new, EVF-34) lives in `EventForge.Workflow/Application/Tasks/` next to the command handler and returns Application result types (state, history, `InvalidHistory`, not-found), never HTTP types. Request/response DTOs, route handlers and result-to-HTTP mapping live in `EventForge.Api` (for example `Tasks/TaskEndpoints.cs`, `Tasks/TaskContracts.cs`, `Tasks/TaskProblemMapping.cs`). Domain and Application reference no ASP.NET Core or Npgsql types (already enforced by the architecture tests).
- **Reads.** For Task 5, queries rebuild state directly from committed Event Store history on a primary-database read. No projection table is read or written (`task_overview` is Task 6). Queries never bypass the mapper or `Apply`.
- **Writes.** Every state change goes through `TaskCommandHandler`; the simulator will use the same boundary later.
- **Shared fold (smallest reuse step).** The handler's private load-and-fold loop (stream identity check, position check, mapper, `Apply`, reason mapping) must not be copied into the query handler. EVF-34 extracts it unchanged into one small internal Application function (for example `TaskHistory.Fold(LoadedStream)` returning state or `InvalidHistory`) used by both handlers; the existing handler tests must pass unmodified. **Owner-approved.** This is the only planned change to existing production code.

## 4. Exact REST routes and DTO contracts

JSON: UTF-8, camelCase, `Content-Type: application/json`. Property names bind case-insensitively (the ASP.NET Core default), so `expectedversion` is accepted as `expectedVersion`. Request bodies reject genuinely unknown members (`JsonUnmappedMemberHandling.Disallow`), so a body containing `unexpectedField` is a `400`, not a silently ignored field. Enums are explicit lowercase strings. Timestamps are ISO-8601 UTC as stored (`recordedAt`).

**Identifiers.** `{id}` and `taskId`: GUID in canonical `D` form (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`), not all zeros, case-insensitive on input and returned lowercase. `agentId`, `generationId`: the Domain rules above (non-blank, ≤100, no control characters); demo values such as `agent-demo-1`, `g1`. `artifactRef`: Domain rules plus the **API-boundary demo rule** (the Domain explicitly leaves this to the adapter): must match `^synthetic://[A-Za-z0-9][A-Za-z0-9._/-]*$`, ≤500 characters (example `synthetic://artifacts/demo-task/g1`). **Owner-approved.** It prevents real URLs, paths or source text from being stored; it adds no Domain field. Reads do not re-apply this rule, so historical events stay readable.

**`expectedVersion`.** Required integer ≥ 0 in the body of every command except create (create always uses 0 and has no field). Declare it as `int?` and reject a missing value: a non-nullable `int` would silently bind to 0.

**Correlation ID.** Optional request header `X-Correlation-Id`, passed as the handler's `correlationId` (stored in event metadata). Non-blank, ≤100 characters, no control characters, otherwise `400`. The API does not generate one, never exposes stored metadata, and may echo a valid supplied value in the same response header. It is traceability only, not an idempotency key.

### Commands

| # | Route | Request body | Domain command | Success |
| --- | --- | --- | --- | --- |
| 1 | `POST /api/tasks` | `{ "taskId": "<guid>" }` | `CreateTask`, expected version 0 | `201` + `Location: /api/tasks/{id}` |
| 2 | `POST /api/tasks/{id}/assign` | `{ "expectedVersion": 1, "agentId": "agent-demo-1" }` | `AssignTask` | `200` |
| 3 | `POST /api/tasks/{id}/generations` | `{ "expectedVersion": 2, "generationId": "g1", "artifactRef": "synthetic://artifacts/demo/g1" }` | `RecordCodeGenerated` | `200` |
| 4 | `POST /api/tasks/{id}/test-results` | `{ "expectedVersion": 3, "generationId": "g1", "outcome": "passed" }` | `outcome` `passed` → `RecordTestsPassed`; `failed` → `RecordTestsFailed`; any other value → `400` | `200` |
| 5 | `POST /api/tasks/{id}/complete` | `{ "expectedVersion": 4 }` | `CompleteTask` | `200` |

Command success body (all five) contains only the task id and the new stream version. Emitted Domain events are not exposed here; use `GET /api/tasks/{id}/events` for history. No state snapshot is returned either (the handler does not return one; deriving it in the API would duplicate Domain logic, so clients call `GET /api/tasks/{id}`).

```json
{
  "taskId": "9d56311e-acda-47c9-8a1c-f8e5d5605f4c",
  "version": 3
}
```

`version` is `CommandSucceeded.NewVersion` and is the value to send as `expectedVersion` on the next command.

### Queries

**`GET /api/tasks/{id}` → `200` Task state** (only fields already in `TaskState`):

```json
{
  "taskId": "0b9f6f2e-6c1d-4f5e-9a57-3a1d2c4e8b10",
  "status": "assigned",
  "assignedAgentId": "agent-demo-1",
  "currentGenerationId": "g1",
  "currentTestOutcome": "failed",
  "version": 4
}
```

`status` ∈ `created | assigned | completed` (`NotCreated` is a `404`, never returned). `assignedAgentId`, `currentGenerationId` and `currentTestOutcome` (`passed | failed`) are `null` while absent; `currentTestOutcome` is `null` after a new generation, per the Domain. `version` = number of applied events. Not exposed: superseded generations, artifact references, metadata.

**`GET /api/tasks/{id}/events` → `200` ordered history** (ascending `streamVersion`):

```json
{
  "taskId": "0b9f6f2e-6c1d-4f5e-9a57-3a1d2c4e8b10",
  "version": 3,
  "events": [
    { "eventId": "…", "streamVersion": 1, "eventType": "TaskCreated", "schemaVersion": 1,
      "recordedAt": "2026-11-22T10:00:00Z", "data": { "taskId": "0b9f6f2e-…" } },
    { "eventId": "…", "streamVersion": 2, "eventType": "TaskAssigned", "schemaVersion": 1,
      "recordedAt": "…", "data": { "agentId": "agent-demo-1" } },
    { "eventId": "…", "streamVersion": 3, "eventType": "CodeGenerated", "schemaVersion": 1,
      "recordedAt": "…", "data": { "generationId": "g1", "artifactRef": "synthetic://artifacts/demo/g1" } }
  ]
}
```

`data` is built from the **mapped Domain event**, never from the raw stored payload: `TaskCreated {taskId}`, `TaskAssigned {agentId}`, `CodeGenerated {generationId, artifactRef}`, `TestsPassed {generationId}`, `TestsFailed {generationId}`, `TaskCompleted {}`. Raw `metadata` (including any correlation id) is not returned. An empty stream is `404`, not an empty list.

## 5. HTTP error and concurrency mapping

Errors use RFC 9457 **Problem Details** (`application/problem+json`, `AddProblemDetails`). Each carries `status`, a short fixed `title`, a stable extension member **`code`**, and the framework `traceId`. `type` is a stable `urn:eventforge:problem:<code>` (no invented URLs). `detail` is a fixed safe sentence; it never contains payloads, identifiers from stored data, SQL, exception messages or connection details. Optional extensions: `currentVersion` (int, only when known), `reason`/`position` (invalid history), `errors` (field → messages, `400` only).

| Situation (source) | Status | `code` | Notes |
| --- | --- | --- | --- |
| `CommandSucceeded` | `201` create / `200` others | — | Response sent after commit only. |
| Malformed JSON, unknown member, missing/invalid field, bad GUID/`outcome`/`artifactRef`/header | `400` | `invalid_request` | `errors` names fields. Validation (including `expectedVersion` ≥ 0) runs before any I/O. |
| Wrong content type | `415` | (framework) | Not customized. |
| Task absent on `GET` | `404` | `task_not_found` | State and history. |
| `CommandRejected(TaskNotFound)` | `404` | `task_not_found` | Only when `expectedVersion` was 0. |
| `VersionConflict(v)` | `409` | `version_conflict` | `currentVersion = v` only if `v` is not `null`. Client reloads and reassesses; no automatic retry. |
| `CommandRejected(other)` | `409` | `workflow_rule_violation` | Extension `rule` = stable `RejectionCode` name (e.g. `TestsNotPassed`); no events appended. `TaskAlreadyExists` (reachable at handler level only when `expectedVersion` equals the current version) maps here; through `POST /api/tasks` a duplicate create yields `version_conflict` (see Section 2). |
| `CommandRejected(TaskIdMismatch)` | `500` | `internal_error` | Cannot happen with route-derived ids unless history is inconsistent; not a client error. |
| `InvalidHistory(reason, position)`, `EventStreamCorruptedException` on `GET` | `500` | `invalid_event_history` | Safe `reason` (`StreamCorrupted`, `UnknownEventType`, `UnsupportedSchemaVersion`, `MalformedPayload`, `InvalidSequence`) and `position` only; no partial state or history is ever returned. |
| `DuplicateEventId` | `500` | `duplicate_event_id` | Server-generated ids make this an internal fault; nothing was written; no automatic retry. |
| `CommitUnknown` | `500` | `commit_outcome_unknown` | The command may or may not be committed. Detail tells the client to `GET` the task/events and compare `version` before deciding. Never retried by the server; resending is not safe (no idempotency). |
| Anything else unexpected (including `NpgsqlException`, not-configured connection string, unavailable database) | `500` | `internal_error` | Global exception handler, no exception text in the body; full details only in server logs. |

**Infrastructure failures (owner decision).** Unexpected infrastructure failures return a safe `500 internal_error`. No `503` is used, and no dedicated exception class or exception hierarchy is introduced for it: `EventForge.Api` does not catch Npgsql types (confined to Infrastructure), and a missing connection string surfaces as a plain `InvalidOperationException`.

**Concurrency semantics (documented to clients):** optimistic concurrency only. `expectedVersion` is the version the client last read; a mismatch is always `409` and nothing is written. Racing writers with the same expected version have exactly one winner; the loser gets `409` (`currentVersion` omitted when unknown). The API makes **no durable idempotency claim**: a retry after a lost response may yield `409 version_conflict` or `409 workflow_rule_violation`, which does not reveal whether the first attempt committed. Client cancellation propagates the request `CancellationToken`; cancellation during commit is reported by the store as `CommitUnknown`, otherwise nothing was written.

## 6. Task state and event-history query approach

`TaskQueryHandler` (EVF-34) offers `GetStateAsync(TaskId)` and `GetHistoryAsync(TaskId)`:

1. `LoadStreamAsync(new StreamKey("Task", id))`; `EventStreamCorruptedException` → invalid history.
2. Version 0 → not found.
3. Run the **shared fold** (Section 3): count/position/stream-identity checks, `TaskEventMapper.FromStored`, `TaskAggregate.Apply` for **every** event, failing on the first fault.
4. State query returns the folded `TaskState`; history query pairs each `StoredEvent` envelope (`eventId`, `streamVersion`, `eventType`, `schemaVersion`, `recordedAt`) with its mapped event.

The history query folds the whole stream even though it only lists events, so an invalid sequence (for example `TaskCompleted` without a passed test) fails explicitly instead of being displayed as if it were fine. Reads use committed primary-database data (immediate local consistency). Reading and folding the full stream on every request is an **intentional small-demo trade-off**: O(n) per request, no snapshots, no pagination, not suitable for large histories.

## 7. Swagger UI and OpenAPI approach

Approved requirement; **not installed or configured in EVF-32.** Integration is EVF-33.

- **Document generation:** Microsoft's built-in `Microsoft.AspNetCore.OpenApi` (`AddOpenApi()`, `MapOpenApi()` serving `/openapi/v1.json`).
- **Interactive UI:** Swagger UI at `http://localhost:5080/swagger` (existing `http` launch profile port), pointed at `/openapi/v1.json`. The UI needs one NuGet package (candidate: `Swashbuckle.AspNetCore.SwaggerUI`, UI only, no Swashbuckle document generation). `AGENTS.md` requires a provenance, licence and vulnerability review before adoption; EVF-33 performs compatibility, provenance, security and licensing checks and records the result; package versions are selected there, not here. Swagger UI is **required**. No new ADR is needed solely for a minor Swagger UI or API-testing package addition; significant architectural changes still require owner approval and an ADR.
- **Exposure:** OpenAPI document and Swagger UI are mapped **only when `IsDevelopment()`**. `/health` is unaffected.
- **Organization:** two tags via the group — `Task Commands` (the five POST routes) and `Task Queries` (the two GET routes). Per endpoint: `WithSummary`, `WithDescription`, `Produces<T>` for success and `ProducesProblem(...)` for 400/404/409/500. Request examples use only synthetic values (`agent-demo-1`, `g1`, `synthetic://…`) and an `expectedVersion` description ("the `version` returned by your last call").
- **Executable end to end:** the complete lifecycle must be runnable from Swagger UI by copying `version` from each response into the next request, including the failure/recovery path.
- **Not doing:** API versioning, XML-doc pipelines, custom schema filters, authentication UI, code generation.
- **Verify in EVF-33 (not assumed here):** that the chosen UI package renders the OpenAPI version emitted by .NET 10's generator, and that nullable `expectedVersion` and the `outcome` values appear correctly in the schema.

## 8. Local synthetic-demo boundary

The API is for an **isolated local educational demonstration only**. It has **no authentication or authorization** and must not be exposed publicly or deployed on an untrusted or shared network. No JWT/OAuth/identity provider, multi-tenancy, rate limiting or production observability is added. Data are synthetic only: no real code, credentials, personal data or confidential references (enforced for `artifactRef` by the boundary rule above; `agentId` is opaque and the caller is responsible for synthetic values). Bind to loopback as today (`localhost:5080`; Compose publishes loopback ports). The API never returns raw SQL errors, connection strings, stored payloads or metadata.

This deliberately defers the planned-but-unimplemented authorization of `architecture.md` Section 7 ("application entry points enforce the caller's permission"); that item remains required before any shared deployment.

## 9. DI and startup strategy

Registered explicitly in `Program.cs` (EVF-33/34), built-in container only, `ValidateOnBuild`/`ValidateScopes` kept:

- `IEventStore` → `PostgreSqlEventStore` (singleton; stateless, depends on the lazily created `PostgreSqlDatabase` singleton).
- `TaskCommandHandler` → singleton via factory `new TaskCommandHandler(sp.GetRequiredService<IEventStore>(), Guid.NewGuid)` (a `Func<Guid>` parameter is not auto-resolved by the container).
- `TaskQueryHandler` → singleton (stateless).
- `AddProblemDetails()`, an exception handler returning the safe `internal_error` problem, JSON options (camelCase, unmapped members disallowed), group mapping, and development-only OpenAPI/Swagger UI.

Startup stays possible without credentials: `PostgreSqlDatabase` is created on first resolution, so the host starts, `/health` returns `200`, and only the first workflow request fails (`500 internal_error`, no credentials in the body). `/health` remains process liveness only. `--verify-database` is parsed and removed before host configuration exactly as now, runs before any endpoint is mapped, and its output and exit codes are unchanged. The API never migrates the schema at startup. No hosted service, background worker or extra endpoint is added. To make `Program` testable with `WebApplicationFactory`, EVF-34 needs a `public partial class Program` marker (or `InternalsVisibleTo`) and the `Microsoft.AspNetCore.Mvc.Testing` test package (compatibility, provenance, security and licensing checks required; no ADR solely for it).

## 10. Verification matrix

PostgreSQL-backed tests follow the existing `PostgresFact` opt-in pattern and isolated-database guard; non-database tests use fakes of `IEventStore`. No test is written in EVF-32.

| Behavior | Level | EVF |
| --- | --- | --- |
| Request DTO validation: missing/negative `expectedVersion`, bad GUID, blank/overlong/control-character ids, bad `outcome`, non-synthetic `artifactRef`, unknown member, malformed JSON, bad correlation header → `400`, no I/O | unit / in-memory API | 33 |
| Result→HTTP mapping table (every `TaskCommandResult`, every `RejectionCode`, `currentVersion` omitted when `null`, safe bodies) | unit | 33 |
| Swagger UI at `/swagger` and `/openapi/v1.json` available in Development, absent otherwise; seven routes and two tags present | API integration | 33 |
| `/health` still `200` without a connection string; `--verify-database` behavior and exit codes unchanged; host starts without credentials | API integration / existing | 33 |
| Shared fold extraction leaves existing handler tests green | existing unit | 34 |
| Query handler: missing task, state reconstruction, ordered history, mapped-only `data`, no metadata exposure | unit + PostgreSQL | 34 |
| Corrupted/unsupported stored events (unknown type, schema v2, malformed payload, gap, invalid sequence) → explicit `500 invalid_event_history`, no partial result, for state, history and commands | unit + PostgreSQL | 34 |
| Successful lifecycle over HTTP (create→assign→generate→pass→complete) with matching `GET` state and history | API + PostgreSQL | 35 |
| Failed generation, then new generation `g2`, passes, completes; completion blocked until `g2` passes | API + PostgreSQL | 35 |
| Invalid transition (complete without passed test) → `409 workflow_rule_violation`, no new event, version unchanged | API + PostgreSQL | 35 |
| Stale `expectedVersion` → `409 version_conflict` with `currentVersion`, nothing appended; stale wins over domain rule | API + PostgreSQL | 35 |
| Concurrent writers, same expected version: one `200`, one `409` (no `currentVersion` guessed), contiguous stream | API + PostgreSQL | 35 |
| Response bodies of successful commands contain exactly `taskId` and `version` (no events, state or metadata) | API | 33 |
| Duplicate create via `POST /api/tasks` → `409 version_conflict`; `404` for missing task (`GET`, and command with `expectedVersion` 0) | API + PostgreSQL | 35 |
| `commit_outcome_unknown` and `duplicate_event_id` mapped safely, never retried (store fake that throws/returns them; call count asserted) | API + fake store | 35 |
| Unexpected exception → safe `500`, no exception text, SQL or connection string in body | API | 35 |
| Architecture tests still pass (no ASP.NET/Npgsql in Domain/Application; Npgsql only in Infrastructure) | existing | 33–35 |

## 11. Task 6 compatibility, discrepancies and limitations

**Task 6 compatibility.** `GET /api/tasks/{id}` and its response DTO are the stable contract. In Task 6 only the implementation behind `TaskQueryHandler.GetStateAsync` changes (read `task_overview`: `task_id`, `status`, `assigned_agent_id`, `current_generation_id`, `latest_test_result`, `last_stream_version`); the route, DTO and error mapping stay. The event-history query keeps reading the Event Store. Command contracts do not change; inline projection failure rolls back the append and surfaces as a normal failure (`500`), and success is reported after commit. The Task 5 fold-based state read then serves as an independent reference for projection tests.

**Discrepancies with existing documents (no document changed here):**

1. `architecture.md` Section 5 states `GET /tasks/{id}` reads `task_overview` and "does not deserialize the full event stream on every request", and uses `/tasks` paths. The approved Task 5 design reads the event stream and uses `/api/tasks`. *Smallest resolution:* keep the approved design for Task 5 and update `architecture.md` (and the roadmap Task 5 status) when EVF-33 to EVF-35 are verified; Task 6 restores the projection read for state.
2. `architecture.md` Section 7 expects per-operation authorization at entry points; none exists. *Resolution:* the demo boundary in Section 8, to be re-opened before any shared deployment.

**Confirmed owner decisions (no longer pending):**

- The `synthetic://` artifact reference restriction at the API boundary is approved (Section 4).
- Extracting the existing fold into a small shared Application function in EVF-34 is approved (Section 3).
- Swagger UI is required and is delivered in EVF-33 (Section 7).
- New NuGet packages (Swagger UI, `Microsoft.AspNetCore.Mvc.Testing`) require compatibility, provenance, security and licensing checks. No ADR is required solely for minor Swagger UI or API-testing package additions; significant architectural changes still need owner approval and an ADR. `architecture.md` Section 2 lists Npgsql as the only approved production package and should be updated with those packages when they are adopted.
- No dedicated exception or `503` mapping is introduced; unexpected infrastructure failures return `500 internal_error` (Section 5).

**Intentional limitations:** full-stream read per query (no snapshots, pagination or caching); no durable request idempotency, so unknown commit outcomes and retries after lost responses cannot be resolved automatically; no authentication; no API versioning; no online or concurrent-read consistency beyond committed primary-database reads; Swagger UI and OpenAPI are development-only conveniences, not a published API contract.
