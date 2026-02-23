# AG-UI Event Reference

This document defines the event names and payload shapes emitted to the `/ag-ui/events` stream.

## Task Lifecycle Events

### `agui.task.submitted`
Emitted by `DispatcherActor` when a top-level task is dispatched.

```json
{
  "taskId": "task-abc",
  "title": "Implement feature X",
  "description": "Full description of the task"
}
```

### `agui.task.done`
Emitted by `TaskCoordinatorActor` when a task completes successfully.

```json
{
  "summary": "Task 'Implement feature X' completed.\nPlan: ...\nBuild: ...\nReview: ...",
  "source": "task-task-abc"
}
```

### `agui.task.failed`
Emitted by `TaskCoordinatorActor` when a task fails (blocked/escalated).

```json
{
  "source": "task-task-abc",
  "error": "Role failed: ...",
  "a2ui": { ... }
}
```

### `agui.task.retry`
Emitted by `TaskCoordinatorActor` when a supervisor-initiated retry begins.

```json
{
  "source": "task-task-abc",
  "role": "builder",
  "reason": "Low quality output, retrying"
}
```

### `agui.task.transition`
Emitted by `TaskCoordinatorActor` at decision points (orchestrator/GOAP/consensus transitions).

```json
{
  "source": "task-task-abc",
  "action": "Plan",
  "decidedBy": "orchestrator"
}
```

### `agui.task.decision`
Emitted by `TaskCoordinatorActor` when a reviewer decision is made.

```json
{
  "source": "task-task-abc",
  "action": "ReviewPassed",
  "decidedBy": "reviewer"
}
```

## UI Surface/Patch Events

### `agui.ui.surface`
Emitted when a new task UI surface is created.

### `agui.ui.patch`
Emitted to stream incremental UI state updates.

---

## Graph Events

These events allow UI consumers to reconstruct the full task hierarchy from the event stream alone.

### `agui.graph.link_created`
Emitted by `DispatcherActor` when a parent–child sub-task link is established.

| Field | Type | Description |
|---|---|---|
| `parentTaskId` | string | ID of the parent task |
| `childTaskId` | string | ID of the newly spawned sub-task |
| `depth` | int | Nesting depth (root = 0) |
| `title` | string | Title of the child task |

```json
{
  "parentTaskId": "task-abc",
  "childTaskId": "subtask-def",
  "depth": 1,
  "title": "Implement auth service"
}
```

### `agui.graph.child_completed`
Emitted by `DispatcherActor` when a child task coordinator terminates with `Done` status.

| Field | Type | Description |
|---|---|---|
| `parentTaskId` | string | ID of the parent task |
| `childTaskId` | string | ID of the completed child task |

```json
{
  "parentTaskId": "task-abc",
  "childTaskId": "subtask-def"
}
```

### `agui.graph.child_failed`
Emitted by `DispatcherActor` when a child task coordinator terminates with a non-`Done` status.

| Field | Type | Description |
|---|---|---|
| `parentTaskId` | string | ID of the parent task |
| `childTaskId` | string | ID of the failed child task |
| `error` | string | Failure reason |

```json
{
  "parentTaskId": "task-abc",
  "childTaskId": "subtask-def",
  "error": "Sub-task terminated without completing."
}
```

---

## Telemetry Events

These events carry operational health metrics for live monitoring.

### `agui.telemetry.quality`
Emitted by `TaskCoordinatorActor` when a role completes (from `OnRoleSucceeded`) or when a quality concern is received (from `OnQualityConcern`).

| Field | Type | Description |
|---|---|---|
| `role` | string | Role name in lowercase (e.g. `"planner"`) |
| `confidence` | double | Output confidence score [0.0–1.0] |
| `retryCount` | int | Current retry count for this task |
| `concern` | string? | Quality concern message (only present when emitted from `OnQualityConcern`) |

```json
{
  "role": "builder",
  "confidence": 0.87,
  "retryCount": 0
}
```

### `agui.telemetry.retry`
Emitted by `TaskCoordinatorActor` in `OnRetryRole` when a supervisor-initiated retry is executed.

| Field | Type | Description |
|---|---|---|
| `role` | string | Role being retried |
| `retryCount` | int | Updated retry count |
| `reason` | string | Reason for the retry |

```json
{
  "role": "builder",
  "retryCount": 1,
  "reason": "Low quality output detected"
}
```

### `agui.telemetry.consensus`
Emitted by `TaskCoordinatorActor` in `OnConsensusResult` when a consensus vote completes.

| Field | Type | Description |
|---|---|---|
| `approved` | bool | Whether consensus approved the artifact |
| `voteCount` | int | Total number of votes cast |
| `retryCount` | int | Current retry count for this task |

```json
{
  "approved": true,
  "voteCount": 3,
  "retryCount": 0
}
```

### `agui.telemetry.circuit`
Emitted by `TaskCoordinatorActor` in `OnGlobalBlackboardChanged` when an adapter circuit state change is detected.

| Field | Type | Description |
|---|---|---|
| `adapterCircuitKey` | string | Global blackboard key for the circuit |
| `state` | string | Circuit state value (e.g. `"open"`, `"closed"`) |
| `hasOpenCircuits` | bool | Whether any adapter circuit is currently open |

```json
{
  "adapterCircuitKey": "adapter_circuit_local-echo",
  "state": "open",
  "hasOpenCircuits": true
}
```

---

## Consuming Events

Events are available via two endpoints:

- **`GET /ag-ui/events`** — SSE stream of all live events (subscribes and replays recent history).
- **`GET /ag-ui/recent`** — Returns the last N events (default 50, max 200) as a JSON array.

### Reconstructing a Task Tree

To reconstruct the task hierarchy from the event stream:

1. Start with `agui.task.submitted` events to identify root tasks.
2. Use `agui.graph.link_created` events to build parent→child edges.
3. Use `agui.graph.child_completed` / `agui.graph.child_failed` to mark leaf terminal states.
4. Use `agui.task.done` / `agui.task.failed` to mark root terminal states.

### Tracking Operational Health

- Monitor `agui.telemetry.quality` for confidence trends per task/role.
- Monitor `agui.telemetry.retry` to detect tasks experiencing repeated failures.
- Monitor `agui.telemetry.consensus` to see approval rates across review sessions.
- Monitor `agui.telemetry.circuit` to detect adapter degradation.
