# Agent Guide

This is the single source of truth for AI agents working on swimming-tuna.

## Project Overview

swimming-tuna (SwarmAssistant MVP) - CLI-first swarm assistant that orchestrates AI
coding agents through a deterministic role pipeline (planner -> builder -> reviewer).
Prioritizes subscription-backed CLIs over direct API keys.

**Tech Stack:** .NET 9 (Akka.NET actors, Microsoft Agent Framework), JavaScript/Node
CLI, Godot Mono UI, .NET Aspire orchestration, ArcadeDB persistence, Langfuse
observability.

## Project Structure

```text
.
├── .agent/              # Agent skills, adapters, hooks, rules
│   ├── adapters/        # Per-tool sync configs (claude/, codex/, cline/, copilot/, kiro/)
│   ├── hooks/           # Shared Python hook scripts
│   ├── rules/           # Shared coding rules (conventions.md, security.md)
│   ├── skills/          # Agent skills (SKILL.md format)
│   └── tools/           # Sync engine (sync.py) and tests
├── project/
│   ├── dotnet/          # .NET runtime (Akka actors, Agent Framework, AG-UI, A2A, ArcadeDB)
│   │   ├── src/SwarmAssistant.Runtime/     # Main runtime
│   │   ├── src/SwarmAssistant.Contracts/   # Shared types
│   │   ├── src/SwarmAssistant.AppHost/     # .NET Aspire orchestrator
│   │   ├── src/SwarmAssistant.ServiceDefaults/ # OpenTelemetry extensions
│   │   └── tests/                          # xUnit tests
│   ├── godot-ui/        # Godot Mono desktop client
│   ├── src/             # JavaScript CLI MVP
│   ├── infra/           # Docker stacks (Langfuse, ArcadeDB)
│   └── config/          # Runtime config
├── ref-projects/        # Reference implementations
├── docs/                # Documentation and plans
├── AGENTS.md            # This file (single source of truth)
├── CLAUDE.md            # Pointer to this file
└── Taskfile.yml         # Task runner
```

## Architecture

See `project/ARCHITECTURE.md` for full phase history. Key patterns:

- **Role pipeline:** planner -> builder -> reviewer with escalation
- **Actor topology:** CoordinatorActor, TaskCoordinatorActor, WorkerActor,
  ReviewerActor, SupervisorActor, DispatcherActor, BlackboardActor, SwarmAgentActor
- **CLI adapter fallback:** copilot -> cline -> kimi -> local-echo
- **Protocols:** AG-UI (SSE streaming), A2A (task APIs), A2UI (Godot surface rendering)
- **Persistence:** ArcadeDB for task snapshots, startup memory bootstrap
- **Observability:** OpenTelemetry traces exported to Langfuse OTLP endpoint

## Common Tasks

```bash
task build              # Build .NET solution + Go binary
task test               # Run tests
task lint               # Run linters
task fmt                # Format code
task run:aspire         # Boot full local dev environment via .NET Aspire
task ruff:check         # Lint Python files
task ruff:format        # Format Python files
task repomix:pack       # Pack repo for AI context
task skills:list        # List available agent skills
task skills:new SKILL=x # Create a new skill
task agent:sync         # Sync all adapters (claude, codex, cline, copilot, kiro)
```

## Development Guidelines

