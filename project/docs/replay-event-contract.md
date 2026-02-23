# Replay Event Contract — `TaskExecutionEvent`

- **Status:** Accepted
- **Date:** 2026-02-23
- **Area:** contracts
- **Phase:** 0
- **Issue:** [Phase 0 — Issue 02](https://github.com/GiantCroissant-Lunar/swimming-tuna/issues/75)
- **Depends on:** [ADR-0001 — Run Domain Model](./adr-0001-run-domain-model.md)

---

## Purpose

This document defines the canonical shape, ordering semantics, idempotency rules, and
correlation requirements for **`TaskExecutionEvent`** — the durable event record that
enables deterministic task and run playback in SwarmAssistant.

Every state transition that occurs within the runtime (role executions, retries,
escalations, sub-task lifecycle, and run boundaries) **must** be emitted as a
`TaskExecutionEvent` before it is considered observable or replayable.

---

## 1 — `TaskExecutionEvent` Schema

### 1.1 Required Fields

| Field | Type | Constraints | Description |
|---|---|---|---|
| `eventId` | `string` | non-empty, globally unique (ULID recommended) | Stable, immutable identifier for this event. |
| `eventType` | `EventType` | see §2 | Discriminator that identifies the kind of transition this event records. |
| `runId` | `string` | non-empty | Identifier of the enclosing Run (see ADR-0001). |
| `taskId` | `string` | non-empty | Identifier of the task this event belongs to. |
| `runSeq` | `int` | ≥ 1, monotonically increasing within a `runId` | Global ordering counter scoped to a Run. Assigned by the event emitter at emission time. |
| `taskSeq` | `int` | ≥ 1, monotonically increasing within a `taskId` | Local ordering counter scoped to a single task. Assigned by the task coordinator at emission time. |
| `occurredAt` | `DateTimeOffset` | UTC | Wall-clock timestamp at which the event was generated. Used for display and diagnostics only; **must not** be used as the primary sort key for replay ordering. |
| `traceId` | `string` | non-empty; W3C Trace Context format (`hex32`) | OpenTelemetry trace ID of the span active at event emission. |
| `spanId` | `string` | non-empty; W3C Trace Context format (`hex16`) | OpenTelemetry span ID of the span active at event emission. |
| `adapterId` | `string` | non-empty | Logical identifier of the CLI adapter that produced or triggered this event (e.g. `"copilot"`, `"cline"`, `"kimi"`, `"local-echo"`). Use `"runtime"` for events emitted directly by the runtime without a CLI adapter. |
| `actor` | `string` | non-empty | Akka actor path of the component that emitted this event (e.g. `/user/coordinator/task-abc123`). |

### 1.2 Optional Fields

| Field | Type | Constraints | Description |
|---|---|---|---|
| `parentTaskId` | `string` | nullable | Populated for sub-task events; identifies the direct parent task. |
| `role` | `SwarmRole` | nullable; see §2.2 | Present on all role-scoped event types. |
| `previousStatus` | `TaskStatus` | nullable | Status before this transition (present on `task.status_changed`). |
| `nextStatus` | `TaskStatus` | nullable | Status after this transition (present on `task.status_changed`). |
| `retryCount` | `int` | ≥ 0; nullable | Number of retry attempts for this role at the time of emission. |
| `escalationLevel` | `int` | ≥ 1; nullable | Escalation depth at the time of the `task.escalated` event. |
| `confidence` | `double` | [0.0, 1.0]; nullable | Agent quality confidence score, present on role completion events. |
| `error` | `string` | nullable | Human-readable error message for failure events. |
| `output` | `string` | nullable | Truncated role output (max 4 096 characters). Full output is stored separately. |
| `metadata` | `Map<string, string>` | nullable; keys non-empty | Open-ended key/value annotations for future extensibility. |

---

## 2 — `EventType` Enum

### 2.1 Run-Scoped Events

| Value | Emitter | Description |
|---|---|---|
| `run.started` | `CoordinatorActor` | A new Run has been created and is accepting tasks. |
| `run.completed` | `CoordinatorActor` | All root tasks in the Run reached `Done`. Terminal. |
| `run.failed` | `CoordinatorActor` | At least one root task reached `Blocked` with no pending retries. Terminal. |
| `run.cancelled` | `CoordinatorActor` | The Run was explicitly cancelled. Terminal. |

### 2.2 Task-Scoped Events

| Value | Emitter | Description |
|---|---|---|
| `task.submitted` | `DispatcherActor` | A task has been accepted by the runtime. |
| `task.status_changed` | `TaskCoordinatorActor` | A task's `TaskStatus` has transitioned. Includes `previousStatus` and `nextStatus`. |
| `task.completed` | `TaskCoordinatorActor` | Task reached `Done`. Terminal for this task. |
| `task.failed` | `TaskCoordinatorActor` | Task reached `Blocked` after all retry attempts are exhausted. Terminal for this task. |
| `task.escalated` | `SupervisorActor` | Task was escalated to a higher supervision level. Includes `escalationLevel`. |

### 2.3 Role-Scoped Events

| Value | Emitter | `role` field | Description |
|---|---|---|---|
| `role.started` | `WorkerActor` / `ReviewerActor` | required | Execution of a role pipeline step has begun. |
| `role.completed` | `WorkerActor` / `ReviewerActor` | required | Role step completed successfully. Includes `confidence` and `output`. |
| `role.failed` | `WorkerActor` / `ReviewerActor` | required | Role step failed. Includes `error`. |
| `role.retried` | `SupervisorActor` | required | Role step is being retried. Includes `retryCount` and `error` from the previous attempt. |

### 2.4 Sub-Task Events

| Value | Emitter | Description |
|---|---|---|
| `subtask.spawned` | `TaskCoordinatorActor` | A child task has been spawned. Includes `parentTaskId`. |
| `subtask.completed` | `TaskCoordinatorActor` | A child task completed. Includes `parentTaskId`. |
| `subtask.failed` | `TaskCoordinatorActor` | A child task failed. Includes `parentTaskId` and `error`. |

### 2.5 `SwarmRole` Values (reference)

Defined in `SwarmAssistant.Contracts.Messaging.SwarmRole`:

| Value | Description |
|---|---|
| `Planner` | Decomposes goals into an ordered task plan. |
| `Builder` | Implements the plan output of the Planner. |
| `Reviewer` | Evaluates Builder output and approves or requests changes. |
| `Orchestrator` | Coordinates multi-agent decisions. |
| `Researcher` | Gathers background context for a task. |
| `Debugger` | Diagnoses and resolves task failures. |
| `Tester` | Validates built artefacts against acceptance criteria. |

### 2.6 `TaskStatus` Values (reference)

Defined in `SwarmAssistant.Contracts.Tasks.TaskStatus`:

| Value | Meaning |
|---|---|
| `Queued` | Task created; not yet assigned to any role. |
| `Planning` | Planner role is active. |
| `Building` | Builder role is active. |
| `Reviewing` | Reviewer role is active. |
| `Done` | All roles completed successfully. Terminal. |
| `Blocked` | Task is stuck and cannot progress without intervention. Terminal. |

---

## 3 — Ordering Rules

### 3.1 Canonical Replay Order

Events **must** be replayed in the following sort order (primary → secondary → tertiary):

1. **`runSeq` ascending** — global ordering within a Run.
2. **`taskSeq` ascending** — secondary tie-break within a task (when multiple tasks share
   the same `runSeq` due to parallel emission, consumers sort by `runSeq` first, then by
   `taskId` consistently, then by `taskSeq`).
3. **`occurredAt` ascending** — informational tie-break only; **must not** be the sole
   ordering key because clock skew across actor threads makes wall-clock ordering
   non-deterministic.

### 3.2 `runSeq` Assignment Rules

- `runSeq` is a **monotonically increasing** integer, starting at `1`, scoped to a
  single `runId`.
- The component responsible for managing `runSeq` is the **`CoordinatorActor`** for
  that Run.
- Incrementing `runSeq` is an **atomic** operation; two events within the same Run
  **must not** share the same `runSeq`.
- When a Run is replayed, `runSeq` values **must** be preserved exactly as originally
  emitted. Re-numbering during replay is not permitted.

### 3.3 `taskSeq` Assignment Rules

- `taskSeq` is a **monotonically increasing** integer, starting at `1`, scoped to a
  single `taskId`.
- The component responsible for managing `taskSeq` is the **`TaskCoordinatorActor`**
  for that task.
- Sub-task events (those with a non-null `parentTaskId`) use the **child task's** own
  `taskSeq` counter, not the parent's.
- `taskSeq` values **must** be preserved exactly during replay.

### 3.4 Timestamp Usage Policy

| Use case | Allowed sort key |
|---|---|
| Deterministic replay | `runSeq`, then `taskSeq` |
| UI display / diagnostics | `occurredAt` |
| Deduplication | `eventId` (exact match) |
| Analytics (duration, latency) | `occurredAt` difference between paired events |

---

## 4 — Idempotency and Duplicate-Handling

### 4.1 Idempotency Key

`eventId` is the idempotency key. Two events with the same `eventId` are considered
identical regardless of all other fields.

### 4.2 Ingestion Rules

Consumers (replay engines, persistence writers, analytics pipelines) **must** apply
the following rules:

1. **Exact-once delivery by `eventId`** — before persisting or processing an event,
   check whether `eventId` already exists in the store. If it does, silently discard
   the duplicate without returning an error.
2. **No state mutation on duplicate** — a duplicate event **must not** advance sequence
   counters, trigger side effects, or update any derived state.
3. **No ordering violation on duplicate** — a late-arriving duplicate whose `runSeq`
   is lower than the current high-water mark **must** be discarded, not inserted
   retroactively into the replay log.
4. **Idempotent acknowledgement** — the ingestion endpoint **must** return the same
   success response for a duplicate as it would for a first-time event, so that
   producers can safely retry without special-casing.

### 4.3 Producer Guarantees

Producers (runtime actors) **must**:

- Generate a new, unique `eventId` (ULID) for every event at the point of emission.
- Never reuse an `eventId` across retries of the same logical transition (each retry
  attempt produces a **new** event with a new `eventId` and incremented `retryCount`).
- Emit exactly one `role.retried` event per retry attempt before re-emitting
  `role.started`.

---

## 5 — Event Rules by Category

### 5.1 Retry Events

When a role step fails and the `SupervisorActor` schedules a retry:

1. A `role.failed` event is emitted for the failed attempt (includes `error` and
   current `retryCount`).
2. A `role.retried` event is emitted with the **same** `role`, the incremented
   `retryCount`, and the error from step 1.
3. A new `role.started` event is emitted to mark the beginning of the retry attempt.

Retry events **must not** reset `taskSeq`. The sequence continues monotonically.

Maximum retry depth is governed by `RuntimeOptions.MaxRoleRetries`. When that limit is
reached and the role still fails, the sequence ends with `task.failed`.

### 5.2 Escalation Events

When a task is escalated to a higher supervision level:

1. A `task.escalated` event is emitted with `escalationLevel` set to the new level
   (starting at `1` for the first escalation).
2. The `actor` field identifies the `SupervisorActor` that raised the escalation.
3. Subsequent retries after an escalation follow the retry rules in §5.1 and continue
   the existing `taskSeq` counter.
4. If escalation ultimately leads to a terminal failure, `task.failed` is the final
   event for that task.

### 5.3 Sub-Task Events

Sub-tasks are spawned when a `TaskCoordinatorActor` decomposes work during the
Planning role:

1. A `subtask.spawned` event is emitted on the **parent** task's `taskSeq` and carries
   both `taskId` (parent) and `parentTaskId` (also parent, for consistency) plus the
   child's ID in `metadata["childTaskId"]`.
2. All subsequent events for the child task use the **child's** `taskId` and its own
   `taskSeq`, but share the parent's `runId` and `runSeq` counter.
3. When the child task reaches `Done`, a `subtask.completed` event is emitted on the
   **parent** task's sequence (incrementing the parent's `taskSeq`).
