# SwarmAssistant .NET Runtime (Phase 10)

## Projects

- `src/SwarmAssistant.Contracts`: shared task and messaging contracts.
- `src/SwarmAssistant.Runtime`: hosted runtime with Akka actor topology, Agent Framework role execution, Langfuse tracing, CLI-first execution routing, AG-UI/A2UI streaming endpoints, A2A task APIs, and ArcadeDB persistence integration.
- `tests/SwarmAssistant.Runtime.Tests`: lifecycle/state-machine tests.

## Actor Topology (Phase 2)

- `CoordinatorActor`: owns task lifecycle and routing.
- `WorkerActor`: planner + builder role execution.
- `ReviewerActor`: review role execution.
- `SupervisorActor`: receives task events, failures, and escalations.

Lifecycle: `queued -> planning -> building -> reviewing -> done|blocked`.

## Agent Framework Integration (Phase 3)

- Role execution path is implemented with `Microsoft.Agents.AI.Workflows`.
- `AgentFrameworkRoleEngine` runs each role via `InProcessExecution.StreamAsync(...)`.
- No provider API key is required for this phase; workflow executors are deterministic and local.

## Langfuse Tracing (Phase 4)

- `RuntimeTelemetry` configures OpenTelemetry OTLP export to Langfuse.
- Actor lifecycle spans are emitted from coordinator/worker/reviewer/supervisor paths.
- Role execution spans are emitted from `AgentFrameworkRoleEngine`.

To enable tracing, set runtime env vars:

```bash
export Runtime__LangfuseTracingEnabled=true
export Runtime__LangfusePublicKey=pk-lf-...
export Runtime__LangfuseSecretKey=sk-lf-...
export Runtime__LangfuseOtlpEndpoint=http://localhost:3000/api/public/otel/v1/traces
```

## CLI-First Role Routing (Phase 5)

- `AgentFrameworkRoleEngine` now supports `subscription-cli-fallback` mode in addition to `in-process-workflow`.
- `SubscriptionCliRoleExecutor` executes roles with ordered adapters:
- `copilot`
- `cline`
- `kimi`
- `local-echo` (deterministic fallback)
- Local profile defaults to `subscription-cli-fallback` with `SandboxMode=host`.
- Secure sandbox modes (`docker`, `apple-container`) are supported through configurable wrapper commands.

Example env overrides:

```bash
export Runtime__AgentFrameworkExecutionMode=subscription-cli-fallback
export Runtime__SandboxMode=docker
export Runtime__DockerSandboxWrapper__Command=docker
export Runtime__DockerSandboxWrapper__Args__0=run
export Runtime__DockerSandboxWrapper__Args__1=--rm
export Runtime__DockerSandboxWrapper__Args__2=my-tools-image
export Runtime__DockerSandboxWrapper__Args__3=sh
export Runtime__DockerSandboxWrapper__Args__4=-lc
export Runtime__DockerSandboxWrapper__Args__5={{command}} {{args_joined}}
```

## AG-UI + A2UI Gateway (Phase 6)

- Runtime now hosts AG-UI-compatible endpoints:
- `GET /ag-ui/events` (SSE stream)
- `GET /ag-ui/recent` (latest buffered events, optional `count` query param)
- `POST /ag-ui/actions` (UI action ingress)
- `CoordinatorActor` emits A2UI payloads for:
- `createSurface` on task assignment
- `updateDataModel` on transitions/results/failures

Run and inspect stream:

```bash
DOTNET_ENVIRONMENT=Local dotnet run --project /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/src/SwarmAssistant.Runtime --no-launch-profile
curl -N http://127.0.0.1:5080/ag-ui/events
curl -s 'http://127.0.0.1:5080/ag-ui/recent?count=100'
```

Supported AG-UI actions (Phase 10):

- `request_snapshot`
- `refresh_surface`
- `submit_task` (payload requires `title`; optional `description`)
- `load_memory` (payload supports optional `limit`)

Startup memory bootstrap (Phase 10):

- Enabled by `Runtime__MemoryBootstrapEnabled=true`
- Restore size controlled by `Runtime__MemoryBootstrapLimit` (default `200`)
- Startup emits `agui.memory.bootstrap` or `agui.memory.bootstrap.failed`
- Demo task submission is skipped when restored tasks already exist

## A2A + ArcadeDB Integration (Phase 7)

- A2A endpoints are enabled with `Runtime__A2AEnabled=true`:
- `GET /.well-known/agent-card.json` (or custom `Runtime__A2AAgentCardPath`)
- `POST /a2a/tasks`
- `GET /a2a/tasks/{taskId}`
- `GET /a2a/tasks`
- `TaskRegistry` captures lifecycle transitions and role outputs for both actor-driven and API-submitted tasks.
- `ArcadeDbTaskMemoryWriter` persists snapshots as `SwarmTask` records using ArcadeDB command API delete+insert writes.
- ArcadeDB runtime configuration:
- `Runtime__ArcadeDbEnabled`
- `Runtime__ArcadeDbHttpUrl`
- `Runtime__ArcadeDbDatabase`
- `Runtime__ArcadeDbUser`
- `Runtime__ArcadeDbPassword`
- `Runtime__ArcadeDbAutoCreateSchema`

Start ArcadeDB local stack before enabling runtime persistence:

```bash
cd /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/infra/arcadedb
docker compose --env-file env/local.env up -d
```

Example task flow:

```bash
curl -s -X POST http://127.0.0.1:5080/a2a/tasks \
  -H 'content-type: application/json' \
  -d '{"title":"Phase 10 endpoint test","description":"validate task registry + ArcadeDB write/read"}'
curl -s http://127.0.0.1:5080/a2a/tasks
curl -s http://127.0.0.1:5080/a2a/tasks/<task-id>
```

Memory-read APIs (Phase 10):

```bash
curl -s 'http://127.0.0.1:5080/memory/tasks?limit=20'
curl -s http://127.0.0.1:5080/memory/tasks/<task-id>
curl -s -X POST http://127.0.0.1:5080/ag-ui/actions \
  -H 'content-type: application/json' \
  -d '{"actionId":"load_memory","payload":{"limit":20}}'
```

## Build

```bash
dotnet build /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/SwarmAssistant.sln
dotnet test /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/SwarmAssistant.sln
```

## Run with Profile

```bash
DOTNET_ENVIRONMENT=Local dotnet run --project /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/src/SwarmAssistant.Runtime
DOTNET_ENVIRONMENT=SecureLocal dotnet run --project /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/src/SwarmAssistant.Runtime
DOTNET_ENVIRONMENT=CI dotnet run --project /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/src/SwarmAssistant.Runtime
```

## Runtime Flags

From `Runtime` config:

- `AutoSubmitDemoTask`
- `DemoTaskTitle`
- `DemoTaskDescription`
- `SimulateBuilderFailure`
- `SimulateReviewerFailure`
- `LangfuseTracingEnabled`
- `LangfusePublicKey`
- `LangfuseSecretKey`
- `LangfuseOtlpEndpoint`
- `RoleExecutionTimeoutSeconds`
- `CliAdapterOrder`
- `DockerSandboxWrapper`
- `AppleContainerSandboxWrapper`
- `AgUiEnabled`
- `AgUiBindUrl`
- `AgUiProtocolVersion`
- `A2AEnabled`
- `A2AAgentCardPath`
- `ArcadeDbEnabled`
- `ArcadeDbHttpUrl`
- `ArcadeDbDatabase`
- `ArcadeDbUser`
- `ArcadeDbPassword`
- `ArcadeDbAutoCreateSchema`
- `MemoryBootstrapEnabled`
- `MemoryBootstrapLimit`
