# Swimming Tuna MVP

Swarm assistant MVP implemented under `/project` with a CLI-first layer and a .NET runtime bootstrap.

## Goals

- Prioritize subscription-backed CLIs before API keys.
- Build toward `Akka.NET + Microsoft Agent Framework + Langfuse` with container isolation.
- Keep durable local state for tasks and event logs.

## Current Layout

- `src/`: JavaScript CLI-first MVP (`planner -> builder -> reviewer -> finalizer`).
- `dotnet/`: .NET runtime with Akka actor topology + Agent Framework role execution + Langfuse tracing hooks (Phase 4).
- `infra/langfuse/`: Docker stack and environment profiles for Langfuse.

## JavaScript MVP Commands

From repository root:

```bash
npm --prefix /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project run init
npm --prefix /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project run status
npm --prefix /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project run run -- --task "Design MVP contracts" --desc "Focus on role state machine and event schema"
```

## .NET Runtime Commands (Phase 4)

```bash
dotnet build /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/SwarmAssistant.sln
dotnet test /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/SwarmAssistant.sln
DOTNET_ENVIRONMENT=Local dotnet run --project /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/src/SwarmAssistant.Runtime
```

Available .NET runtime profiles:

- `Local`
- `SecureLocal`
- `CI`

Runtime config includes:

- `AutoSubmitDemoTask`
- `DemoTaskTitle`
- `DemoTaskDescription`
- `SimulateBuilderFailure`
- `SimulateReviewerFailure`
- `AgentFrameworkExecutionMode`
- `LangfuseTracingEnabled`
- `LangfusePublicKey`
- `LangfuseSecretKey`
- `LangfuseOtlpEndpoint`

Enable tracing with environment variables:

```bash
export Runtime__LangfuseTracingEnabled=true
export Runtime__LangfusePublicKey=pk-lf-...
export Runtime__LangfuseSecretKey=sk-lf-...
export Runtime__LangfuseOtlpEndpoint=http://localhost:3000/api/public/otel/v1/traces
```

## Langfuse Stack Commands (Phase 1)

```bash
cd /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/infra/langfuse
docker compose --env-file env/local.env up -d
docker compose --env-file env/local.env down
```

## Adapter Notes

Update `config/swarm.config.json` command templates to match your installed tools.

- `copilot` uses the modern `copilot --prompt ...` flow.
- `cline` and `kimi` are configurable command adapters.
- No provider API key integration is implemented in this MVP by design.
