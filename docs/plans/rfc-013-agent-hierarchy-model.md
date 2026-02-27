# RFC-013: Agent Hierarchy Model

**Status:** Draft
**Date:** 2026-02-27
**Dependencies:** RFC-011 (API-Direct)
**Supersedes:** Previous RFC-013 draft (agent-hierarchy-tracing.md) — tracing/enrichment
  moved to RFC-015.

## Problem

The swarm dispatches tasks to CLI/API agents, but the execution model is flat: one
task in, one stdout out. Agents internally spawn sub-agents (Claude Code `Task`,
Pi `/control`, Cline multi-turn API loops), but the swarm has no data model to
represent this hierarchy. Without a shared vocabulary for agent levels, neither
the orchestration layer (RFC-014) nor the observability layer (RFC-015) can work.

## Proposal

### 1. AgentSpan — Universal Unit of Agent Execution

Flat record with `ParentSpanId` reference. No `Children` property — tree projection
is built on demand by `AgentSpanCollector`.

```csharp
namespace SwarmAssistant.Contracts.Hierarchy;

public sealed record AgentSpan
{
    public required string SpanId { get; init; }
    public string? ParentSpanId { get; init; }         // null for root spans
    public required int Level { get; init; }            // 0, 1, 2, ...
    public required AgentSpanKind Kind { get; init; }
    public required string TaskId { get; init; }
    public string? RunId { get; init; }

    // Identity
    public required string AgentId { get; init; }       // "coordinator-task-01", "kilo-builder"
    public string? AdapterId { get; init; }             // "kilo", "claude", "api-anthropic"
    public SwarmRole? Role { get; init; }               // Planner, Builder, Reviewer, Decomposer

    // Lifecycle
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public AgentSpanStatus Status { get; init; } = AgentSpanStatus.Running;

    // Metrics
    public TokenUsage? Usage { get; init; }
    public decimal? CostUsd { get; init; }

    // Sub-agent metadata
    public SubAgentFlavor Flavor { get; init; } = SubAgentFlavor.None;
}
```

### 2. Supporting Enums

```csharp
public enum AgentSpanKind
{
    Coordinator,    // Level 0: TaskCoordinatorActor (one per task)
    CliAgent,       // Level 1: CLI adapter invocation
    ApiAgent,       // Level 1: api-direct invocation
    Decomposer,     // Level 1: RFC-014 decomposer role
    SubAgent,       // Level 2+: detected inside CLI agent
    ToolCall        // Level 2+: individual tool invocation within an agent
}

public enum SubAgentFlavor
{
    None,           // Not a sub-agent
    Normal,         // Independent helper (Claude Code Task, Pi /control)
    CoWork          // Peer coordination (agents sharing context)
}

public enum AgentSpanStatus
{
    Running,
    Completed,
    Failed,
    TimedOut
}
```

### 3. RunSpan — Run-Level Container

Above the per-task `AgentSpan` tree, a `RunSpan` represents the full run lifecycle
from design doc acceptance to PR-ready. This is the root of both the orchestration
tree (RFC-014) and the trace tree (RFC-015).

```csharp
namespace SwarmAssistant.Contracts.Hierarchy;

public sealed record RunSpan
{
    public required string RunId { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public RunSpanStatus Status { get; init; } = RunSpanStatus.Accepted;
    public string? FeatureBranch { get; init; }
    public string? BaseBranch { get; init; }
}

public enum RunSpanStatus
{
    Accepted,
    Decomposing,
    Executing,
    Merging,
    ReadyForPr,
    Done,
    Failed
}
```

### 4. Hierarchy Levels

```
RunSpan (run)
│
└── AgentSpan Level 0: TaskCoordinatorActor
    │         Kind: Coordinator
    │         One per task. Drives the GOAP planning loop.
    │
    ├── AgentSpan Level 1: CLI/API Agent Invocation
    │   │         Kind: CliAgent | ApiAgent
    │   │         Created when AgentFrameworkRoleEngine dispatches.
    │   │
    │   ├── AgentSpan Level 2: Sub-Agent (detected from CLI output)
    │   │   │         Kind: SubAgent
    │   │   │         Flavor: Normal (independent) or CoWork (peer)
    │   │   │
    │   │   └── AgentSpan Level 3+: Deeper nesting (recursive)
    │   │
    │   └── AgentSpan Level 2: Tool Call (detected from CLI output)
    │                 Kind: ToolCall
    │
    └── AgentSpan Level 1: SUBTASK spawned agent
                  Maps to a new Level 0 coordinator for the sub-task.
                  Linked via ParentSpanId to the spawning coordinator.
```

