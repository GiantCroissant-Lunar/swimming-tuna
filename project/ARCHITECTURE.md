# MVP Architecture (CLI-First Swarm)

This MVP borrows patterns from the reference set under `/ref-projects` while keeping implementation minimal.

## Borrowed Patterns

- Typed role flow (`planner -> builder -> reviewer -> finalizer`) inspired by swarm role separation.
- Durable event log (`data/events.jsonl`) and task state (`data/tasks.json`) for replay/debug.
- Adapter routing with ordered fallbacks to avoid single-provider lock-in.

## Runtime Design

- `src/lib/adapters.mjs`: probes CLIs and executes role prompts with fallback.
- `src/lib/orchestrator.mjs`: runs deterministic step machine per task.
- `src/lib/store.mjs`: persists tasks/events.
- `src/index.mjs`: CLI entrypoint for `init`, `status`, and `run`.
- `dotnet/src/SwarmAssistant.Runtime`: Akka-based runtime with actor topology, AG-UI/A2UI endpoints, A2A task APIs, and task-memory persistence adapters.
- `godot-ui/`: Godot Mono surface renderer for AG-UI/A2UI events.
- `infra/langfuse`: local Langfuse stack with `local`, `secure-local`, and `ci` env profiles.

## Akka Runtime (Phase 2)

- Coordinator role: `CoordinatorActor`
- Worker role: `WorkerActor` (`planner`, `builder`)
- Reviewer role: `ReviewerActor`
- Supervision/escalation: `SupervisorActor`

Typed contracts in `dotnet/src/SwarmAssistant.Contracts/Messaging/SwarmMessages.cs`:
- `TaskAssigned`
- `TaskStarted`
- `TaskResult`
- `TaskFailed`
- `EscalationRaised`

## Agent Framework Runtime (Phase 3)

- `dotnet/src/SwarmAssistant.Runtime/Actors/AgentFrameworkRoleEngine.cs` executes role tasks through `Microsoft.Agents.AI.Workflows`.
- `WorkerActor` and `ReviewerActor` call the Agent Framework engine instead of generating role text inline.
- Execution mode is configured through `Runtime.AgentFrameworkExecutionMode`.

## Observability Runtime (Phase 4)

- `dotnet/src/SwarmAssistant.Runtime/Telemetry/RuntimeTelemetry.cs` configures OpenTelemetry tracing export to Langfuse OTLP (`/api/public/otel/v1/traces`).
- Actor spans are emitted for:
- `coordinator.task.assigned`
- `coordinator.task.transition`
- `coordinator.role.succeeded`
- `coordinator.role.failed`
- `worker.role.execute`
- `reviewer.role.execute`
- `supervisor.task.started`
- `supervisor.task.result`
- `supervisor.task.failed`
- `supervisor.escalation.raised`
- `agent-framework.role.execute`

## CLI-First Execution Runtime (Phase 5)

- `dotnet/src/SwarmAssistant.Runtime/Execution/SubscriptionCliRoleExecutor.cs` adds subscription-backed CLI adapter fallback (`copilot -> cline -> kimi -> local-echo`).
- `dotnet/src/SwarmAssistant.Runtime/Execution/RolePromptFactory.cs` produces deterministic role prompts for planner, builder, and reviewer roles.
- `dotnet/src/SwarmAssistant.Runtime/Execution/SandboxCommandBuilder.cs` maps runtime commands into `host`, `docker`, or `apple-container` wrappers.
- Runtime profiles now support `subscription-cli-fallback` mode for CLI-first execution without provider API keys.

## UI Protocol Runtime (Phase 6)

- `dotnet/src/SwarmAssistant.Runtime/Ui/UiEventStream.cs` provides in-memory pub/sub for AG-UI event streaming.
- `dotnet/src/SwarmAssistant.Runtime/Ui/A2UiPayloadFactory.cs` builds A2UI payloads (`createSurface`, `updateDataModel`) from task lifecycle events.
- `dotnet/src/SwarmAssistant.Runtime/Program.cs` exposes:
- `GET /ag-ui/events` (SSE stream)
- `GET /ag-ui/recent` (recent event buffer)
- `POST /ag-ui/actions` (UI action ingress)
- `CoordinatorActor` now emits AG-UI + A2UI events on assignment, transition, success, and failure.
- `godot-ui/scripts/Main.cs` consumes AG-UI updates via polling `GET /ag-ui/recent` and renders A2UI components in a windowed Godot app.
- A2UI component-to-scene mapping:
- `text` -> `godot-ui/scenes/components/A2TextComponent.tscn`
- `button` -> `godot-ui/scenes/components/A2ButtonComponent.tscn`

