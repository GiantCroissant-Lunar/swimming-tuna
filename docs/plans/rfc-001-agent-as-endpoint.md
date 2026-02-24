# RFC-001: Agent-as-Endpoint

**Status:** Draft
**Date:** 2026-02-24
**Dependencies:** None
**Implements:** Multi-agent evolution Phase A

## Problem

Today, agents (WorkerActor, ReviewerActor, SwarmAgentActor) are in-process Akka actors
that communicate via `.Tell()` / `.Ask()`. They have no external identity — no way for
another process, container, or SwarmAssistant instance to address them directly.

For true multi-agent communication, every agent needs to be an addressable endpoint.

## Proposal

Every agent — whether CLI-backed or API-backed — exposes an A2A HTTP endpoint that
other agents can discover and call.

### Agent Endpoint Contract

Each agent, when active, serves:

```
GET  /.well-known/agent-card.json   → identity, capabilities, cost model
POST /a2a/tasks                     → accept task assignment
GET  /a2a/tasks/{taskId}            → task status
POST /a2a/messages                  → receive peer message (new)
GET  /a2a/health                    → liveness check
```

### Agent Card Extension

Extend the existing agent card with per-agent fields:

```json
{
  "agentId": "builder-01",
  "name": "swarm-assistant-builder",
  "version": "phase-12",
  "protocol": "a2a",
  "capabilities": ["build", "code-generation"],
  "provider": "copilot-cli",
  "sandboxLevel": 0,
  "costModel": {
    "type": "subscription",
    "provider": "github-copilot"
  },
  "endpoints": {
    "submitTask": "/a2a/tasks",
    "sendMessage": "/a2a/messages",
    "health": "/a2a/health"
  }
}
```

### Endpoint Lifecycle

1. **Short-lived agents** (Phase A): container/process starts, registers its endpoint
   with the agent registry, serves requests, deregisters on shutdown.
2. **Long-lived agents** (future): persistent container with stable endpoint URL,
   survives across tasks.

### Relationship to Existing A2A

The current runtime-level A2A endpoints (`POST /a2a/tasks`, `GET /a2a/tasks`) become
the **orchestrator agent's** endpoints — it's just one agent that happens to coordinate.
The same protocol applies to all agents uniformly.

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Protocol | HTTP REST (A2A) | Already implemented for runtime; standard; works across containers |
| Agent identity | UUID per agent instance | Unique across restarts; registered in agent registry |
| Message endpoint | `POST /a2a/messages` | New — enables peer-to-peer messages beyond task delegation |
| Discovery | Via agent registry (RFC-004) | Agents register on start, deregister on stop |

## Out of Scope

- Agent registry implementation (see RFC-004)
- Peer communication patterns (see RFC-005)
- Container lifecycle management (see RFC-003)

## Open Questions

- Should short-lived agents use dynamic ports or a reverse proxy for stable URLs?
- Should agent cards include capability version/proficiency levels?
