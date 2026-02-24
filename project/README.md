# Swimming Tuna MVP

Swarm assistant MVP implemented under `/project` with a CLI-first layer and a .NET runtime bootstrap.

## Goals

- Prioritize subscription-backed CLIs before API keys.
- Build toward `Akka.NET + Microsoft Agent Framework + Langfuse` with container isolation.
- Keep durable local state for tasks and event logs.

## Current Layout

- `src/`: JavaScript CLI-first MVP (`planner -> builder -> reviewer -> finalizer`).
- `dotnet/`: .NET runtime with Akka actor topology + Agent Framework role execution + Langfuse tracing hooks + CLI-first adapter routing + AG-UI/A2UI gateway + A2A task endpoints + ArcadeDB write/read path + AG-UI action loop + startup memory bootstrap hardening (Phase 11).
- `godot-ui/`: Godot Mono client that polls AG-UI recent events, renders A2UI payloads, maintains a task list panel, and sends AG-UI actions back to runtime.
- `infra/langfuse/`: Docker stack and environment profiles for Langfuse.
- `infra/arcadedb/`: Phase 7 task-persistence notes for ArcadeDB command API wiring.

## UI/UX design system (optional)

If you want to pair A2UI/GenUI surfaces with a generated design system (e.g., via `ui-ux-pro-max`), see:

- `docs/ui-ux-pro-max-a2ui-genui.md`

## JavaScript MVP Commands

From repository root:

```bash
npm --prefix /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project run init
npm --prefix /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project run status
npm --prefix /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project run run -- --task "Design MVP contracts" --desc "Focus on role state machine and event schema"
```

## .NET Runtime Commands (Phase 11)

```bash
dotnet build /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/SwarmAssistant.sln
dotnet test /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/SwarmAssistant.sln
DOTNET_ENVIRONMENT=Local dotnet run --project /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/src/SwarmAssistant.Runtime --no-launch-profile
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
- `RoleExecutionTimeoutSeconds`
- `CliAdapterOrder`
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

Enable CLI-first role execution with subscription adapters:

```bash
export Runtime__AgentFrameworkExecutionMode=subscription-cli-fallback
export Runtime__SandboxMode=host
```

For containerized execution, configure wrapper commands:

```bash
export Runtime__SandboxMode=docker
export Runtime__DockerSandboxWrapper__Command=docker
export Runtime__DockerSandboxWrapper__Args__0=run
export Runtime__DockerSandboxWrapper__Args__1=--rm
export Runtime__DockerSandboxWrapper__Args__2=my-tools-image
export Runtime__DockerSandboxWrapper__Args__3=sh
export Runtime__DockerSandboxWrapper__Args__4=-lc
export Runtime__DockerSandboxWrapper__Args__5={{command}} {{args_joined}}
```

AG-UI endpoints (when runtime is up):

```bash
curl -s http://127.0.0.1:5080/healthz
curl -s 'http://127.0.0.1:5080/ag-ui/recent?count=100'
curl -N http://127.0.0.1:5080/ag-ui/events
curl -s -X POST http://127.0.0.1:5080/ag-ui/actions -H 'content-type: application/json' -d '{"taskId":"manual-test","actionId":"request_snapshot","payload":{"source":"cli"}}'
```

AG-UI action examples:

```bash
curl -s -X POST http://127.0.0.1:5080/ag-ui/actions -H 'content-type: application/json' -d '{"actionId":"request_snapshot","payload":{"source":"cli"}}'
curl -s -X POST http://127.0.0.1:5080/ag-ui/actions -H 'content-type: application/json' -d '{"actionId":"refresh_surface","taskId":"<task-id>","payload":{"source":"cli"}}'
curl -s -X POST http://127.0.0.1:5080/ag-ui/actions -H 'content-type: application/json' -d '{"actionId":"submit_task","payload":{"title":"UI Submitted Task","description":"Created from AG-UI action"}}'
curl -s -X POST http://127.0.0.1:5080/ag-ui/actions -H 'content-type: application/json' -d '{"actionId":"load_memory","payload":{"source":"cli","limit":20}}'
```

A2A endpoints (when `Runtime__A2AEnabled=true`):

```bash
curl -s http://127.0.0.1:5080/.well-known/agent-card.json
curl -s -X POST http://127.0.0.1:5080/a2a/tasks -H 'content-type: application/json' -d '{"title":"Phase 11 API test","description":"submit via A2A endpoint"}'
curl -s http://127.0.0.1:5080/a2a/tasks
curl -s http://127.0.0.1:5080/a2a/tasks/<task-id>
curl -s 'http://127.0.0.1:5080/memory/tasks?limit=20'
curl -s http://127.0.0.1:5080/memory/tasks/<task-id>
```

Enable ArcadeDB persistence:

```bash
export Runtime__ArcadeDbEnabled=true
export Runtime__ArcadeDbHttpUrl=http://127.0.0.1:2480
export Runtime__ArcadeDbDatabase=swarm_assistant
export Runtime__ArcadeDbUser=root
export Runtime__ArcadeDbPassword=<password>
export Runtime__ArcadeDbAutoCreateSchema=true
export Runtime__MemoryBootstrapEnabled=true
export Runtime__MemoryBootstrapLimit=200
```

Run Godot windowed client:

```bash
/Users/apprenticegc/Work/lunar-horse/tools/Godot_mono.app/Contents/MacOS/Godot --path /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/godot-ui --windowed --resolution 1280x720
```

Export and run macOS app:

```bash
/Users/apprenticegc/Work/lunar-horse/tools/Godot_mono.app/Contents/MacOS/Godot --headless --path /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/godot-ui --export-debug "macOS" /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/build/godot-ui/SwarmAssistantUI.app
"/Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/build/godot-ui/SwarmAssistantUI.app/Contents/MacOS/SwarmAssistant UI" --windowed --resolution 1280x720
```

## Langfuse Stack Commands (Phase 1)

```bash
cd /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/infra/langfuse
docker compose --env-file env/local.env up -d
docker compose --env-file env/local.env down
```

## ArcadeDB Stack Commands (Phase 7)

```bash
cd /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/infra/arcadedb
docker compose --env-file env/local.env up -d
docker compose --env-file env/local.env down
```

Run end-to-end persistence verification:

```bash
/Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/infra/arcadedb/scripts/smoke-e2e.sh
```

## Adapter Notes

Update `config/swarm.config.json` command templates to match your installed tools.

- `copilot` uses the modern `copilot --prompt ...` flow.
- `cline` and `kimi` are configurable command adapters.
- No provider API key integration is implemented in this MVP by design.
