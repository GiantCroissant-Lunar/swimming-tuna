# RFC-013: Agent Hierarchy Model & Multi-Level Tracing

**Status:** Draft
**Date:** 2026-02-26
**Dependencies:** RFC-008 (Godot Dashboard), RFC-006 (Langfuse Replay), RFC-011 (API-Direct)
**Extends:** RFC-008 Layer 5 (Agent Characters), RFC-006 (Langfuse spans)

## Problem

The swarm dispatches tasks to CLI/API agents, but those agents may internally spawn
sub-agents: Claude Code uses `Task` to launch parallel workers, Pi uses `/control` for
peer agents, Cline makes multiple API calls with tool use loops. Today, the swarm sees
only a flat execution: one agent in, one stdout out. This creates three gaps:

1. **Godot can't render sub-agents as characters** — RFC-008 Layer 5 wants each agent
   as a character, but sub-agents are invisible
2. **Tracing is single-level** — Langfuse shows one span per role execution, with no
   visibility into internal agent decomposition
3. **Cost attribution is wrong** — a Claude Code agent spawning 3 sub-agents reports
   aggregate cost, not per-sub-agent breakdown

The swarm already has hierarchical task decomposition (`SUBTASK:` → `SpawnSubTask`),
but that's at the *task* level, not the *agent* level. We need a model that represents
the full agent tree: coordinator → agent → sub-agent → sub-sub-agent.

## Proposal

### 1. Agent Hierarchy Model

Introduce `AgentSpan` as the universal unit of agent execution:

```csharp
public sealed record AgentSpan
{
    public required string SpanId { get; init; }
    public string? ParentSpanId { get; init; }         // null for Level 0
    public required int Level { get; init; }            // 0, 1, 2, ...
    public required AgentSpanKind Kind { get; init; }
    public required string TaskId { get; init; }
    public string? RunId { get; init; }

    // Identity
    public required string AgentId { get; init; }       // "coordinator-task-01", "kilo-builder", "claude-sub-1"
    public string? AdapterId { get; init; }             // "kilo", "claude", "api-anthropic"
    public SwarmRole? Role { get; init; }               // Planner, Builder, etc.

    // Lifecycle
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public AgentSpanStatus Status { get; init; } = AgentSpanStatus.Running;

    // Metrics
    public TokenUsage? Usage { get; init; }
    public decimal? CostUsd { get; init; }

    // Sub-agent metadata
    public SubAgentFlavor Flavor { get; init; } = SubAgentFlavor.None;
    public IReadOnlyList<AgentSpan> Children { get; init; } = [];
}

public enum AgentSpanKind
{
    Coordinator,    // Level 0: TaskCoordinatorActor
    CliAgent,       // Level 1: CLI adapter invocation
    ApiAgent,       // Level 1: api-direct invocation
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

### 2. Hierarchy Levels

```
Level 0: TaskCoordinatorActor
│         Kind: Coordinator
│         One per task. Drives the GOAP planning loop.
│
├── Level 1: CLI/API Agent Invocation
│   │         Kind: CliAgent | ApiAgent
│   │         Created when AgentFrameworkRoleEngine dispatches to an adapter/provider.
│   │         Token data: exact for ApiAgent, estimated or enriched for CliAgent.
│   │
│   ├── Level 2: Sub-Agent (detected from CLI output)
│   │   │         Kind: SubAgent
│   │   │         Flavor: Normal (independent) or CoWork (peer)
│   │   │         Detected by parsing structured output from CLI tools.
│   │   │
│   │   └── Level 3: Deeper nesting (if sub-agent spawns its own)
│   │                 Same model, recursive.
│   │
│   └── Level 2: Tool Call (detected from CLI output)
│                 Kind: ToolCall
│                 Individual Read/Write/Edit/Bash calls within the agent.
│                 Useful for understanding what the agent actually did.
│
└── Level 1: SUBTASK spawned agent (existing mechanism)
              Maps to a new Level 0 coordinator for the sub-task.
              Linked via ParentSpanId to the spawning coordinator.
