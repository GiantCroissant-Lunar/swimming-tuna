# RFC-007: Budget-Aware Lifecycle

**Status:** Draft
**Date:** 2026-02-24
**Dependencies:** RFC-004 (Agent Registry & Discovery), RFC-006 (Langfuse as Replay Engine)
**Implements:** Multi-agent evolution Phase C

## Problem

Agents consume resources: API tokens, subscription quotas, compute time. Today, agents
run until their task is done or they crash — there is no awareness of cost constraints.
In a tycoon-style system, budget is a core mechanic: agents "come" when they have budget,
"go" when it's exhausted.

## Proposal

Make budget a first-class lifecycle constraint. The agent registry tracks budget per agent,
the auto-scaler factors cost into spawn/despawn decisions, and agents self-report or get
evicted when budget is exhausted.

### Budget Model

Each agent has a budget envelope:

```json
{
  "agentId": "builder-01",
  "budget": {
    "type": "token-limited",
    "totalTokens": 500000,
    "usedTokens": 0,
    "warningThreshold": 0.8,
    "hardLimit": 1.0,
    "refreshPolicy": "none"
  }
}
```

Budget types:

| Type | Description | Example |
|------|-------------|---------|
| `token-limited` | Fixed token budget, exhausted when used | API key with spending cap |
| `rate-limited` | Tokens per time window, refreshes periodically | Subscription with daily/monthly quota |
| `unlimited` | No token constraint (but may have rate limits) | Enterprise subscription |
| `pay-per-use` | No cap, but cost accumulates | API key with billing |

### Lifecycle Phases

```
Budget Available
    │
    ▼
┌─────────────┐
│   SPAWNING   │ ← Container/process starting
└──────┬──────┘
       │
       ▼
┌─────────────┐
│   ACTIVE     │ ← Accepting tasks, heartbeating
│              │    Budget: [████████░░] 80%
└──────┬──────┘
       │ budget < warningThreshold
       ▼
┌─────────────┐
│   LOW_BUDGET │ ← Still active, registry marks as low priority
│              │    Budget: [██████████] 95% used
│              │    Dashboard: amber warning
└──────┬──────┘
       │ budget >= hardLimit
       ▼
┌─────────────┐
│  EXHAUSTED   │ ← Finishes current task, accepts no new tasks
│              │    Budget: [██████████] 100% used
│              │    Deregisters from registry
└──────┬──────┘
       │
       ▼
┌─────────────┐
│  DEPARTED    │ ← Container/process stopped, cleanup done
│              │    "Agent has left"
└─────────────┘
```

### Auto-Scaling with Budget Awareness

Evolve the existing auto-scale scaffolding (`AutoScaleEnabled`, `MinPoolSize`,
`MaxPoolSize`, `ScaleUpThreshold`, `ScaleDownThreshold`) with budget logic:

**Scale-up decision:**
1. Task queue depth exceeds `ScaleUpThreshold`
2. Query registry for available agents with matching capability
3. If no available agents or all are `LOW_BUDGET`:
   - Check if budget allows spawning a new agent
   - Prefer subscription agents (cheaper) over API agents
   - Prefer agents with more remaining budget
4. Spawn agent, register, assign task

**Scale-down decision:**
1. Idle agents exceed `ScaleDownThreshold` for N minutes
2. Despawn idle agents, preferring to keep:
   - Subscription agents (no marginal cost to keep alive)
   - Agents with specialized capabilities (harder to replace)
3. Always keep `MinPoolSize` agents alive

**Budget-exhausted handling:**
1. Agent reports budget exhausted (or Langfuse data shows limit reached)
2. Registry moves agent to `EXHAUSTED` status
3. Agent finishes current task (no new assignments)
4. Agent deregisters and shuts down
5. If work remains: auto-scaler spawns replacement (if budget available elsewhere)

### Provider Cost Strategy

```
Preference order (cheapest first):
1. Subscription CLI (Copilot, Cline) — $0 marginal cost
2. pi-mono bridged subscription — subscription includes API access
3. API key (Anthropic, OpenAI) — pay-per-token

Override: if task requires specific capability only available from
expensive provider, use it regardless of cost ranking.
```

### Dashboard Integration (RFC-008)

The Godot HUD shows:

```
┌─────────────────────────────────────────┐
│ SWARM BUDGET                            │
│ ┌─────────────────────┐                 │
│ │ Total spent: $1.23  │ Burn: $0.02/min │
│ └─────────────────────┘                 │
│                                         │
│ builder-01 [copilot]  ████████░░  free  │
│ builder-02 [api]      ██████░░░░  $0.45 │
│ reviewer-01 [cline]   █████████░  free  │
│ planner-01 [api]      ██░░░░░░░░  $0.78 │
│                          ⚠ low budget   │
└─────────────────────────────────────────┘
```

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Budget tracking | Langfuse (source) + registry (cache) | Langfuse has actuals; registry needs fast access |
| Scale preference | Cheapest capable agent first | Tycoon principle: minimize cost |
| Exhausted behavior | Finish current task, then depart | Don't abandon in-flight work |
| Budget refresh | Periodic poll from Langfuse | Real-time not critical; seconds-level freshness sufficient |

## Out of Scope

- Budget forecasting (predict when agent will exhaust)
- Multi-currency budgets (tokens vs. dollars vs. API credits)
- Budget approval workflows (human approves high-cost spawns)

## Open Questions

- How to handle subscription quotas that aren't visible via API (e.g., Copilot rate limits)?
- Should agents self-report usage or should the runtime track it centrally?
- What happens if ALL agents exhaust budget and tasks remain? Alert? Queue? Pause?
