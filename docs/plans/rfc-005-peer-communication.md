# RFC-005: Peer Communication

**Status:** Draft
**Date:** 2026-02-24
**Dependencies:** RFC-001 (Agent-as-Endpoint), RFC-004 (Agent Registry & Discovery)
**Implements:** Multi-agent evolution Phase B

## Problem

Today, all task routing goes through the coordinator: Agent A cannot ask Agent B for help
directly. The coordinator is a bottleneck and a single point of control. For true
multi-agent collaboration, agents need to communicate peer-to-peer.

## Proposal

Enable bilateral A2A communication between agents. Any agent can send a message to any
other agent it discovers via the registry. The orchestrator becomes one agent among peers,
not a privileged router.

### Message Types

Agents communicate via `POST /a2a/messages` (defined in RFC-001):

```json
{
  "messageId": "msg-001",
  "fromAgentId": "planner-01",
  "toAgentId": "builder-01",
  "type": "task-request",
  "payload": {
    "taskId": "task-implement-api",
    "description": "Implement the /users endpoint based on the plan",
    "artifacts": ["art-plan-001"],
    "replyTo": "http://localhost:5090/a2a/messages"
  },
  "timestamp": "2026-02-24T10:30:00Z"
}
```

### Communication Patterns

**1. Task Delegation (one-to-one)**
```
Planner → Builder: "Implement this based on my plan"
Builder → Planner: "Done, here are the artifacts"
```
This is what the orchestrator does today, but now any agent can delegate.

**2. Help Request (one-to-many via registry)**
```
Builder: Query registry for agents with "code-review" capability
Builder → Reviewer: "Can you review this code?"
Reviewer → Builder: "Found 3 issues, see feedback"
```

**3. Broadcast (via blackboard)**
```
Builder: Write to blackboard: "task-123 build complete"
All agents: Read blackboard, react if relevant
```

**4. Negotiation (contract-net, activated from dormant code)**
```
Coordinator: Publish CFP to registry: "Need a builder for Python task"
Builder-01: Bid { cost: 0, eta: "5min", capability_match: 0.9 }
Builder-02: Bid { cost: 0.02, eta: "2min", capability_match: 0.95 }
Coordinator: Award to Builder-02 (faster, better match)
```

### Blackboard Integration

The existing `BlackboardActor` mediates coordination signals:

| Signal | Publisher | Consumers | Purpose |
|--------|-----------|-----------|---------|
| `agent.joined` | Registry | All agents | New peer available |
| `agent.left` | Registry | All agents | Peer departed |
| `task.available` | Any agent | Capable agents | Work to claim |
| `task.claimed` | Agent | All agents | Prevent double-work |
| `task.complete` | Agent | Dependent agents | Unblock downstream |
| `artifact.produced` | Agent | Interested agents | New output available |
| `help.needed` | Agent | Capable agents | Request assistance |

### Orchestrator as Peer

The coordinator/orchestrator becomes an agent with special capabilities but no special
protocol privileges:

```json
{
  "agentId": "orchestrator-01",
  "capabilities": ["orchestrate", "plan", "delegate", "monitor"],
  "role": "coordinator"
}
```

It uses the same `POST /a2a/messages` to delegate tasks. Other agents *can* bypass it
and communicate directly — but they don't *have to*. Centralized orchestration is just
one valid topology, not the only one.

### Communication Topology Examples

**Centralized (current, degenerate case):**
```
         Orchestrator
        /     |      \
    Planner Builder Reviewer
```

**Star with peer shortcuts:**
```
         Orchestrator
        /     |      \
    Planner→Builder→Reviewer
```

**Mesh (fully peer-to-peer):**
```
    Planner ↔ Builder
       ↕         ↕
    Reviewer ↔ Orchestrator
```

All three topologies use the same protocol. The topology emerges from which agents
choose to communicate with whom.

### Tracing

All peer messages are traced via Langfuse:
- Each message creates a span linked to the parent task trace
- `fromAgentId` and `toAgentId` are span attributes
- Message payload captured for replay (RFC-002, RFC-006)

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Peer protocol | A2A HTTP (same as task submission) | Uniform protocol; no new transport |
| Message routing | Direct HTTP call to peer endpoint | No broker needed; agents know peer URLs from registry |
| Blackboard role | Coordination signals, not message relay | Keep blackboard lightweight; direct messages go peer-to-peer |
| Orchestrator privilege | None (same protocol as all agents) | True multi-agent; orchestration is a capability, not a privilege |

## Out of Scope

- Message ordering guarantees (agents handle idempotency)
- Persistent message queues (messages are fire-and-forget with retry)
- Cross-instance peer communication (future: requires registry federation)

## Open Questions

- Should there be a message size limit (to prevent agents sending huge payloads)?
- How to handle message delivery to an agent that just departed (race condition)?
- Should agents be allowed to subscribe to specific blackboard signal types?
