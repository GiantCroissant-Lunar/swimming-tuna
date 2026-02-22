# SwarmAssistant .NET Runtime (Phase 6)

## Projects

- `src/SwarmAssistant.Contracts`: shared task and messaging contracts.
- `src/SwarmAssistant.Runtime`: hosted runtime with Akka actor topology, Agent Framework role execution, Langfuse tracing, CLI-first execution routing, and AG-UI/A2UI streaming endpoints.
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
- `GET /ag-ui/recent` (latest buffered events)
- `POST /ag-ui/actions` (UI action ingress)
- `CoordinatorActor` emits A2UI payloads for:
- `createSurface` on task assignment
- `updateDataModel` on transitions/results/failures

Run and inspect stream:

```bash
DOTNET_ENVIRONMENT=Local dotnet run --project /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/src/SwarmAssistant.Runtime --no-launch-profile
curl -N http://127.0.0.1:5080/ag-ui/events
```

## A2A + ArcadeDB Scaffolding (Phase 6)

- Agent card endpoint can be toggled with `Runtime__A2AEnabled=true`.
- ArcadeDB integration points are configured via:
- `Runtime__ArcadeDbEnabled`
- `Runtime__ArcadeDbHttpUrl`
- `Runtime__ArcadeDbDatabase`

Storage operations are not wired yet; this phase introduces runtime contracts only.

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
