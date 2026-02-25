# RFC-006: Langfuse as Replay Engine

**Status:** Draft
**Date:** 2026-02-24 (updated 2026-02-25)
**Dependencies:** RFC-002 (Artifact Model), RFC-009 (Agent Skills & Knowledge)
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

### Feedback Loop & Agent Learning (added 2026-02-25)

The original proposal is write-only: spans go into Langfuse, nothing comes back.
This section adds a bidirectional feedback loop that turns Langfuse into agent memory.

```
Today:     Agent → [executes] → Langfuse (traces, spans)
                                    ↓
                               (dead end)

Proposed:  Agent → [executes] → Langfuse (traces, spans, scores, annotations)
                                    ↓
           Agent ← [queries]  ← Langfuse (similar traces, quality signals, patterns)
```

#### Score Writing (after reviewer decisions)

When the reviewer accepts or rejects builder output, write a score to Langfuse:

```
POST /api/public/scores
{
  "traceId": "run-001",
  "name": "reviewer_verdict",
  "value": 1.0,                            // 1.0 = accepted, 0.0 = rejected
  "observationId": "builder-01.execute-role",
  "comment": "Accepted: clean implementation, tests pass",
  "dataType": "NUMERIC"
}
```

Additional scores per dimension:

| Score name | Value range | When written |
|-----------|-------------|--------------|
| `reviewer_verdict` | 0.0 / 1.0 | After reviewer accept/reject |
| `spec_consistency` | 0.0 - 1.0 | After gatekeeper review (RFC-009 skill) |
| `test_coverage` | 0.0 - 1.0 | After test runner validates |
| `gatekeeper_fixes` | 0 - N (count) | After gatekeeper commits |

#### Similarity Queries (before planning)

Before a planner starts work, query Langfuse for traces with similar task descriptions:

```
GET /api/public/traces?tags=role:planner&limit=10
→ Filter by task description similarity (keyword overlap or embedding distance)
→ For each trace: fetch scores (reviewer_verdict, gatekeeper_fixes)
→ Build a "lessons from similar tasks" context block
```

Example injected context:

```
--- Langfuse Learning Context ---
Based on 3 similar past tasks:
  - "Add GET /a2a/health endpoint" → accepted first try, 0 gatekeeper fixes
  - "Add POST /a2a/tasks endpoint" → accepted first try, 2 gatekeeper fixes
    Annotation: "OpenAPI spec had nullable mismatch with DTO"
  - "Add agent heartbeat endpoint" → rejected once, accepted on retry
    Annotation: "Missing error handling for 503/504"

Common patterns:
  - API endpoint tasks need spec-vs-code consistency checking
  - Error handling for timeout/unavailable cases frequently missed
--- End Langfuse Learning Context ---
```

This replaces or augments StrategyAdvisorActor's statistical insights with
**semantic, annotation-rich** historical context.

#### Annotation API

Annotations add human or gatekeeper knowledge to traces:

```
POST /api/public/comments
{
  "traceId": "run-001",
  "observationId": "builder-01.execute-role",
  "content": "Schema nullability must match DTO: lastHeartbeat was nullable in spec but non-nullable in code"
}
```

These annotations are queryable and feed into the learning context for future tasks.

#### Knowledge Extraction Pipeline

Periodically (or after each dogfooding cycle), extract patterns from Langfuse:

```
1. Query traces with low reviewer_verdict scores
2. Extract reviewer comments and gatekeeper annotations
3. Cluster by similarity (task type, failure pattern)
4. Auto-generate RFC-009 KnowledgeEntry records
5. Optionally propose new skills from recurring patterns

Example:
  3 traces failed with "nullable mismatch" annotation
  → Generate KnowledgeEntry: category=gotcha, content="Verify nullable alignment..."
  → Propose skill: "spec-consistency" (if not already exists)
```

This closes the loop: execution → scoring → querying → learning → better execution.

#### Integration with RFC-009 Shared Knowledge

Langfuse feedback feeds into the RFC-009 shared knowledge layer:

```
Langfuse scores/annotations → Knowledge extraction pipeline → KnowledgeEntry records
                                                                    ↓
                                                            Blackboard / qmd index
                                                                    ↓
                                                            Agent prompt injection
```

The StrategyAdvisorActor evolves from purely statistical to semantically rich:

| Current | Evolved |
|---------|---------|
| "3 similar tasks, 67% success rate" | "3 similar tasks: API endpoints need spec consistency, error handling for timeouts" |
| "Average 1.2 retries" | "Common rejection: nullable mismatch (3 occurrences)" |
| "Adapter success: copilot 80%" | "Copilot struggles with multi-file spec changes, succeeds at single-file implementations" |

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Cost source of truth | Langfuse | Already captures token usage; single source |
| Replay mechanism | Langfuse API queries + git diffs | No new storage needed; data already exists |
| Trace structure | Standardized span attributes | Enables consistent querying across agents |
| Refresh frequency | Poll Langfuse API periodically | Real-time not needed; dashboard updates every few seconds |
| Score granularity | Per-dimension (verdict, spec, tests) | Enables targeted learning per failure type |
| Similarity matching | Keyword overlap (phase 1), embedding (phase 2) | Simple first, semantic when qmd available |
| Knowledge extraction | Post-cycle batch (not real-time) | Avoids noise from in-progress tasks |

## Out of Scope

- Langfuse self-hosting configuration (already in `project/infra/langfuse/`)
- Real-time streaming from Langfuse (polling is sufficient initially)
- Cost prediction / forecasting
- Automatic skill generation from Langfuse patterns (future: RFC-009 extension)

## Open Questions

- What Langfuse API rate limits apply? Need to batch queries efficiently.
- Should we cache Langfuse data locally (ArcadeDB?) for faster dashboard refresh?
- How to handle cost for agents that use multiple models within one task?
- What similarity threshold for "similar task" queries? Too low = noise, too high = no matches.
- Should annotations be structured (JSON) or free-text? Structured enables clustering but adds friction.
- How to prevent the learning context from growing too large over many cycles?
