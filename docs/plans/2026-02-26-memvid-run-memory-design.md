# Design: Memvid Run Memory Integration

**Date:** 2026-02-26
**Status:** Approved

## Summary

Integrate memvid v2 (`.mv2` stores) as the unified knowledge layer for swarm runs.
Each run gets a shared memory store; each task gets its own store. Agents query
sibling task stores for context before execution, eliminating the blind-spot that
caused CS0854-class failures in RFC-006 dogfooding.

## Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Deployment | Subprocess (Option C) | Simplest for experimentation, no sidecar overhead |
| IPC | CLI invocations (Option B) | Quick, independent calls; cold start acceptable for post-hoc |
| File location | Inside worktree (Option A) | Shared lifecycle with run; `.swarm/memory/` in `.gitignore` |
| Encoding trigger | Inline in actor (Option A) | Guarantees sibling context is ready before next task starts |
| Storage budget | Unconstrained | Experimental phase |

## Architecture

```
TaskCoordinatorActor (C#)
  │
  ├── run start: python -m memvid_svc create .swarm/memory/run.mv2
  │              python -m memvid_svc put .swarm/memory/run.mv2 --stdin (plan context)
  │
  ├── before builder dispatch: python -m memvid_svc find .swarm/memory/tasks/task-*.mv2 --query "..."
  │                            inject results into prompt via RolePromptFactory
  │
  ├── after role succeeds: python -m memvid_svc put .swarm/memory/tasks/task-1.mv2 --stdin
  │                        (role output, artifacts summary)
  │
  └── run complete: .swarm/memory/ stays in worktree until cleanup
```

## Components

### 1. `memvid_svc` Python CLI

Location: `project/infra/memvid-svc/`

Thin wrapper around `memvid-sdk`. Commands:

| Command | Args | Stdin | Stdout |
|---------|------|-------|--------|
| `create` | `<path.mv2>` | — | `{"ok": true}` |
| `put` | `<path.mv2> --stdin` | JSON `{"title", "label", "text", "metadata"}` | `{"frame_id": N}` |
| `find` | `<path.mv2> --query "..." [--k N] [--mode auto]` | — | `{"results": [...]}` |
| `timeline` | `<path.mv2> [--limit N]` | — | `{"entries": [...]}` |
| `info` | `<path.mv2>` | — | `{"frames", "size_bytes", ...}` |

Dependencies: `memvid-sdk` only.
Runs with local Python 3.8+ or containerized.

### 2. `MemvidClient` C# class

Location: `SwarmAssistant.Runtime/Memvid/`

```csharp
public sealed class MemvidClient
{
    Task<bool> CreateStoreAsync(string path, CancellationToken ct);
    Task<int> PutAsync(string path, MemvidDocument doc, CancellationToken ct);
    Task<List<MemvidResult>> FindAsync(string path, string query, int k, CancellationToken ct);
    Task<List<MemvidEntry>> TimelineAsync(string path, int limit, CancellationToken ct);
}
```

- Wraps `Process.Start` to `python -m memvid_svc`
- JSON stdout deserialization
- Configurable Python path, timeout
- Behind feature flag: `MemvidEnabled`

### 3. `TaskCoordinatorActor` changes

- After planning: create `run.mv2`, put plan context
- After each role succeeds: put output into `tasks/{taskId}.mv2`
- Before builder dispatch: query sibling `.mv2` files, inject context
- New method: `TryBuildSiblingContextAsync`

### 4. `RolePromptFactory` changes

- New 7th context layer: "Sibling Run Memory"
- Queries completed sibling task `.mv2` stores
- Injects top-K relevant chunks into builder/reviewer prompts

## Data Flow

```
Plan phase:
  Planner output → put into run.mv2

Build phase (task-1):
  Query run.mv2 for task-1 context → inject into prompt
  Builder-1 output → put into tasks/task-1.mv2

Build phase (task-2):
  Query run.mv2 for task-2 context → inject into prompt
  Query tasks/task-1.mv2 for sibling context → inject into prompt
  Builder-2 output → put into tasks/task-2.mv2

Review phase:
  Query all tasks/*.mv2 → inject into reviewer prompt
```

## File Layout

```
{worktree}/
├── .swarm/
│   └── memory/
│       ├── run.mv2
│       ├── meta.json            ← run index (task IDs, status, trace ID)
│       └── tasks/
│           ├── {task-id}.mv2
│           └── ...
├── .gitignore                   ← includes .swarm/memory/
└── (source code)
```

## Configuration

```csharp
public bool MemvidEnabled { get; init; } = false;
public string MemvidPythonPath { get; init; } = "python3";
public int MemvidTimeoutSeconds { get; init; } = 30;
public int MemvidSiblingMaxChunks { get; init; } = 5;
public string MemvidSearchMode { get; init; } = "auto";
```

## Out of Scope

- Godot integration (consumes `.mv2` files — separate effort)
- LLM request/response capture proxy (needs adapter changes)
- Run archival before worktree cleanup (manual for now)
- Encryption of `.mv2` files
