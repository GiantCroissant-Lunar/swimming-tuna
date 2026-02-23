# ADR-0001: Run Domain Model and Terminology (Run vs Session)

- **Status:** Accepted
- **Date:** 2026-02-23
- **Area:** contracts
- **Phase:** 0
- **Issue:** [Phase 0][Issue 01]

---

## Context

The SwarmAssistant runtime tracks work through `taskId` and a parent–child sub-task
hierarchy. There is currently no durable boundary that groups a set of related tasks
into a single cohesive lifecycle. This absence blocks:

- **Replay and dogfooding analytics** — replaying a full work session requires
  reconstructing which tasks belonged together from event-stream heuristics rather
  than an explicit grouping key.
- **Multi-task lifecycle tracking** — status roll-ups (how many tasks in a "run"
  succeeded vs. failed) require ad-hoc queries with no canonical identifier.
- **Observability correlation** — without a run boundary, OpenTelemetry traces cannot
  be automatically correlated across tasks that share a logical invocation.

The word "session" has crept into several places in the codebase (Langfuse OTLP
session references, adapter hook filenames) with no agreed definition. Leaving it
undefined introduces ambiguity in analytics dashboards and in future cross-agent
protocol work.

---

## Decision

### 1 — Introduce `Run` as a First-Class Domain Concept

A **Run** is a durable, uniquely identified grouping of one or more SwarmAssistant
tasks that share a common origin and lifecycle boundary.

A Run is the unit of:

- **Replay** — all tasks associated with a `runId` can be restored together.
- **Analytics** — status roll-ups, timing, and quality metrics are aggregated
  per Run.
- **Correlation** — every OpenTelemetry trace and Langfuse session scopes to the
  Run that produced it.

### 2 — Required Run Fields

| Field | Type | Constraints | Description |
|---|---|---|---|
| `runId` | `string` | non-empty, globally unique (ULID recommended) | Stable identifier for this Run. |
| `createdAt` | `DateTimeOffset` | UTC, immutable | Timestamp at which the Run was created. |
| `status` | `RunStatus` | see below | Current lifecycle state of the Run. |
| `source` | `string` | non-empty | Logical origin of the Run (e.g. `"a2a-api"`, `"ag-ui-action"`, `"memory-bootstrap"`, `"cli"`). |
| `metadata` | `Dictionary<string, string>` | nullable; keys non-empty | Optional open-ended key/value annotations (e.g. `"requestedBy"`, `"environment"`, `"correlationId"`). |

#### `RunStatus` values

| Value | Meaning |
|---|---|
| `Pending` | Run created but no tasks have started yet. |
| `Running` | At least one task is active. |
| `Completed` | All tasks in the Run reached `Done` status. |
| `Failed` | At least one task reached `Blocked`/failed status and no retry is pending. |
| `Cancelled` | Run was explicitly cancelled before completion. |

Status transitions allowed:

```
Pending → Running
Running → Completed
Running → Failed
Running → Cancelled
Pending → Cancelled
```

Completed, Failed, and Cancelled are terminal; no further transitions are allowed.

### 3 — Run–Task Relationship Rules

1. **One-to-many:** A Run contains one or more top-level tasks. Sub-tasks inherit
   their parent's `runId` automatically.
2. **Immutable assignment:** A task's `runId` is set at submission time and **must
   not** change for the lifetime of the task.
3. **Single membership:** A task belongs to **at most one** Run. Many-to-many
   membership is not permitted.
4. **Root scope only:** Only top-level tasks are directly associated with a Run;
   sub-tasks are implicitly in scope via the root task's `runId`.
5. **Run completion rule:** A Run transitions to `Completed` only when **all** of
   its root tasks have reached `Done`. A Run transitions to `Failed` when any root
   task reaches `Blocked`/failed and the Run has no pending retries.

### 4 — Terminology: `session` is Telemetry-Only

The word **session** is reserved exclusively for telemetry and observability contexts:

- OpenTelemetry resource attribute `session.id` (if applicable to the exporter).
- Langfuse session correlation (maps 1:1 to `runId`).
- Adapter hook filenames (`session_start.py`, `session_end.py`) remain unchanged
  but are understood as process-scoped lifecycle hooks, not a domain concept.

`session` **must not** appear as a domain field name or a public API parameter in
runtime code, contracts, or documentation outside of the contexts listed above.

---

## Migration Strategy

Existing tasks stored in ArcadeDB or the in-memory `TaskRegistry` that were created
before this ADR was adopted **will not** carry a `runId`.

The following rules apply for backward compatibility:

1. **Ungrouped tasks** — any task without a `runId` is treated as belonging to an
   implicit _ungrouped_ run. These tasks are queryable and observable but are not
   aggregated into a named Run.
2. **No back-fill required** — retroactive `runId` assignment is out of scope for
   this ADR. Existing task snapshots in ArcadeDB are left as-is.
3. **Null-safe consumers** — all runtime code and API consumers **must** treat a
   missing or null `runId` on a task as valid. Validation failures on `runId`
   absence are not acceptable.
4. **Future tasks** — once the `Run` runtime implementation lands (tracked separately),
   all new task submissions **must** carry a `runId`. The submission API will accept
   an optional caller-supplied `runId`; if absent, the runtime will generate one.

---

## Rejected Alternatives

### A — Reuse `session` as the Run identifier

**Rejected.** `session` is already used with process-scoped semantics in adapter
hooks and is a reserved term in several observability SDKs (OpenTelemetry,
Langfuse). Reusing it as a domain concept would create naming conflicts and make
correlation logic harder to reason about.

### B — Derive run grouping purely from the task hierarchy

**Rejected.** The parent–child sub-task graph is already used for execution
decomposition. Using it to infer run grouping would conflate two orthogonal
concerns: task decomposition strategy and lifecycle grouping. A run may span tasks
that are logically related but structurally independent (e.g. a planning task and
a follow-up build task submitted separately).

### C — Make `runId` mandatory immediately with hard validation

**Rejected.** Requiring `runId` immediately would break the existing in-memory
registry, ArcadeDB-persisted snapshots, and the A2A/AG-UI submission paths before
the runtime implementation is merged. Null-safe backward compatibility is preferred
until a follow-up implementation issue is resolved.

### D — Call the concept `Workflow` instead of `Run`

**Rejected.** `Workflow` conflicts with `Microsoft.Agents.AI.Workflows` (Agent
Framework) already in use and implies an ordered, statically defined sequence.
`Run` is shorter, unambiguous in this context, and consistent with conventions in
related systems (GitHub Actions, Buildkite, Temporal).

---

## Consequences

- All follow-up implementation issues that introduce `runId` to the runtime,
  contracts, or API must reference this ADR.
- `SwarmTask` in `SwarmAssistant.Contracts` will gain an optional `RunId` field in a
  follow-up contracts issue.
- The AG-UI event envelope will gain an optional `runId` field in a follow-up
  contracts issue (minor version bump, backward-compatible).
- Langfuse session correlation will be mapped to `runId` in a follow-up
  observability issue.
