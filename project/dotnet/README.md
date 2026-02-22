# SwarmAssistant .NET Runtime (Phase 2)

## Projects

- `src/SwarmAssistant.Contracts`: shared task and messaging contracts.
- `src/SwarmAssistant.Runtime`: hosted runtime with Akka actor topology.
- `tests/SwarmAssistant.Runtime.Tests`: lifecycle/state-machine tests.

## Actor Topology (Phase 2)

- `CoordinatorActor`: owns task lifecycle and routing.
- `WorkerActor`: planner + builder role execution.
- `ReviewerActor`: review role execution.
- `SupervisorActor`: receives task events, failures, and escalations.

Lifecycle: `queued -> planning -> building -> reviewing -> done|blocked`.

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

Agent Framework execution integration and Langfuse tracing hooks are planned for later phases.
