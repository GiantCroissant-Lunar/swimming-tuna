# RFC-002: Artifact Model

**Status:** Draft
**Date:** 2026-02-24
**Dependencies:** None
**Implements:** Multi-agent evolution (cross-cutting)

## Problem

Agents produce outputs — code files, UI designs, config changes, documentation. Today
these are invisible to the system: they exist only as side effects on the file system.
There is no way to:

- Know which agent produced which file
- Bundle all outputs from a run/session
- Replay a session by reconstructing artifacts forward/backward
- Track artifact lineage across tasks

## Proposal

Introduce **artifacts** as first-class entities tracked per agent, per task, per run.

### Artifact Definition

An artifact is any addressable output produced by an agent during task execution:

```json
{
  "artifactId": "art-abc123",
  "runId": "run-001",
  "taskId": "task-builder-01",
  "agentId": "builder-01",
  "type": "file",
  "path": "src/Actors/NewActor.cs",
  "contentHash": "sha256:abcdef...",
  "createdAt": "2026-02-24T10:30:00Z",
  "metadata": {
    "language": "csharp",
    "linesAdded": 45,
    "linesRemoved": 0
  }
}
```

### Artifact Types

| Type | Examples | Captured How |
|------|----------|--------------|
| `file` | Source code, config, docs | Git diff per agent workspace branch |
| `design` | UI mockups, diagrams | File output with mime type |
| `trace` | Agent decision logs | Langfuse trace spans |
| `message` | Agent-to-agent communications | Langfuse + blackboard snapshots |

### Run Bundle

A **run** (session) bundles all artifacts produced by all agents across all tasks:

```
Run "run-001"
├── Task "plan-api-redesign"
│   └── Agent planner-01
│       ├── artifact: trace (Langfuse span)
│       └── artifact: file (docs/api-plan.md)
├── Task "implement-api"
│   └── Agent builder-01
│       ├── artifact: trace (Langfuse span)
│       ├── artifact: file (src/ApiController.cs)
│       └── artifact: file (tests/ApiControllerTests.cs)
└── Task "review-api"
    └── Agent reviewer-01
        ├── artifact: trace (Langfuse span)
        └── artifact: message (review feedback to builder-01)
```

### Replay Model

Replay = **trace actions** (from Langfuse) + **artifact snapshots** (from git/storage):

- **Forward replay**: step through agent actions chronologically, reconstruct
  artifacts as they were created/modified at each step
- **Backward replay**: start from final state, walk backward through diffs
  to see how each artifact evolved
- **Artifact at point-in-time**: given a timestamp, reconstruct the exact state
  of any artifact by applying diffs up to that point

### Storage Strategy

- **Traces**: Langfuse (already captured)
- **File artifacts**: Git commits on workspace branches (`swarm/task-{id}`),
  each agent commit tagged with agentId and taskId
- **Artifact index**: ArcadeDB or Langfuse metadata — maps artifactId to
  run/task/agent and storage location

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Artifact identity | Content-addressable (hash) | Deduplication, integrity verification |
| Storage | Git for files, Langfuse for traces | Leverage existing infrastructure |
| Bundle scope | Per run (session) | Natural unit of work; contains all tasks |
| Replay engine | Langfuse traces + git diffs | Both already captured; reconstruction is query |

## Out of Scope

- Replay UI in Godot (see RFC-006 for Langfuse integration, RFC-008 for dashboard)
- Artifact diffing/merge across agents (future)

## Open Questions

- Should artifact metadata be stored in Langfuse span attributes or separately?
- How to handle artifacts from agents that don't use workspace branches?
- Should we version bundles (run v1, run v2 after retry)?