```

### 3. Structured Output Enrichment

Each CLI adapter gets an `IOutputEnricher` that parses structured data into child `AgentSpan`s:

```csharp
internal interface IOutputEnricher
{
    string AdapterId { get; }
    AgentSpan[] EnrichFromOutput(
        AgentSpan parentSpan,
        string rawOutput,
        string? sessionArtifactPath);
}
```

#### Kilo Enricher

Kilo with `--format json` emits newline-delimited JSON events during execution:

```csharp
internal sealed class KiloOutputEnricher : IOutputEnricher
{
    public string AdapterId => "kilo";

    // Parse kilo JSON events into child spans
    // Each assistant message with tokens becomes a Level 2 ToolCall span
    // Token data: tokens.{total, input, output, reasoning, cache.{read, write}}
}
```

**Adapter change:** Add `--format json` to kilo's `ExecuteArgs`. Parse the JSON stream
to extract both the text output and the structured token/tool data.

Alternatively, after execution call `kilo export <sessionId>` for full per-turn breakdown.

#### Claude Code Enricher

Claude Code session JSONL files contain full Anthropic API responses with usage data:

```csharp
internal sealed class ClaudeCodeOutputEnricher : IOutputEnricher
{
    public string AdapterId => "claude";

    // Post-hoc: read ~/.claude/projects/<dir>/<latest>.jsonl
    // Each assistant turn with message.usage becomes a child span
    // Sub-agent spawns (Task tool calls) become Level 2 SubAgent spans
    // Token data: usage.{input_tokens, output_tokens, cache_*_input_tokens}
}
```

**Detection of sub-agents:** Look for tool_use blocks where `name == "Task"` in the
session JSONL. Each Task invocation is a Level 2 SubAgent span with Flavor.Normal.

#### Cline Enricher

Cline writes per-API-call token data to `~/.cline/data/tasks/<id>/ui_messages.json`:

```csharp
internal sealed class ClineOutputEnricher : IOutputEnricher
{
    public string AdapterId => "cline";

    // Post-hoc: read ~/.cline/data/tasks/<latest>/ui_messages.json
    // Each "say": "api_req_started" entry becomes a child ToolCall span
    // Token data: text.{tokensIn, tokensOut, cacheWrites, cacheReads, cost}
}
```

#### Fallback (Copilot, Kimi, Pi)

No structured output available. Return empty children:

```csharp
internal sealed class NoOpOutputEnricher : IOutputEnricher
{
    // ParentSpan only, no children.
    // Token usage estimated from prompt/output length.
}
```

Pi will move from NoOp to a real enricher once session file parsing is implemented.

### 4. Span Collection & Storage

```csharp
internal sealed class AgentSpanCollector
{
    private readonly ConcurrentDictionary<string, AgentSpan> _spans = new();

    // Called by AgentFrameworkRoleEngine when a role execution starts
    public AgentSpan StartSpan(string taskId, string runId, SwarmRole role,
        AgentSpanKind kind, string? parentSpanId, string adapterId);

    // Called when execution completes — triggers enrichment
    public AgentSpan CompleteSpan(string spanId, string rawOutput,
        TokenUsage? usage, AgentSpanStatus status);

    // Returns the full tree for a task
    public AgentSpan GetTree(string taskId);

    // Returns flat list for Langfuse export
    public IReadOnlyList<AgentSpan> GetFlat(string taskId);
}
```

### 5. Langfuse Integration

Map `AgentSpan` tree to nested OTLP spans:

```csharp
// Level 0 span (existing)
using var coordinatorActivity = telemetry.StartActivity("task.coordinate", taskId: taskId);

// Level 1 span (existing, enhanced)
using var agentActivity = telemetry.StartActivity("agent.execute",
    parentId: coordinatorActivity?.Id, taskId: taskId, role: role);
agentActivity?.SetTag("agent.span.level", 1);
agentActivity?.SetTag("agent.span.kind", "cli-agent");
agentActivity?.SetTag("agent.span.adapter", "kilo");