4. When the child task reaches `Blocked`, a `subtask.failed` event is emitted on the
   parent task's sequence with `error` populated.
5. Sub-task nesting is unbounded in the schema but is constrained at runtime by
   `RuntimeOptions.MaxSubTaskDepth`.

### 5.4 Run Boundary Events

- `run.started` is the first event with `runSeq = 1` for any given `runId`.
- `run.completed`, `run.failed`, and `run.cancelled` are terminal; no further events
  for the same `runId` are valid after one of these is emitted.
- These events carry `taskId = ""` (empty string) because they are run-scoped, not
  task-scoped.

---

## 6 — Correlation Fields Reference

All four fields below are **required** on every `TaskExecutionEvent`. A missing or
empty value is a schema violation.

| Field | Source | Format | Purpose |
|---|---|---|---|
| `traceId` | OpenTelemetry `Activity.TraceId` | 32-char lowercase hex | Correlates all events emitted within the same OTel trace. Maps to Langfuse trace. |
| `spanId` | OpenTelemetry `Activity.SpanId` | 16-char lowercase hex | Identifies the specific span active at emission. Enables parent–child span reconstruction. |
| `adapterId` | `RoleTaskSucceeded.AdapterId` / executor context | logical adapter name | Identifies which CLI adapter produced the role output that triggered this event. Use `"runtime"` for system-generated events with no adapter involvement. |
| `actor` | Akka `Self.Path.ToString()` | Akka actor path | Identifies the actor that emitted the event. Enables per-actor event filtering in replay tooling. |

---

## 7 — Schema Versioning

- The current schema version is **`1.0`**.
- The `metadata` field **may** carry a `"schema.version"` key for forward-compatibility
  detection.
- Additive changes (new optional fields, new `EventType` values) are backward-compatible
  and do not require a version bump.
- Breaking changes (removing fields, changing field types, removing `EventType` values)
  require a new major version and a migration note in this document.

---

## Consequences

- `SwarmAssistant.Contracts` will gain a `TaskExecutionEvent` record and `EventType`
  enum in a follow-up implementation issue.
- The `TaskCoordinatorActor` and `SupervisorActor` will be updated to emit
  `TaskExecutionEvent` instances in addition to their existing AG-UI envelopes.
- A replay reader interface (`IReplayEventReader`) and writer interface
  (`IReplayEventWriter`) will be defined in a follow-up issue.
- ArcadeDB persistence for replay events is tracked in a separate infrastructure issue.
