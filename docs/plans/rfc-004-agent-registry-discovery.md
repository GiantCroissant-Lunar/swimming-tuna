# RFC-004: Agent Registry & Discovery

**Status:** Draft
**Date:** 2026-02-24
**Dependencies:** RFC-001 (Agent-as-Endpoint)
**Implements:** Multi-agent evolution Phase A

## Problem

Today, `CapabilityRegistryActor` tracks which in-process actors can handle which roles.
But for multi-agent with containerized agents coming and going, we need a dynamic registry
that tracks live agents, their capabilities, endpoints, cost models, and health.

## Proposal

Evolve `CapabilityRegistryActor` into a full **Agent Registry** that serves as the
discovery backbone for all agents — in-process or external.

### Registry Data Model

```json
{
  "agentId": "builder-01",
  "name": "swarm-builder",
  "status": "active",
  "endpoint": "http://localhost:5091",
  "agentCard": { "...see RFC-001..." },
  "capabilities": ["plan", "build", "code-generation"],
  "sandboxLevel": 2,
  "provider": {
    "adapter": "copilot",
    "type": "subscription",
    "plan": "github-copilot-business"
  },
  "budget": {
    "totalTokens": 500000,
    "usedTokens": 123456,
    "remaining": 376544,
    "source": "langfuse"
  },
  "health": {
    "lastHeartbeat": "2026-02-24T10:30:00Z",
    "consecutiveFailures": 0,
    "circuitBreakerState": "closed"
  },
  "registeredAt": "2026-02-24T10:00:00Z",
  "lastTaskAt": "2026-02-24T10:25:00Z"
}
```

### Registry Operations

```
Register(agentCard)         → agent joins the swarm
Deregister(agentId)         → agent leaves (graceful)
Heartbeat(agentId)          → agent is alive
Query(capabilities, filter) → find agents matching criteria
Evict(agentId)              → force-remove (missed heartbeats)
```

### Discovery Flow

```
Agent starts up
    │
    ├─ Provisions sandbox (RFC-003)
    ├─ Starts A2A endpoint (RFC-001)
    ├─ Calls Registry.Register(agentCard)
    │
    ▼
Registry assigns agentId, records endpoint
    │
    ├─ Publishes "agent.joined" to blackboard
    ├─ Other agents can Query registry to discover new peer
    │
    ▼
Agent begins heartbeat loop (every N seconds)
    │
    ├─ Registry tracks health
    ├─ If heartbeat missed K times → Evict
    │
    ▼
Agent completes / budget exhausted / shutdown
    │
    ├─ Calls Registry.Deregister(agentId)
    ├─ Registry publishes "agent.left" to blackboard
    └─ Cleanup sandbox
```

### Capability Matching

When a task needs an agent, the registry finds candidates:

```
Query: { capabilities: ["build"], prefer: "cheapest" }

Registry returns ranked list:
1. builder-01 (subscription, $0/task, 376k tokens remaining)
2. builder-02 (api, $0.003/1k tokens, unlimited)
3. builder-03 (subscription, $0/task, 12k tokens remaining — low budget)
```

The caller (orchestrator agent or any peer) picks from the list.

### Evolution from CapabilityRegistryActor

| Current | Evolved |
|---------|---------|
| `CapabilityRegistryActor` | `AgentRegistryActor` |
| In-process actor refs only | External endpoints + in-process refs |
| Static capability list | Dynamic: capabilities + cost + health |
| Contract-net CFP (dormant) | Active discovery + budget-aware selection |
| No health tracking | Heartbeat + circuit breaker |

The existing contract-net scaffolding (`ContractNetCallForProposals`,
`ContractNetBidRequest` in `CapabilityRegistryActor.cs`) becomes the mechanism
for capability-based agent selection — activated rather than rewritten.

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Registry location | In-process actor (host runtime) | Simple; host is always running; agents register via HTTP |
| Health mechanism | Heartbeat + eviction | Standard; handles ungraceful shutdowns |
| Budget data source | Langfuse API (RFC-006) | Token usage already tracked there |
| Selection strategy | Pluggable (cheapest, fastest, round-robin) | Different tasks have different priorities |

## Out of Scope

- Cross-instance registry federation (multiple SwarmAssistant hosts)
- Agent reputation/quality scoring
- Automatic capability inference from past performance

## Open Questions

- Should the registry itself be an agent with an A2A endpoint (full dogfooding)?
- How often should budget data refresh from Langfuse?
- Should capability matching support fuzzy/semantic matching (e.g., "can code" matches "build")?
