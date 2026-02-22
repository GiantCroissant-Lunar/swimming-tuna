# SwarmAssistant .NET Runtime Bootstrap (Phase 1)

## Projects

- `src/SwarmAssistant.Contracts`: shared task contracts.
- `src/SwarmAssistant.Runtime`: hosted runtime service bootstrap.
- `tests/SwarmAssistant.Runtime.Tests`: baseline test project.

## Build

```bash
dotnet build /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/SwarmAssistant.sln
```

## Run with Profile

```bash
DOTNET_ENVIRONMENT=Local dotnet run --project /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/src/SwarmAssistant.Runtime
DOTNET_ENVIRONMENT=SecureLocal dotnet run --project /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/src/SwarmAssistant.Runtime
DOTNET_ENVIRONMENT=CI dotnet run --project /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/src/SwarmAssistant.Runtime
```

## Phase 1 Scope

This phase creates the runtime skeleton and configuration profiles only.
Akka actor topology and Agent Framework integration are added in later phases.