// Level 2 spans (NEW — from enrichment)
foreach (var child in enrichedSpan.Children)
{
    using var subActivity = telemetry.StartActivity("agent.sub-execute",
        parentId: agentActivity?.Id, taskId: taskId);
    subActivity?.SetTag("agent.span.level", 2);
    subActivity?.SetTag("agent.span.kind", child.Kind.ToString());
    subActivity?.SetTag("agent.span.flavor", child.Flavor.ToString());
    subActivity?.SetTag("gen_ai.usage.input_tokens", child.Usage?.InputTokens);
    subActivity?.SetTag("gen_ai.usage.output_tokens", child.Usage?.OutputTokens);
}
```

Result in Langfuse: nested trace tree showing coordinator → agent → sub-agents.

### 6. Godot Dashboard Integration (RFC-008 Layer 5)

Extend the A2UI `updateDataModel` payload to include hierarchy:

```json
{
  "type": "updateDataModel",
  "surfaceType": "dashboard",
  "layer": "agent-hierarchy",
  "data": {
    "spans": [
      {
        "spanId": "span-001",
        "parentSpanId": null,
        "level": 0,
        "kind": "coordinator",
        "agentId": "coordinator-task-01",
        "status": "running",
        "children": [
          {
            "spanId": "span-002",
            "parentSpanId": "span-001",
            "level": 1,
            "kind": "cli-agent",
            "agentId": "kilo-builder",
            "adapterId": "kilo",
            "role": "builder",
            "status": "completed",
            "usage": { "input": 2450, "output": 890, "cost": 0.0 },
            "children": [
              {
                "spanId": "span-003",
                "parentSpanId": "span-002",
                "level": 2,
                "kind": "tool-call",
                "agentId": "kilo-builder-turn-1",
                "status": "completed",
                "usage": { "input": 1200, "output": 450 }
              }
            ]
          }
        ]
      }
    ]
  }
}
```

Godot renders each span as a character:
- Level 0: Manager character (orchestrator at desk)
- Level 1: Worker characters (agents at workstations)
- Level 2: Helper characters (sub-agents appearing near their parent)
- CoWork sub-agents: Two characters at a shared desk

### 7. Sub-Agent Flavor Detection

**Normal sub-agents** (independent helpers):
- Claude Code `Task` tool invocations
- Pi `/control` calls
- Kilo tool calls that spawn processes

**CoWork sub-agents** (peer coordination):
- Claude Code agents that share context (detected by `resume` parameter in Task calls)
- Pi sessions that reference other sessions
- Any sub-agent pattern where output of one feeds into another within the same parent

Detection heuristic:
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

## Implementation Tasks

1. `AgentSpan`, `AgentSpanKind`, `SubAgentFlavor`, `AgentSpanStatus` types
2. `IOutputEnricher` interface
3. `KiloOutputEnricher` — parse `--format json` or `kilo export` output
4. `ClaudeCodeOutputEnricher` — parse session JSONL for turns and Task sub-agents
5. `ClineOutputEnricher` — parse `ui_messages.json` for API call entries
6. `NoOpOutputEnricher` — fallback for copilot/kimi/pi
7. `AgentSpanCollector` — span lifecycle management and tree assembly
8. Langfuse nested span export — map AgentSpan tree to OTLP parent-child activities
9. A2UI `agent-hierarchy` payload — extend dashboard data model
10. Kilo adapter: add `--format json` or post-run `kilo export` call
11. Dogfood validation: submit task, verify Langfuse shows nested spans
12. Dogfood validation: verify Godot receives hierarchy payload

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Post-hoc enrichment | Not real-time streaming | CLI tools write structured data to files; easier to parse after completion |
| `IOutputEnricher` per adapter | Not universal parser | Each CLI tool has different structured output format |
| `AgentSpan` not `Activity` | Custom type alongside OTLP | Need persistent tree model for Godot; `Activity` is fire-and-forget |
| Flavor detection heuristic | Not CLI-reported | CLI tools don't declare sub-agent coordination mode |
| Level 2+ optional | Graceful degradation | Some adapters (copilot, kimi) can't report sub-agents; that's OK |

## Out of Scope

- Real-time sub-agent streaming to Godot (post-hoc is sufficient for v1)
- LLM capture proxy (alternative to post-hoc parsing — separate RFC if needed)
- Sub-agent cost allocation policies (e.g., charge sub-agent cost to parent's budget)
- Cross-task sub-agent coordination (sub-agents only visible within their parent task)

## Open Questions

- Should enrichment happen synchronously after CLI execution, or async in background?
- How to correlate Claude Code session files with specific swarm invocations?
  (timestamp matching? inject a marker via prompt?)
- Should `AgentSpan` be persisted to ArcadeDB for historical analysis?
- For Godot rendering, what's the max sub-agent depth before visual clutter?
- Should the enricher run inside the worktree or on the host filesystem?
  (session files are on host, not in worktree)
