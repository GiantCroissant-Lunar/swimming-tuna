# RFC-015: Multi-Level Tracing & Enrichment

**Status:** Draft
**Date:** 2026-02-27
**Dependencies:** RFC-013 (Agent Hierarchy Model), RFC-006 (Langfuse Replay),
  RFC-008 (Godot Dashboard)

## Problem

The swarm dispatches tasks to CLI/API agents, but those agents may internally
spawn sub-agents: Claude Code uses `Task` to launch parallel workers, Pi uses
`/control` for peer agents, Cline makes multiple API calls with tool use loops.
Today the swarm sees only a flat execution: one agent in, one stdout out.

This creates three gaps:
1. **Godot can't render sub-agents as characters** — RFC-008 Layer 5 wants each
   agent as a character, but sub-agents are invisible
2. **Tracing is single-level** — Langfuse shows one span per role execution,
   with no visibility into internal agent decomposition
3. **Cost attribution is wrong** — a Claude Code agent spawning 3 sub-agents
   reports aggregate cost, not per-sub-agent breakdown

RFC-013 provides the data model (`AgentSpan`, `AgentSpanCollector`). This RFC
adds the enrichment pipeline that populates child spans and exports them to
Langfuse and Godot.

## Proposal

### 1. IOutputEnricher Interface

Each CLI adapter gets an enricher that parses structured output into child
`AgentSpan`s after execution completes (post-hoc, not real-time):

```csharp
namespace SwarmAssistant.Runtime.Hierarchy;

internal interface IOutputEnricher
{
    string AdapterId { get; }
    IReadOnlyList<AgentSpan> EnrichFromOutput(
        AgentSpan parentSpan,
        string rawOutput,
        string? sessionArtifactPath);
}
```

### 2. Adapter-Specific Enrichers

#### KiloOutputEnricher

Kilo with `--format json` emits newline-delimited JSON events during execution.
Alternatively, `kilo export <sessionId>` provides full per-turn breakdown post-hoc.

```csharp
internal sealed class KiloOutputEnricher : IOutputEnricher
{
    public string AdapterId => "kilo";

    // Parse kilo JSON events into child spans
    // Each assistant message with tokens becomes a Level 2 ToolCall span
    // Token data: tokens.{total, input, output, reasoning, cache.{read, write}}
}
```

**Adapter change:** Add `--format json` to kilo's `ExecuteArgs` to get structured
output alongside text. Parse the JSON stream to extract both the text output and
the structured token/tool data.

#### ClaudeCodeOutputEnricher

Claude Code session JSONL files contain full Anthropic API responses with usage:

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

**Sub-agent detection:** Look for tool_use blocks where `name == "Task"` in the
session JSONL. Each Task invocation is a Level 2 SubAgent span. Use `DetectFlavor`
from RFC-013 to classify as Normal or CoWork.

**Correlation challenge:** Match Claude Code session files to specific swarm
invocations via timestamp range matching or injecting a marker in the prompt.

#### ClineOutputEnricher

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

#### NoOpOutputEnricher (Copilot, Kimi, Pi)

No structured output available. Returns empty children list:

```csharp
internal sealed class NoOpOutputEnricher : IOutputEnricher
{
    public string AdapterId => "fallback";

    // Parent span only, no children.
    // Token usage estimated from prompt/output length.
}
```

Pi will move from NoOp to a real enricher once session file parsing is implemented.

### 3. Enrichment Pipeline

```csharp
internal sealed class EnrichmentPipeline
{
    private readonly IReadOnlyDictionary<string, IOutputEnricher> _enrichers;
    private readonly AgentSpanCollector _collector;

    // Called by AgentFrameworkRoleEngine after role execution completes
    public void Enrich(AgentSpan parentSpan, string rawOutput, string? sessionPath)
    {
        var enricher = _enrichers.GetValueOrDefault(parentSpan.AdapterId ?? "")
            ?? _enrichers["fallback"];

        var children = enricher.EnrichFromOutput(parentSpan, rawOutput, sessionPath);

        foreach (var child in children)
        {
            _collector.AddSpan(child);  // Store flat, linked by ParentSpanId
        }
    }
}
```

Enrichment happens **synchronously after CLI execution** — before the role result
is sent back to `TaskCoordinatorActor`. This ensures spans are available for
immediate Langfuse export.

### 4. Langfuse Integration

Map `AgentSpan` flat collection to nested OTLP spans using `ParentSpanId` →
`Activity.ParentId`:

```csharp
// Level 0 span (existing, enhanced with RFC-013 tags)
using var coordinatorActivity = telemetry.StartActivity("task.coordinate");
coordinatorActivity?.SetTag("agent.span.level", 0);
coordinatorActivity?.SetTag("agent.span.kind", "coordinator");

// Level 1 span (existing, enhanced)
using var agentActivity = telemetry.StartActivity("agent.execute",
    parentId: coordinatorActivity?.Id);
agentActivity?.SetTag("agent.span.level", 1);
agentActivity?.SetTag("agent.span.kind", "cli-agent");
agentActivity?.SetTag("agent.span.adapter", "kilo");

// Level 2 spans (NEW — from enrichment)
foreach (var child in collector.GetFlat(taskId).Where(s => s.ParentSpanId == agentSpan.SpanId))
{
    using var subActivity = telemetry.StartActivity("agent.sub-execute",
        parentId: agentActivity?.Id);
    subActivity?.SetTag("agent.span.level", child.Level);
    subActivity?.SetTag("agent.span.kind", child.Kind.ToString());
    subActivity?.SetTag("agent.span.flavor", child.Flavor.ToString());
    subActivity?.SetTag("gen_ai.usage.input_tokens", child.Usage?.InputTokens);
    subActivity?.SetTag("gen_ai.usage.output_tokens", child.Usage?.OutputTokens);
}
```

Result in Langfuse: nested trace tree showing run → coordinator → agent → sub-agents.

### 5. Run-Level Tracing

When RFC-014 is active, the `RunSpan` becomes the trace root:

```
Run: "RFC-012 Pi Adapter"                    ← RunSpan
 ├── Task: "Add Pi adapter definition"       ← AgentSpan L0
 │    ├── Planner (kilo)                     ← AgentSpan L1
 │    └── Builder (kilo)                     ← AgentSpan L1
 │         ├── tool: read file               ← AgentSpan L2
 │         └── tool: write file              ← AgentSpan L2
 └── Task: "Add unit tests"                  ← AgentSpan L0
      └── Builder (kilo)                     ← AgentSpan L1
```

Without RFC-014 (standalone tasks via `POST /a2a/tasks`), the L0 coordinator
span is the trace root — unchanged from current behavior.

### 6. Godot Dashboard Integration (RFC-008 Layer 5)

New `agent-hierarchy` A2UI surface payload. Uses `GetTree()` projection from
`AgentSpanCollector`:

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

### 7. Structured Output Availability

| Adapter | Token data | Sub-agent visibility | Enricher |
|---------|-----------|---------------------|----------|
| Kilo | `--format json` live + `kilo export` post-hoc | Tool calls | KiloOutputEnricher |
| Claude Code | Session JSONL (~/.claude/projects/) | Task sub-agents | ClaudeCodeOutputEnricher |
| Cline | `ui_messages.json` post-hoc | API calls | ClineOutputEnricher |
| Copilot | Logs only (timing, no tokens) | None | NoOpOutputEnricher |
| Kimi | None | None | NoOpOutputEnricher |
| Pi | TBD (session files) | TBD (/control) | NoOpOutputEnricher (v1) |
| API-direct | Full (from ModelResponse) | N/A | N/A (direct — already has exact tokens) |

## Implementation Tasks

1. `IOutputEnricher` interface
2. `KiloOutputEnricher` — parse `--format json` or `kilo export` output
3. `ClaudeCodeOutputEnricher` — parse session JSONL for turns and Task sub-agents
4. `ClineOutputEnricher` — parse `ui_messages.json` for API call entries
5. `NoOpOutputEnricher` — fallback for copilot/kimi/pi
6. `EnrichmentPipeline` — adapter lookup, orchestrates enrichment after role execution
7. Wire enrichment into `AgentFrameworkRoleEngine` after role completion
8. Langfuse nested span export — map AgentSpan tree to OTLP parent-child activities
9. A2UI `agent-hierarchy` payload — extend dashboard data model
10. Kilo adapter: add `--format json` or post-run `kilo export` call
11. Unit tests: each enricher with sample adapter output
12. Unit tests: enrichment pipeline adapter lookup and fallback
13. Integration test: submit task, verify Langfuse shows nested spans
14. Dogfood: verify Godot receives hierarchy payload

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Post-hoc enrichment | Not real-time streaming | CLI tools write structured data to files; easier to parse after completion |
| `IOutputEnricher` per adapter | Not universal parser | Each CLI tool has different structured output format |
| Synchronous enrichment | Not background async | Spans must be available for immediate Langfuse export |
| Graceful degradation | Level 2+ optional | Some adapters (copilot, kimi) can't report sub-agents; that's OK |
| Flavor detection heuristic | Not CLI-reported | CLI tools don't declare sub-agent coordination mode |

## Out of Scope

- Real-time sub-agent streaming to Godot (post-hoc is sufficient for v1)
- LLM capture proxy (alternative to post-hoc parsing — separate RFC if needed)
- Sub-agent cost allocation policies (charge sub-agent cost to parent's budget)
- Cross-task sub-agent coordination (sub-agents only visible within their parent task)

## Open Questions

- How to correlate Claude Code session files with specific swarm invocations?
  (timestamp matching? inject a marker via prompt?)
- Should `AgentSpan` be persisted to ArcadeDB for historical analysis?
- For Godot rendering, what's the max sub-agent depth before visual clutter?
- Should the enricher run inside the worktree or on the host filesystem?
  (session files are on host, not in worktree)