## A2A + ArcadeDB Runtime (Phase 7)

- `Program.cs` exposes A2A-compatible endpoints when `A2AEnabled=true`:
- `GET /.well-known/agent-card.json` (or custom `A2AAgentCardPath`)
- `POST /a2a/tasks` (submit task to coordinator)
- `GET /a2a/tasks/{taskId}` (task snapshot)
- `GET /a2a/tasks` (recent tasks list)
- `TaskRegistry` tracks in-memory task snapshots and role outputs (`planningOutput`, `buildOutput`, `reviewOutput`) plus summary/error state.
- `RuntimeActorRegistry` bridges HTTP endpoint handlers to the live coordinator actor ref.
- `ITaskMemoryWriter` abstraction is wired to `ArcadeDbTaskMemoryWriter` by default.
- `ArcadeDbTaskMemoryWriter` writes `TaskRegistry` snapshots using an atomic ArcadeDB `UPDATE ... UPSERT WHERE taskId = :taskId` command and can bootstrap schema when `ArcadeDbAutoCreateSchema=true`.

## AG-UI Action Loop (Phase 8)

- `POST /ag-ui/actions` now dispatches supported actions:
- `request_snapshot`: publish runtime or per-task snapshot events.
- `refresh_surface`: rebuild and publish A2UI surface for active task.
- `submit_task`: submit new tasks from UI payloads (`title`, `description`).
- `godot-ui/scripts/Main.cs` includes submit/snapshot/refresh controls and maps button-driven A2UI actions to `/ag-ui/actions`.
- Runtime emits `agui.action.task.submitted` and `agui.task.snapshot` events so the exported Godot app can confirm action round-trips.

## Memory Read Runtime (Phase 9)

- `ArcadeDbTaskMemoryReader` implements `ITaskMemoryReader` and reads `SwarmTask` snapshots from ArcadeDB command API.
- Runtime exposes memory APIs:
- `GET /memory/tasks?limit=<n>`
- `GET /memory/tasks/{taskId}`
- `POST /ag-ui/actions` now supports `load_memory` and publishes `agui.memory.tasks` with task summaries for UI consumption.
- `godot-ui/scripts/Main.cs` includes a `Load Memory` action that fetches persisted tasks and updates active task selection/log output.

## Startup Memory Bootstrap (Phase 10)

- `Worker` now restores persisted snapshots at runtime startup via `ITaskMemoryReader` when `MemoryBootstrapEnabled=true`.
- `TaskRegistry.ImportSnapshots(...)` seeds in-memory task state without re-writing memory storage.
- Runtime emits:
- `agui.memory.bootstrap` (restore summary)
- `agui.memory.bootstrap.failed` (restore failure)
- bootstrap-triggered `agui.ui.surface` / `agui.ui.patch` events for recent restored tasks.
- Demo task auto-submit is skipped when restored tasks already exist in registry.

## Task List UX + Bootstrap Hardening (Phase 11)

- Startup restore logic is extracted into `dotnet/src/SwarmAssistant.Runtime/Tasks/StartupMemoryBootstrapper.cs` so restore behavior can be tested independently from hosted runtime startup.
- `Worker` now delegates restore flow to `StartupMemoryBootstrapper` and uses `ShouldAutoSubmitDemoTask(...)` for deterministic demo submit gating.
- Runtime tests in `dotnet/tests/SwarmAssistant.Runtime.Tests/StartupMemoryBootstrapperTests.cs` cover:
- successful restore import + AG-UI bootstrap events
- restore failure event publication
- disabled restore no-op behavior
- demo auto-submit gate behavior
- `godot-ui/scripts/Main.cs` now renders a dedicated task list panel and supports row selection -> `request_snapshot` -> `refresh_surface` action chaining.
- `.github/workflows/phase11-health.md` adds a weekly gh-aw hygiene workflow to detect runtime/docs drift (non-blocking).

## .NET Aspire Orchestration (Phase 12)

- `project/dotnet/src/SwarmAssistant.AppHost` is the .NET Aspire orchestrator project that spins up the backend runtime, ArcadeDB, Langfuse (PostgreSQL), and the Godot UI executable.
- `project/dotnet/src/SwarmAssistant.ServiceDefaults` provides OpenTelemetry extensions for observability and service discovery.
- Use `task run:aspire` to boot the entire local development environment including frontend, backend, and databases.
- The `Runtime` and `Godot UI` are configured via environment variables to dynamically discover ports assigned by Aspire.

## Protocol Contracts (Phase 0)

Machine-readable contract definitions live in `/project/docs/ag-ui-contracts.json`.
That file is the **single source of truth** for all event types, action IDs, payload schemas, and versioning rules.
This section summarises the contracts for quick reference.

