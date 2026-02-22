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
- `ArcadeDbTaskMemoryWriter` writes `TaskRegistry` snapshots using ArcadeDB command API delete+insert operations and can bootstrap schema when `ArcadeDbAutoCreateSchema=true`.

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
