# Dogfooding Run Playbook

This playbook defines the team protocol for running SwarmAssistant dogfooding cycles,
reviewing replay artifacts, and driving continuous self-improvement.

## Run Lifecycle

Each dogfooding cycle follows five ordered phases:

```
create run → submit tasks → monitor → replay → retro
```

### Phase 1 — Create Run

1. Agree on a **run goal** (e.g. "validate planner role on Phase 7 tasks").
2. Start the runtime in the target profile:
   ```bash
   DOTNET_ENVIRONMENT=Local dotnet run \
     --project project/dotnet/src/SwarmAssistant.Runtime \
     --no-launch-profile
   ```
   Or boot the full stack via Aspire:
   ```bash
   task run:aspire
   ```
3. Confirm the health check is green:
   ```bash
   curl http://127.0.0.1:5080/healthz
   ```
4. Record the **run metadata** (see [Required Run Metadata](#required-run-metadata)).

**Done criteria:** runtime is reachable, run metadata is written, run ID is set.

### Phase 2 — Submit Tasks

1. Submit one or more tasks via the A2A endpoint or AG-UI action:
   ```bash
   # A2A endpoint
   curl -X POST http://127.0.0.1:5080/a2a/tasks \
     -H 'Content-Type: application/json' \
     -d '{"title":"<task title>","description":"<task description>"}'

   # AG-UI action
   curl -X POST http://127.0.0.1:5080/ag-ui/actions \
     -H 'Content-Type: application/json' \
     -d '{"actionId":"submit_task","payload":{"title":"<task title>"}}'
   ```
2. Record each returned `taskId` in the run log.
3. Wait for the `agui.task.submitted` event on the SSE stream before submitting the
   next task if strict ordering is required.

**Done criteria:** all planned tasks submitted and their IDs recorded.

### Phase 3 — Monitor

1. Open the SSE stream to observe live events:
   ```bash
   curl -N http://127.0.0.1:5080/ag-ui/events
   ```
2. Watch for key lifecycle events:
   | Event | Expected action |
   |---|---|
   | `agui.task.submitted` | Confirm dispatch. |
   | `agui.task.transition` | Note role transitions (planner → builder → reviewer). |
   | `agui.task.decision` | Confirm reviewer verdict. |
   | `agui.task.retry` | Log retry reason. |
   | `agui.task.done` | Record summary output. |
   | `agui.task.failed` | Trigger escalation procedure (see [Escalation Handling](#escalation-handling)). |
3. Poll task snapshots periodically:
   ```bash
   curl http://127.0.0.1:5080/a2a/tasks
   ```
4. Note any stuck tasks (no transition for > `RoleExecutionTimeoutSeconds`).

**Done criteria:** all submitted tasks reach a terminal state (`done` or `failed`).

### Phase 4 — Replay

1. Fetch the full event log from ArcadeDB memory:
   ```bash
   curl http://127.0.0.1:5080/memory/tasks
   ```
2. For each task of interest, fetch the detailed snapshot:
   ```bash
   curl http://127.0.0.1:5080/memory/tasks/<taskId>
   ```
3. Review the role outputs in order:
   - `planningOutput` — planner role artifact.
   - `buildOutput` — builder role artifact.
   - `reviewOutput` — reviewer role artifact.
   - `summary` — final run summary.
4. Replay events through the AG-UI action loop to verify idempotency:
   ```bash
   curl -X POST http://127.0.0.1:5080/ag-ui/actions \
     -H 'Content-Type: application/json' \
     -d '{"actionId":"load_memory"}'
   ```
5. Use the Godot UI (`task run:aspire`) to inspect A2UI surface history.

**Done criteria:** all task snapshots retrieved and role outputs reviewed for correctness.

### Phase 5 — Retro

1. Fill in the **retro section** of the run log (see [Required Run Metadata](#required-run-metadata)).
2. Identify action items and open GitHub issues for each.
3. Update this playbook if the protocol needs adjustment.
4. Archive the run log in `docs/dogfooding/runs/` with the naming convention
   `<YYYY-MM-DD>-<short-slug>.md`.

**Done criteria:** retro written, action items filed, run log archived.

---

## Required Run Metadata

Create a run log file before starting Phase 1. Use this template:

```markdown
# Dogfooding Run Log

- **Date:** YYYY-MM-DD
- **Operator:** @<github-handle>
- **Run goal:** <one-line description of what is being validated>
- **Runtime profile:** Local | SecureLocal | CI
- **Adapter order:** copilot | cline | kimi | local-echo (list active order)
- **ArcadeDB enabled:** yes | no
- **Langfuse tracing enabled:** yes | no
- **Fault injection:** SimulateBuilderFailure=<true|false> SimulateReviewerFailure=<true|false>

## Tasks Submitted

| # | taskId | Title | Final state | Notes |
|---|--------|-------|-------------|-------|
| 1 | | | | |

## Observations

<free-form notes during monitoring phase>

## Replay Findings

<role output quality notes per task>

## Retro

### What went well

### What did not go well

### Action items

| Item | Owner | GitHub issue |
|------|-------|--------------|
| | | |
```

---

## Escalation Handling

When a task emits `agui.task.failed`, follow this ladder:

1. **Inspect error detail** — fetch the task snapshot and read the `error` field:
   ```bash
   curl http://127.0.0.1:5080/a2a/tasks/<taskId>
   ```
2. **Check adapter health** — confirm the active CLI adapter is reachable. Look for
   `CopilotCliAdapter`, `ClineCliAdapter`, or `LocalEchoAdapter` startup log lines.
3. **Retry via HITL** — if the failure is recoverable, send an approve action to
   unblock the reviewer:
   ```bash
   curl -X POST http://127.0.0.1:5080/ag-ui/actions \
     -H 'Content-Type: application/json' \
     -d '{"actionId":"approve_review","taskId":"<taskId>"}'
   ```
4. **Enable fault injection bypass** — if validating failure paths, set
   `SimulateBuilderFailure=false` and restart the runtime.
5. **Escalate to human** — if no automatic recovery is possible, open a GitHub issue
   with the run log and task snapshot attached. Assign label `area/runtime` and
   `priority/p1`.

| Condition | First responder action |
|---|---|
| All adapters returning error | Restart runtime; check CLI auth. |
| Task stuck in `building` > timeout | Kill task; reduce `RoleExecutionTimeoutSeconds`. |
| ArcadeDB write failure | Check `project/infra/arcadedb/` stack; restart container. |
| Langfuse OTLP rejected | Verify `LangfusePublicKey` / `LangfuseSecretKey`; disable tracing to unblock. |
| Repeated reviewer rejection loop | Enable `SimulateReviewerFailure=false`; inspect reviewer prompt. |

---

## Self-Improvement Run Metadata

Self-improvement runs are dogfooding cycles where the swarm processes tasks about
its own codebase. In addition to the base run metadata above, record:

- **Target component:** `runtime` | `cli` | `godot-ui` | `contracts` | `docs`
- **Phase under test:** Phase number (e.g. `Phase 7`)
- **Expected output artifact:** path of the file the run should produce or modify
- **Eval criteria:** measurable acceptance criteria (e.g. "all 286 tests pass after change")
- **Baseline metrics:** latency (seconds/task), token cost (if Langfuse enabled), retry count

At retro, compare actual metrics against baseline and record the delta.