### Contract Version

| Artifact | Version |
|---|---|
| `contractVersion` | `0.1` |
| `a2uiProtocolVersion` | `a2ui/v0.8` |

### Event Envelope

Every AG-UI event is delivered in this wrapper:

```json
{
  "sequence": 42,
  "type": "agui.task.transition",
  "taskId": "task-abc123",
  "at": "2026-02-23T10:09:37.562Z",
  "payload": { ... }
}
```

`sequence` is monotonically increasing; use it for ordering and gap detection (do **not** use `at`).
`taskId` is `null` for runtime-scoped events.

### AG-UI Events

| Event type | Scope | Description |
|---|---|---|
| `agui.runtime.started` | runtime | Actor system ready; carries runtime profile info. |
| `agui.runtime.snapshot` | runtime | Task count summary (started/completed/failed/escalations). |
| `agui.task.submitted` | task | Task dispatched to a TaskCoordinatorActor. |
| `agui.task.transition` | task | Task state change or orchestrator decision point. |
| `agui.task.decision` | task | Reviewer passed or rejected the build output. |
| `agui.task.retry` | task | Supervisor-initiated role retry. |
| `agui.task.done` | task | Task completed successfully; carries final summary. |
| `agui.task.failed` | task | Task blocked or escalated; carries error detail. |
| `agui.task.snapshot` | task | Full task snapshot (in response to `request_snapshot`). |
| `agui.ui.surface` | task | Create/replace an A2UI surface in Godot. |
| `agui.ui.patch` | task | Incremental data-model update to an existing surface. |
| `agui.action.received` | any | Echo published on every `POST /ag-ui/actions` before processing. |
| `agui.action.acknowledged` | task | Deterministic ACK for accepted HITL intervention actions. |
| `agui.action.rejected` | task | Deterministic rejection for failed HITL intervention actions (includes reasonCode). |
| `agui.memory.bootstrap` | runtime | Startup memory restore summary. |
| `agui.memory.bootstrap.failed` | runtime | Startup memory restore failure. |
| `agui.memory.tasks` | runtime | Task summary list (bootstrap or `load_memory`). |
| `a2a.task.submitted` | task | Task submitted via A2A protocol (`A2AEnabled=true`). |

### AG-UI Actions

Actions are submitted as `POST /ag-ui/actions` with body `{ "actionId": "...", "taskId": "...", "payload": { ... } }`.

| `actionId` | Status | Requires `taskId` | Description |
|---|---|---|---|
| `request_snapshot` | implemented | optional | Request runtime or per-task snapshot event. |
| `refresh_surface` | implemented | required | Rebuild A2UI surface for the task. |
| `submit_task` | implemented | n/a (use `payload.taskId`) | Submit a new task; `payload` requires `title`. Optional `payload.taskId` sets the task ID (top-level `taskId` field is unused for this action). |
| `load_memory` | implemented | optional | Fetch persisted task list from ArcadeDB. |
| `approve_review` | implemented | required | HITL: approve reviewer output, advance to Done. |
| `reject_review` | implemented | required | HITL: reject reviewer output, mark failed. |
| `request_rework` | implemented | required | HITL: send back to Builder with feedback. |
| `pause_task` | implemented | required | Suspend a running task's role execution. |
| `resume_task` | implemented | required | Resume a paused task. |
| `set_subtask_depth` | implemented | required | Override max sub-task depth before planning starts. |

### A2UI Payloads (Godot Consumption)

A2UI payloads are embedded under the `a2ui` key of `agui.ui.surface` and `agui.ui.patch` events.

**`createSurface`** — full surface create/replace:
```json
{
  "protocol": "a2ui/v0.8",
  "operation": "createSurface",
  "surface": {
    "id": "task-surface-<taskId>",
    "title": "Swarm Task Monitor",
    "components": [ { "id": "...", "type": "text|button|vbox|...", "props": {...}, "children": [...] } ]
  }
}
```

**`updateDataModel`** — incremental patch:
```json
{
  "protocol": "a2ui/v0.8",
  "operation": "updateDataModel",
  "surfaceId": "task-surface-<taskId>",
  "dataModelPatch": { "status": "building", "updatedAt": "..." }
}
```

Supported component types: `text`, `rich_text`, `button`, `line_edit`, `progress_bar`, `separator`, `vseparator`, `vbox`, `hbox`, `panel`, `margin`, `scroll`, `grid`.
Godot `GenUiNodeFactory` maps each `type` to the corresponding scene in `godot-ui/scenes/components/`.

### HTTP Endpoints and Error Format

