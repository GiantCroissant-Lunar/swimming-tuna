# RFC-006: Langfuse as Replay Engine

**Status:** Draft
**Date:** 2026-02-24
**Dependencies:** RFC-002 (Artifact Model)
**Implements:** Multi-agent evolution Phase C

## Problem

Langfuse is currently used as a tracing tool — spans go in, dashboards come out. But the
trace data contains everything needed to reconstruct what agents did: inputs, outputs,
token usage, timing, and (with RFC-002) artifact references. This makes Langfuse the
foundation for session replay, cost accounting, and agent performance analysis.

## Proposal

Promote Langfuse from "observability tool" to **replay engine and cost source of truth**.
The SwarmAssistant runtime queries Langfuse for:

1. **Token/cost metering** per agent, per task, per run
2. **Action replay** — chronological reconstruction of agent decisions
3. **Data model in/out** — what each agent received and produced
4. **Communication graph** — which agents talked to whom (via RFC-005 trace spans)

### Langfuse Data Model Mapping

```
Langfuse Trace  ←→  SwarmAssistant Run (session)
Langfuse Span   ←→  Agent action (role execution, message, tool use)
Span Attributes ←→  Agent metadata (agentId, taskId, provider, sandbox level)
Span Input      ←→  Data model IN (prompt, task description, peer message)
Span Output     ←→  Data model OUT (result, artifacts produced, response)
Token Usage     ←→  Cost (input tokens, output tokens, model, price)
```

### API Integration

Query Langfuse REST API to power the dashboard and agent registry:

**Cost per agent:**
```
GET /api/public/traces?tags=agentId:builder-01
→ Sum token usage across all spans
→ Apply price model (subscription = $0, API = per-token rate)
```

**Replay timeline:**
```
GET /api/public/traces/{traceId}/spans
→ Ordered list of agent actions with inputs/outputs
→ Cross-reference with artifact snapshots (RFC-002) for reconstruction
```

**Communication graph:**
```
GET /api/public/traces?tags=runId:run-001
→ Extract spans with fromAgentId/toAgentId attributes
→ Build directed graph of agent communications
```

### Trace Structure

Standardize how SwarmAssistant emits traces:

```
Trace: run-001
├── Span: orchestrator-01.delegate-planning
│   ├── agentId: orchestrator-01
│   ├── targetAgentId: planner-01
│   ├── input: { taskDescription: "Design API..." }
│   └── output: { delegated: true, taskId: "task-plan-01" }
│
├── Span: planner-01.execute-role
│   ├── agentId: planner-01
│   ├── role: "planner"
│   ├── input: { task: "Design API...", context: "..." }
│   ├── output: { plan: "...", artifacts: ["art-plan-001"] }
│   ├── tokens: { input: 2500, output: 1200, model: "claude-sonnet-4-6" }
│   └── cost: { usd: 0.011 }
│
├── Span: planner-01.message.builder-01
│   ├── type: "peer-message"
│   ├── fromAgentId: planner-01
│   ├── toAgentId: builder-01
│   ├── input: { message: "Plan ready, see artifact art-plan-001" }
│   └── output: { delivered: true }
│
└── Span: builder-01.execute-role
    ├── agentId: builder-01
    ├── role: "builder"
    ├── input: { plan: "art-plan-001", task: "Implement API..." }
    ├── output: { artifacts: ["art-code-001", "art-code-002"] }
    ├── tokens: { input: 4000, output: 8000, model: "claude-sonnet-4-6" }
    └── cost: { usd: 0.036 }
```

### Replay Reconstruction

**Forward replay** (step-by-step reconstruction):
1. Fetch all spans for a trace, ordered by startTime
2. For each span: show agent action, input, output
3. For artifact-producing spans: fetch git diff at that point (RFC-002)
4. Result: chronological movie of what happened

**Backward replay** (undo from final state):
1. Start from final artifact state
2. Walk spans in reverse chronological order
3. For each artifact-producing span: reverse the diff
4. Result: see how the output was constructed incrementally

**Point-in-time snapshot:**
1. Given timestamp T, find all spans with endTime <= T
2. Aggregate: which agents were active, what artifacts existed, token usage so far
3. Result: frozen state of the swarm at time T

### Cost Accounting

Langfuse captures token usage per span. Map to actual cost:

| Provider | Cost Model | Source |
|----------|-----------|--------|
| Subscription CLI (Copilot, Cline) | $0 per token (flat subscription) | Agent card `costModel.type = "subscription"` |
| API key (Anthropic, OpenAI) | Per-token rate | Langfuse token counts × model pricing |
| pi-mono bridged | Subscription rate but API access | Agent card `costModel.type = "subscription-api"` |

Dashboard shows: cost per agent, cost per task, cost per run, burn rate, budget remaining.

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Cost source of truth | Langfuse | Already captures token usage; single source |
| Replay mechanism | Langfuse API queries + git diffs | No new storage needed; data already exists |
| Trace structure | Standardized span attributes | Enables consistent querying across agents |
| Refresh frequency | Poll Langfuse API periodically | Real-time not needed; dashboard updates every few seconds |

## Out of Scope

- Langfuse self-hosting configuration (already in `project/infra/langfuse/`)
- Real-time streaming from Langfuse (polling is sufficient initially)
- Cost prediction / forecasting

## Open Questions

- What Langfuse API rate limits apply? Need to batch queries efficiently.
- Should we cache Langfuse data locally (ArcadeDB?) for faster dashboard refresh?
- How to handle cost for agents that use multiple models within one task?
