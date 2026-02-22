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
- `dotnet/src/SwarmAssistant.Runtime`: Akka-based runtime with actor topology.
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
