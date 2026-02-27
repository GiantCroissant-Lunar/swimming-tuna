# SwarmAssistant .NET Runtime (Phase 11)

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
- RFC-010 adds initial `api-direct` and `hybrid` execution modes:
  - `api-direct`: execute via registered `IModelProvider` implementations
  - `hybrid`: prefer `api-direct` when role model mapping has a registered provider, otherwise fallback to CLI adapters
- `SubscriptionCliRoleExecutor` executes roles with ordered adapters:
- `copilot`
- `cline`
- `kimi`
- `kilo`
- `pi`
- `local-echo` (deterministic fallback)
- Local profile defaults to `subscription-cli-fallback` with `SandboxMode=host`.
  > **⚠️ Security note:** `SandboxMode=host` executes adapter CLI commands directly on your
  > machine with no isolation. Adapter-generated commands have unrestricted access to your local
  > resources. Use `docker` or `apple-container` sandbox modes whenever adapters receive
  > externally influenced task inputs.
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

### Pi + Z.AI Dogfood Profile

Use the `Dogfood` environment profile to run the swarm with pi first
(`CliAdapterOrder=["pi","kilo","kimi"]`, no `local-echo` fallback):

```bash
DOTNET_ENVIRONMENT=Dogfood dotnet run --project project/dotnet/src/SwarmAssistant.Runtime --no-launch-profile
```

Prerequisites:

- `pi`, `kimi`, and `kilo` CLIs installed and on `PATH`
- run `task setup:pi:zai` once to set the z.ai coding provider URL in `~/.pi/agent/models.json`
- set the z.ai key in your shell:
  - `export ZAI_API_KEY=<your-key>`
- verify pi can reach z.ai GLM-4.7:
  - `pi --provider zai --model glm-4.7 -p "ping"`
- ensure `RoleModelMapping` includes `Orchestrator` (otherwise pi may fallback to another adapter for orchestration decisions)

### RFC-010 Model Mapping (Initial)

`RuntimeOptions` now supports role-level model hints via `RoleModelMapping`.
When a role is executed, supported adapters receive model/reasoning hints via
their CLI flags or env vars.

```json
{
  "Runtime": {
    "ApiProviderOrder": ["openai"],
    "OpenAiBaseUrl": "https://api.openai.com/v1",
    "RoleModelMapping": {
      "Planner": { "Model": "anthropic/claude-sonnet-4-6", "Reasoning": "high" },
      "Builder": { "Model": "kilo/giga-potato" }
    }
  }
}
```

`ApiProviderOrder` controls provider precedence.
`OpenAiApiKeyEnvVar` defines the environment-variable name used to fetch OpenAI
credentials.
`OpenAiBaseUrl` defines the OpenAI endpoint used when role model hints (for
example `Planner`/`Builder`) resolve to API-backed providers.

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
DOTNET_ENVIRONMENT=Local dotnet run --project project/dotnet/src/SwarmAssistant.Runtime --no-launch-profile
curl -s http://127.0.0.1:5080/healthz
curl -N http://127.0.0.1:5080/ag-ui/events
curl -s 'http://127.0.0.1:5080/ag-ui/recent?count=100'
```

Supported AG-UI actions (Phase 11):

- `request_snapshot`
- `refresh_surface`
- `submit_task` (payload requires `title`; optional `description`)
- `load_memory` (payload supports optional `limit`)

Startup memory bootstrap (Phase 11):

- Enabled by `Runtime__MemoryBootstrapEnabled=true`
- Restore size controlled by `Runtime__MemoryBootstrapLimit` (default `200`)
- Startup emits `agui.memory.bootstrap` or `agui.memory.bootstrap.failed`
- Demo task submission is skipped when restored tasks already exist
- Startup restore flow is implemented via `StartupMemoryBootstrapper` and covered by dedicated unit tests.

## A2A + ArcadeDB Integration (Phase 7)

- A2A endpoints are enabled with `Runtime__A2AEnabled=true`:
- `GET /.well-known/agent-card.json` (or custom `Runtime__A2AAgentCardPath`)
- `POST /a2a/tasks`
- `GET /a2a/tasks/{taskId}`
- `GET /a2a/tasks`
- `TaskRegistry` captures lifecycle transitions and role outputs for both actor-driven and API-submitted tasks.
- `ArcadeDbTaskMemoryWriter` persists snapshots as `SwarmTask` records using an atomic ArcadeDB `UPDATE ... UPSERT WHERE taskId = :taskId` command.
- ArcadeDB runtime configuration:
- `Runtime__ArcadeDbEnabled`
- `Runtime__ArcadeDbHttpUrl`
- `Runtime__ArcadeDbDatabase`
- `Runtime__ArcadeDbUser`
- `Runtime__ArcadeDbPassword`
- `Runtime__ArcadeDbAutoCreateSchema`

Start ArcadeDB local stack before enabling runtime persistence:

```bash
cd project/infra/arcadedb
docker compose --env-file env/local.env up -d
```

Example task flow:

```bash
curl -s -X POST http://127.0.0.1:5080/a2a/tasks \
  -H 'content-type: application/json' \
  -d '{"title":"Phase 11 endpoint test","description":"validate task registry + ArcadeDB write/read"}'
