# RFC-008: Godot Dashboard

**Status:** Draft
**Date:** 2026-02-24
**Dependencies:** RFC-004 (Agent Registry & Discovery)
**Implements:** Multi-agent evolution Phase A–D (incremental)

## Problem

The current Godot UI renders A2UI surfaces for individual tasks. For the tycoon-style
multi-agent system, we need a dashboard that shows the swarm as a whole: who's active,
what they're doing, how they're communicating, and what it costs.

## Proposal

Build the Godot dashboard incrementally through four layers, each delivered via
A2UI/AG-UI/GenUI protocols adapted for Godot's scene/node system.

### Layer 1: Agent List (Phase A)

Table/list view showing all registered agents:

```
┌──────────────────────────────────────────────────────────┐
│ AGENTS                                          [3 active]│
├──────────┬──────────┬─────────┬────────┬────────┬────────┤
│ Agent    │ Role     │ Status  │Provider│ Budget │ Task   │
├──────────┼──────────┼─────────┼────────┼────────┼────────┤
│ planner  │ planner  │ ● active│copilot │ ∞ free │ task-01│
│ builder  │ builder  │ ● active│api     │ 376k   │ task-01│
│ reviewer │ reviewer │ ○ idle  │cline   │ ∞ free │ —      │
│ builder-2│ builder  │ ◐ low   │api     │ 12k ⚠  │ task-02│
└──────────┴──────────┴─────────┴────────┴────────┴────────┘
```

**Data source:** Agent Registry (RFC-004) via new AG-UI endpoint
**Godot implementation:** Tree node or ItemList with custom draw

### Layer 2: Task Tree (Phase A)

Hierarchical view of runs, tasks, and agent assignments:

```
┌──────────────────────────────────────────────────────┐
│ TASKS                                                │
│                                                      │
│ ▼ Run: run-001 (active, 3 tasks, $0.45)             │
│   ├── task-01: Design API          [planner] ● done  │
│   ├── task-02: Implement API       [builder] ● active│
│   │   ├── subtask: /users endpoint [builder] ● active│
│   │   └── subtask: /auth endpoint  [builder-2] ○ queued│
│   └── task-03: Review API          [reviewer] ○ waiting│
│                                                      │
│ ▶ Run: run-000 (complete, 2 tasks, $0.12)            │
└──────────────────────────────────────────────────────┘
```

**Data source:** TaskRegistry + ArcadeDB via existing `/a2a/tasks` and `/memory/tasks`
**Godot implementation:** Tree node with collapsible runs

### Layer 3: Network Graph (Phase B)

Visual graph showing agent communication topology:

```
┌──────────────────────────────────────────────────────┐
│ NETWORK                                              │
│                                                      │
│         [planner]                                    │
│          /    \                                      │
│    plan ↙      ↘ plan                               │
│        /        \                                    │
│   [builder]──────[reviewer]                         │
│       │    review    │                               │
│       │              │                               │
│   [builder-2]        │                               │
│       └──────────────┘                               │
│          feedback                                    │
│                                                      │
│ ── active link   ╌╌ idle link   → message direction  │
└──────────────────────────────────────────────────────┘
```

**Data source:** Langfuse communication spans (RFC-006) + blackboard signals
**Godot implementation:** GraphEdit node or custom 2D node with edge drawing

### Layer 4: Cost HUD (Phase C)

Overlay showing real-time swarm economics:

```
┌─────────────────────────────────────────┐
│ BUDGET          Run: $0.45 │ Rate: $0.02/min │
│                                              │
│ ▓▓▓▓▓▓▓▓░░ 78% subscription (free)          │
│ ▓▓░░░░░░░░ 22% API ($0.45)                  │
│                                              │
│ Tokens: 45.2k in / 89.1k out                │
│ Agents: 3 active / 1 low budget / 0 departed│
└──────────────────────────────────────────────┘
```

**Data source:** Langfuse API (RFC-006) + agent registry budget cache
**Godot implementation:** HBoxContainer with ProgressBar and Label nodes

### Layer 5: Agent Characters (Phase D — future)

Pixel-art or stylized characters representing agents in a 2D workspace:

- Characters walk, sit at desks, show typing/reading animations
- Speech bubbles for agent-to-agent messages
- Visual proximity reflects communication frequency
- Tycoon HUD overlays the character view

This layer is cosmetic on top of Layers 1–4. The data is the same;
only the rendering changes from tables/graphs to characters.

**Godot implementation:** TileMap + AnimatedSprite2D + A2UI surface payloads

### Protocol: A2UI for Dashboard

Extend A2UI surface payloads to carry dashboard data:

```json
{
  "type": "updateDataModel",
  "surfaceType": "dashboard",
  "layer": "agent-list",
  "data": {
    "agents": [
      {
        "agentId": "builder-01",
        "role": "builder",
        "status": "active",
        "provider": "copilot",
        "budgetRemaining": 376544,
        "currentTaskId": "task-01"
      }
    ]
  }
}
```

The Godot client receives A2UI events via AG-UI SSE stream and routes to the
appropriate dashboard layer for rendering.

### GenUI for Dynamic Surfaces

For agent-generated UI (e.g., an agent wants to show its plan or ask for input),
use GenUI payloads that the Godot client renders dynamically:

```json
{
  "type": "genui",
  "agentId": "planner-01",
  "surface": {
    "type": "form",
    "title": "Plan Approval",
    "fields": [
      { "type": "text", "label": "Plan Summary", "value": "..." },
      { "type": "button", "label": "Approve", "action": "approve_plan" },
      { "type": "button", "label": "Reject", "action": "reject_plan" }
    ]
  }
}
```

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Rendering framework | Godot Control nodes (Tree, ItemList, GraphEdit) | Native Godot UI; performant; themeable |
| Data transport | AG-UI SSE + A2UI payloads | Already implemented; dashboard is another surface type |
| Incremental delivery | Layers 1–4 before characters | Data first, cosmetics later |
| Character style | Deferred to Phase D | Focus on function over form initially |

## Out of Scope

- Mobile/web dashboard (Godot desktop only for now)
- Real-time collaboration (multiple humans viewing same dashboard)
- Dashboard customization (drag-and-drop layout)

## Open Questions

- Should dashboard layers be dockable/rearrangeable panels or fixed layout?
- Should the network graph auto-layout or allow manual positioning?
- How to handle 50+ agents visually without clutter?
- Should characters use pixel-agents style sprites or a custom art direction?
