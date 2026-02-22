# SwarmAssistant .NET Runtime (Phase 4)

## Projects

- `src/SwarmAssistant.Contracts`: shared task and messaging contracts.
- `src/SwarmAssistant.Runtime`: hosted runtime with Akka actor topology, Agent Framework role execution, and Langfuse tracing.
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

## Remaining Scope

Provider-backed model execution adapters and prompt/eval pipelines are planned for later phases.