curl -s http://127.0.0.1:5080/a2a/tasks
curl -s http://127.0.0.1:5080/a2a/tasks/<task-id>
```

Memory-read APIs (Phase 11):

```bash
curl -s 'http://127.0.0.1:5080/memory/tasks?limit=20'
curl -s http://127.0.0.1:5080/memory/tasks/<task-id>
curl -s -X POST http://127.0.0.1:5080/ag-ui/actions \
  -H 'content-type: application/json' \
  -d '{"actionId":"load_memory","payload":{"limit":20}}'
```

## Build

```bash
dotnet build project/dotnet/SwarmAssistant.sln
dotnet test project/dotnet/SwarmAssistant.sln
```

### Memvid Integration Tests

Memvid integration tests are opt-in and run only when `MEMVID_INTEGRATION_TESTS=1`.

Set up the Python environment:

```bash
cd project/infra/memvid-svc
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
```

Run only memvid integration tests:

```bash
MEMVID_INTEGRATION_TESTS=1 \
dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests/SwarmAssistant.Runtime.Tests.csproj \
  --filter "MemvidIntegrationTests|MemvidClientFindModeTests|MemvidClientTests"
```

Optional overrides:

- `MEMVID_PYTHON_PATH` to point to a different Python executable
- `MEMVID_SVC_DIR` to point to a different memvid service directory

## Run with Profile

```bash
DOTNET_ENVIRONMENT=Local dotnet run --project project/dotnet/src/SwarmAssistant.Runtime
DOTNET_ENVIRONMENT=SecureLocal dotnet run --project project/dotnet/src/SwarmAssistant.Runtime
DOTNET_ENVIRONMENT=CI dotnet run --project project/dotnet/src/SwarmAssistant.Runtime
```

## Runtime Flags

From `Runtime` config:

- `Profile`
- `RoleSystem`
- `AgentExecution`
- `AgentFrameworkExecutionMode`
- `AutoSubmitDemoTask`
- `DemoTaskTitle`
- `DemoTaskDescription`
- `SimulateBuilderFailure`
- `SimulateReviewerFailure`
- `RoleExecutionTimeoutSeconds`
- `CliAdapterOrder`
- `SandboxMode`
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
- `LangfuseTracingEnabled`
- `LangfusePublicKey`
- `LangfuseSecretKey`
- `LangfuseOtlpEndpoint`
- `LangfuseBaseUrl`
- `HealthHeartbeatSeconds`
- `ApiKey`
- `AgentEndpointEnabled`
- `AgentEndpointPortRange`
- `AgentHeartbeatIntervalSeconds`

## Security

By default the runtime binds to `127.0.0.1` (`AgUiBindUrl = http://127.0.0.1:5080`) and is not
reachable from outside the local machine.

If you expose the runtime beyond localhost (e.g. in a team or staging environment), set
`Runtime__ApiKey` to a strong random secret. Callers must then supply the key via the
`X-API-Key` header on all non-public endpoints:

- `POST /ag-ui/actions`
- `POST /a2a/tasks`
- `GET /a2a/tasks`
- `GET /a2a/tasks/{taskId}`
- `GET /memory/tasks`
- `GET /memory/tasks/{taskId}`
- `GET /ag-ui/events`
- `GET /ag-ui/recent`

The `GET /.well-known/agent-card.json` and `GET /healthz` endpoints are intentionally public.

```bash
export Runtime__ApiKey=my-secret-key
# then call:
curl -X POST http://<host>:5080/a2a/tasks \
  -H 'X-API-Key: my-secret-key' \
  -H 'content-type: application/json' \
  -d '{"title":"task title"}'
```

Do not commit `Runtime__ApiKey` to source control; supply it via environment variable or a
secrets manager.

## Agent Endpoints (RFC-001)

When `Runtime__AgentEndpointEnabled=true`, each SwarmAgentActor spawns its own
A2A HTTP endpoint within the configured port range. Each agent serves:

- `GET /.well-known/agent-card.json` — agent identity and capabilities
- `POST /a2a/tasks` — accept task submissions
- `GET /a2a/health` — liveness check

```bash
export Runtime__AgentEndpointEnabled=true
export Runtime__AgentEndpointPortRange=8001-8032

# Query individual agent:
curl -s http://127.0.0.1:8001/.well-known/agent-card.json
curl -s http://127.0.0.1:8001/a2a/health
curl -s -X POST http://127.0.0.1:8001/a2a/tasks \
  -H 'content-type: application/json' \
  -d '{"title":"test task"}'
```

> **Security:** Per-agent endpoints bind to `127.0.0.1` only and do not require
> `X-API-Key` authentication (unlike the runtime-level A2A endpoints on port 5080).
> Do not expose per-agent ports beyond localhost without adding authentication middleware.