- Conventional commit messages (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`)
- Run pre-commit hooks before committing: `pre-commit run --all-files`
- C#: file-scoped namespaces, sealed records, init-only properties
- Python: ruff for linting/formatting
- See `.agent/rules/conventions.md` for detailed coding conventions
- See `.agent/rules/security.md` for security rules

## .NET Runtime

The main runtime lives in `project/dotnet/`. Build and test with:

```bash
dotnet build project/dotnet/SwarmAssistant.sln
dotnet test project/dotnet/SwarmAssistant.sln
```

Runtime profiles: `Local`, `SecureLocal`, `CI`.

Key runtime config flags (set via `Runtime__<key>` env vars):

| Flag | Purpose |
|------|---------|
| `AgentFrameworkExecutionMode` | `subscription-cli-fallback` for CLI-first |
| `AgUiEnabled` / `AgUiBindUrl` | AG-UI SSE endpoint |
| `A2AEnabled` | A2A task API endpoints |
| `ArcadeDbEnabled` / `ArcadeDbHttpUrl` | ArcadeDB persistence |
| `MemoryBootstrapEnabled` | Restore persisted tasks on startup |
| `LangfuseTracingEnabled` | OpenTelemetry export to Langfuse |
| `SimulateBuilderFailure` / `SimulateReviewerFailure` | Fault injection |
| `ProjectContextPath` | Path to project context file (e.g., AGENTS.md) injected into role prompts |
| `WorkspaceBranchEnabled` | Create `swarm/task-{id}` git branches for builder isolation (default: false) |
| `AgentEndpointEnabled` | Enable per-agent A2A HTTP endpoints |
| `AgentEndpointPortRange` | Port range for agent endpoints (e.g., `8001-8032`) |
| `AgentHeartbeatIntervalSeconds` | Agent health check heartbeat interval |

## API Endpoints

When the runtime is running (default `http://127.0.0.1:5080`):

| Endpoint | Purpose |
|----------|---------|
| `GET /healthz` | Health check |
| `GET /ag-ui/events` | SSE event stream |
| `GET /ag-ui/recent` | Recent event buffer |
| `POST /ag-ui/actions` | UI action ingress |
| `GET /.well-known/agent-card.json` | A2A agent card |
| `POST /a2a/tasks` | Submit task (A2A) |
| `GET /a2a/tasks` / `GET /a2a/tasks/{id}` | Task snapshots |
| `GET /memory/tasks` / `GET /memory/tasks/{id}` | Memory read |

AG-UI actions: `request_snapshot`, `refresh_surface`, `submit_task`, `load_memory`.

## Infrastructure

```bash
# Langfuse (observability)
docker compose -f project/infra/langfuse/docker-compose.yml --env-file project/infra/langfuse/env/local.env up -d

# ArcadeDB (persistence)
docker compose -f project/infra/arcadedb/docker-compose.yml --env-file project/infra/arcadedb/env/local.env up -d
```

Or use `task run:aspire` to boot everything via .NET Aspire.

## Agent Skills

Skills are in `.agent/skills/`:

| Skill | Description |
|-------|-------------|
| [skill-creator](.agent/skills/skill-creator/) | Guide for creating effective skills |
| [remotion](.agent/skills/remotion/) | Remotion video creation best practices |
| [memory](.agent/skills/memory/) | Cross-session memory via Supermemory (decisions, debugging) |
| [swarm-dev](.agent/skills/swarm-dev/) | Swarm dev workflow: decompose, submit, monitor, review, merge |

To add a new skill: `task skills:new SKILL=my-skill`, then edit `.agent/skills/my-skill/SKILL.md`.

## Agent Adapters

Adapters in `.agent/adapters/` define how to sync skills, rules, and hooks to each
CLI tool:

| Adapter | Tool | Hooks | Sync Command |
|---------|------|-------|--------------|
| claude | Claude Code | SessionStart, Stop, PreCompact | `task agent:sync:claude` |
| codex | Codex CLI | none | `task agent:sync:codex` |
| cline | Cline CLI | TaskStart, PreToolUse | `task agent:sync:cline` |
| copilot | Copilot CLI | sessionStart, preToolUse | `task agent:sync:copilot` |
| kiro | Kiro IDE | fileEdited, preToolUse, postToolUse, promptSubmit, agentStop | `task agent:sync:kiro` |

Run `task agent:sync` to sync all adapters at once.

## Shared Rules & Hooks

- `.agent/rules/conventions.md` - Coding style, naming, and project conventions
- `.agent/rules/security.md` - Security policies and guardrails
- `.agent/hooks/session_start.py` - Session initialization hook
- `.agent/hooks/session_end.py` - Session cleanup hook
- `.agent/hooks/pre_tool_use.py` - Pre-tool-use validation hook

## Provider Strategy

Priority is subscription-backed local CLIs (no direct API keys in MVP):

1. Copilot CLI
2. Cline CLI
3. Kimi CLI
4. Kilo CLI
5. Local fallback adapter (echo)

Adapter probes validate command availability; execution can still fail on auth or
environment restrictions, triggering fallback to the next adapter.

## CI/CD Workflows

| Workflow | Trigger | Purpose |
|----------|---------|---------|
| `auto-merge-non-main.yml` | PR opened/synced | Auto-squash-merge PRs to non-main branches |
| `doc-sync.md` (gh-aw)¹ | Weekly (Sun 9am UTC) | Documentation freshness audit |
| `phase11-health.md` (gh-aw)¹ | Weekly (Mon 9:30am UTC) | Repository hygiene audit |
| `review-resolve.md` (gh-aw)¹ | `pull_request_review` submitted | Fix bot review comments and create follow-up PR |

> ¹ `gh-aw` entries are markdown-driven workflow specs compiled via `gh aw compile`.

### Review Comment Resolver

When an automated reviewer (`gemini-code-assist[bot]`, `coderabbitai[bot]`) submits a
review, the `review-resolve` gh-aw workflow spawns a Copilot CLI agent that:

1. Filters to bot-authored inline review comments only
2. Reads affected files and understands each comment
3. Applies minimal code fixes addressing the feedback
4. Creates a single follow-up PR via `create-pull-request` safe output

Recompile after editing: `gh aw compile .github/workflows/review-resolve.md`

## Key References

- `project/ARCHITECTURE.md` - Full phase-by-phase architecture history
- `project/README.md` - Runtime config details and endpoint examples
- `.agent/rules/` - Detailed coding and security conventions
- `docs/` - Planning documents and design decisions