### 5. AgentSpanCollector

Lives in `SwarmAssistant.Runtime`. Manages span lifecycle and provides both flat
and tree projections.

```csharp
namespace SwarmAssistant.Runtime.Hierarchy;

internal sealed class AgentSpanCollector
{
    private readonly ConcurrentDictionary<string, AgentSpan> _spans = new();

    // Called when a role execution starts
    public AgentSpan StartSpan(string taskId, string? runId, SwarmRole role,
        AgentSpanKind kind, string? parentSpanId, string? adapterId);

    // Called when execution completes
    public AgentSpan CompleteSpan(string spanId, AgentSpanStatus status,
        TokenUsage? usage = null, decimal? costUsd = null);

    // Flat list — for Langfuse export (RFC-015)
    public IReadOnlyList<AgentSpan> GetFlat(string taskId);

    // Tree projection — for Godot / API responses (RFC-015)
    public AgentSpanTree GetTree(string taskId);

    // All spans for a run — for run-level views (RFC-014)
    public IReadOnlyList<AgentSpan> GetByRun(string runId);
}
```

### 6. AgentSpanTree — Projection Type

Used only for serialization to consumers (Godot, API). Not stored.

```csharp
public sealed record AgentSpanTree
{
    public required AgentSpan Span { get; init; }
    public IReadOnlyList<AgentSpanTree> Children { get; init; } = [];
}
```

### 7. TokenUsage

```csharp
public sealed record TokenUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int? ReasoningTokens { get; init; }
    public int? CacheReadTokens { get; init; }
    public int? CacheWriteTokens { get; init; }
}
```

### 8. Sub-Agent Flavor Detection

```csharp
SubAgentFlavor DetectFlavor(string toolName, JsonElement? toolArgs)
{
    // Claude Code: Task with resume parameter → CoWork
    if (toolName == "Task" && toolArgs?.TryGetProperty("resume", out _) == true)
        return SubAgentFlavor.CoWork;

    // Default: Normal (independent helper)
    return SubAgentFlavor.Normal;
}
```

## Where Types Live

| Type | Assembly | Rationale |
|------|----------|-----------|
| `AgentSpan`, `RunSpan`, enums, `TokenUsage` | `SwarmAssistant.Contracts` | Shared vocabulary for Runtime, Godot, external consumers |
| `AgentSpanTree` | `SwarmAssistant.Contracts` | Projection type for API/Godot serialization |
| `AgentSpanCollector` | `SwarmAssistant.Runtime` | Runtime-only lifecycle management |

## Implementation Tasks

1. `AgentSpan`, `AgentSpanKind`, `SubAgentFlavor`, `AgentSpanStatus` in Contracts
2. `RunSpan`, `RunSpanStatus` in Contracts
3. `TokenUsage` in Contracts
4. `AgentSpanTree` projection type in Contracts
5. `AgentSpanCollector` in Runtime with `StartSpan`, `CompleteSpan`, `GetFlat`, `GetTree`, `GetByRun`
6. Wire `AgentSpanCollector` into `AgentFrameworkRoleEngine` — start/complete spans on role dispatch
7. Unit tests for collector: start, complete, flat projection, tree projection
8. Unit tests for flavor detection

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Flat storage + tree projection | Not tree-shaped records | Incremental updates during execution; Langfuse wants flat; tree built on demand |
| Types in Contracts | Not Runtime-only | Godot and external consumers need the vocabulary |
| `RunSpan` separate from `AgentSpan` | Not a special AgentSpan | Run lifecycle (decomposing, merging, PR) is fundamentally different from agent execution |
| No `Children` on `AgentSpan` | Use `AgentSpanTree` for projection | Keeps the core record simple and update-friendly |

## Out of Scope

- Output enrichment / parsing (RFC-015)
- Langfuse span export (RFC-015)
- Godot rendering (RFC-015)
- Run orchestration / git tree (RFC-014)
- Sub-agent cost allocation policies
