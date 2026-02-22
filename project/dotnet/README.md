# SwarmAssistant .NET Runtime (Phase 3)

## Projects

- `src/SwarmAssistant.Contracts`: shared task and messaging contracts.
- `src/SwarmAssistant.Runtime`: hosted runtime with Akka actor topology and Agent Framework role execution.
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

## Remaining Scope

Langfuse tracing hooks and provider-backed model execution adapters are planned for later phases.