All error responses use the standard envelope:
```json
{ "error": "<message>", "taskId?": "...", "actionId?": "...", "supported?": ["..."] }
```

| Endpoint | Auth | Description |
|---|---|---|
| `GET /healthz` | none | Liveness probe. |
| `GET /ag-ui/events` | optional `X-API-Key` | SSE stream (`text/event-stream`). |
| `GET /ag-ui/recent?count=N` | optional `X-API-Key` | Last N events from ring buffer (max 200). |
| `POST /ag-ui/actions` | optional `X-API-Key` | Action ingress — see action table above. |
| `GET /memory/tasks?limit=N` | optional `X-API-Key` | Task snapshot list (ArcadeDB → registry fallback). |
| `GET /memory/tasks/{taskId}` | optional `X-API-Key` | Single task snapshot. |
| `POST /a2a/tasks` | optional `X-API-Key` | A2A task submission (`A2AEnabled=true`). |
| `GET /a2a/tasks/{taskId}` | optional `X-API-Key` | A2A task snapshot. |
| `GET /a2a/tasks?limit=N` | optional `X-API-Key` | A2A task list. |
| `GET /.well-known/agent-card.json` | none | A2A capability card (`A2AEnabled=true`). |

HTTP status codes used by `POST /ag-ui/actions`:

| Status | Meaning |
|---|---|
| 200 | `load_memory` success (returns `{ source, count }`). |
| 202 | Action accepted; result published as AG-UI event. |
| 400 | Missing required field or unsupported `actionId`. |
| 401 | `X-API-Key` header required but missing or incorrect. |
| 404 | Referenced `taskId` not found. |
| 409 | Task is in a state that does not allow the requested action (planned actions). |
| 503 | Coordinator actor not yet available (`submit_task`). |

### Event Sequencing and Ordering Guarantees

- Events within a single task are published in order; `sequence` is server-assigned and monotonically increasing.
- The SSE stream replays all events in the 200-event ring buffer on new subscriber connect.
- Gaps in `sequence` values are possible on buffer overflow; consumers should log but not fail.
- `agui.ui.surface` always precedes `agui.ui.patch` for the same surface in a lifecycle run.
- `agui.task.done` and `agui.task.failed` are terminal; no further task-scoped events follow.
- `agui.action.received` is always the first event for every `POST /ag-ui/actions` call.

Typical lifecycle order per task:
```
agui.task.submitted → agui.ui.surface
→ agui.task.transition (Queued→Planning)
→ agui.ui.patch (planner output)
→ agui.task.transition (Planning→Building)
→ agui.ui.patch (builder output)
→ agui.task.transition (Building→Reviewing)
→ agui.task.decision
→ agui.ui.patch (reviewer output)
→ agui.task.transition (Reviewing→Done)
→ agui.task.done
```

### Idempotency Rules

- `agui.ui.surface`: treat as full surface reset; duplicate surface IDs replace the previous surface.
- `agui.ui.patch`: apply `dataModelPatch` as a shallow merge; **ignore unknown keys**.
- `agui.task.done` / `agui.task.failed`: tolerate receiving more than once after reconnect replay.
- `agui.memory.bootstrap`: published at most once per runtime startup.

### Compatibility Rules

- Consumers **MUST** ignore unknown fields in event payloads (open-world model).
- Consumers **MUST NOT** fail on unknown event types; log and skip.
- Required fields defined in `ag-ui-contracts.json` must always be present.
- Use `sequence` for ordering; `at` is informational only.

### How to Evolve Contracts Safely

1. **Update `ag-ui-contracts.json` first**, in a dedicated commit/PR.
2. Mark new event types or actions as `"status": "planned"` until both runtime and Godot handle them; only then set `"implemented"`.
3. Adding **optional** fields to an existing event payload is a **MINOR** bump (`contractVersion` `0.x → 0.x+1`); no breaking change.
4. Adding a **required** field on an existing event is a **MAJOR** bump; coordinate runtime and Godot migration before merging.
5. Renaming or removing an event type or field is **BREAKING**; bump `contractVersion` MAJOR and document a migration note.
6. Before merging, run: `rg 'agui\.' project/dotnet/src --include '*.cs'` and verify every emitted event type appears in `ag-ui-contracts.json`.
7. Update this section's version table when the contract version changes.

## Provider Strategy

Priority is subscription-backed local CLIs:

1. Copilot CLI
2. Cline CLI
3. Kimi CLI
4. Local fallback adapter

No direct provider API key integration is included in this MVP.

## Known Constraints

- Adapter probes only validate command availability; execution can still fail on auth or environment restrictions.
- In restricted sandboxes, CLIs that need sockets/process brokers may fail and fallback will trigger.
