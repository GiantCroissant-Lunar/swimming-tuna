# RFC-014: Run Orchestration & Tree-Structured Git Workflow

**Status:** Draft
**Date:** 2026-02-27
**Dependencies:** RFC-013 (Agent Hierarchy Model), RFC-011 (API-Direct)

## Problem

Today, the gatekeeper (Claude Code / human) manually:
1. Reads a design doc (RFC, PRD)
2. Decomposes it into tasks by hand
3. Submits tasks via `POST /a2a/tasks`
4. Monitors completion
5. Reviews output in the main workspace
6. Merges and pushes

The runtime handles the middle (dispatch → execute), but the top (accept doc →
decompose → create run) and bottom (merge → PR) are manual. Additionally, the
git workflow is flat — all tasks branch from HEAD and there's no merge-back
mechanism. Concurrent tasks can pollute each other's workspace.

We need:
- A **run lifecycle** that accepts a design doc, decomposes it into tasks via
  LLM, and manages the full lifecycle
- A **tree-structured git workflow** where worktree branches mirror the agent
  hierarchy and merge back level by level
- A **PR-ready signal** so the gatekeeper creates the PR when ready

## Proposal

### 1. Entry Point — `POST /a2a/runs`

New API endpoint that accepts a design doc and initiates a run:

```json
POST /a2a/runs
{
  "title": "RFC-012 Pi Adapter",
  "document": "# RFC-012: Pi Adapter & Self-Extending...",
  "baseBranch": "main",
  "branchPrefix": "feat"
}
```

Response:

```json
{
  "runId": "run-abc123",
  "status": "accepted",
  "featureBranch": "feat/rfc-012-pi-adapter",
  "statusUrl": "/a2a/runs/run-abc123"
}
```

The `document` field is free-form — RFC, PRD, Notion export, plain text.
The runtime doesn't parse it; the Decomposer agent does.

Additional endpoints:

```
GET  /a2a/runs                          — list all runs
GET  /a2a/runs/{runId}                  — run details + task statuses
POST /a2a/runs/{runId}/done             — gatekeeper marks run as done (after PR)
```

### 2. Actor Hierarchy

```
SupervisorActor
 └── DispatcherActor
      └── RunCoordinatorActor (per run)             ← NEW
           ├── [Decomposer phase]                   ← NEW role
           │    LLM reads doc → outputs task list
           ├── TaskCoordinatorActor (task 1)
           │    └── worktree from feat/rfc-012
           ├── TaskCoordinatorActor (task 2)
           │    └── worktree from feat/rfc-012
           └── TaskCoordinatorActor (task 3)
                └── worktree from feat/rfc-012
```

`RunCoordinatorActor` is the parent of all `TaskCoordinatorActor`s in a run.
It owns the feature branch, the merge gate, and the run lifecycle state machine.

### 3. Run Lifecycle

```
accepted → decomposing → executing → merging → ready-for-pr → done
                                         ↑           │
                                         └───────────┘  (tasks still completing)
```

| State | What happens |
|-------|-------------|
| `accepted` | Run created, feature branch created from `baseBranch` |
| `decomposing` | Decomposer agent reading doc, producing task definitions |
| `executing` | Tasks dispatched to agents, running concurrently in worktrees |
| `merging` | Tasks completing, merging back to feature branch (serialized via gate) |
| `ready-for-pr` | All tasks done, feature branch pushed, `agui.run.ready-for-pr` emitted |
| `done` | Gatekeeper created PR or marked run as done via `POST /a2a/runs/{runId}/done` |

Note: `executing` and `merging` overlap — as each task completes it merges
immediately (serialized). The run transitions to `ready-for-pr` only when all
tasks are in a terminal state.

### 4. Decomposer Role

New `SwarmRole.Decomposer` — runs once per run at the start.

**Input:** The design doc (full text).

**Prompt template:** Instructs the LLM to output a JSON array of task definitions:

```json
[
  {
    "title": "Add Pi adapter definition",
    "description": "Add a new entry to AdapterDefinitions in SubscriptionCliRoleExecutor.cs...",
    "priority": 1
  },
  {
    "title": "Add provider fallback mapping",
    "description": "Add [\"pi\"] = \"pi-agent\" to AdapterProviderFallbacks...",
    "priority": 2
  }
]
```

The RunCoordinatorActor parses this output and creates `TaskCoordinatorActor`s
for each task. Tasks may include `priority` for ordering but execute concurrently
by default.

**Execution path:** Uses `AgentFrameworkRoleEngine` like other roles — can go
through API-direct (hybrid mode) or CLI adapter. The decomposer prompt is
a new template alongside existing planner/builder/reviewer templates.

### 5. Git Tree Lifecycle

```
main
 └── feat/rfc-012-pi-adapter                 ← L0: RunCoordinatorActor creates
      ├── swarm/task-001                     ← L1: builder worktree
      │    (merges back to feat/rfc-012)
      ├── swarm/task-002                     ← L1: builder worktree
      │    (merges back to feat/rfc-012)
      └── swarm/task-003                     ← L1: builder worktree
           (merges back to feat/rfc-012)
```

| Event | Git action |
|-------|-----------|
| Run accepted | `git checkout -b {prefix}/{slug} {baseBranch}` |
| Task starts building | `git worktree add .worktrees/swarm-{taskId} -b swarm/{taskId} {featureBranch}` |
| Task completes | Auto-commit in worktree, remove worktree dir, preserve branch |
| Task merges (serialized) | Acquire merge lock → `git merge swarm/{taskId} --no-ff` into feature branch → release lock |
| Merge conflict | Emit `agui.run.merge-conflict`, mark task as `needs-gatekeeper` |
| Successful merge | Delete `swarm/{taskId}` branch |
| All tasks done | `git push -u origin {featureBranch}`, emit `agui.run.ready-for-pr` |

### 6. WorkspaceBranchManager Changes

The existing `WorkspaceBranchManager` needs a `parentBranch` parameter:

```csharp
// Current: always forks from current HEAD
public async Task<string?> EnsureWorktreeAsync(string taskId)

// New: forks from specified parent branch
public async Task<string?> EnsureWorktreeAsync(string taskId, string? parentBranch = null)
```

When `parentBranch` is null (backward compat for standalone tasks), behavior is
unchanged — forks from HEAD. When set (run-managed tasks), forks from the
feature branch.

New method for merge-back:

```csharp
public async Task<MergeResult> MergeTaskBranchAsync(string taskId, string targetBranch)
{
    // 1. git checkout {targetBranch}
    // 2. git merge swarm/{taskId} --no-ff
    // 3. If conflict → return MergeResult.Conflict
    // 4. git branch -d swarm/{taskId}
    // 5. Return MergeResult.Success
}

public enum MergeResult { Success, Conflict, BranchNotFound }
```

### 7. Merge Gate

`RunCoordinatorActor` holds a `SemaphoreSlim(1, 1)` for serialized merges:

```csharp
private readonly SemaphoreSlim _mergeLock = new(1, 1);

private async Task OnTaskCompletedAsync(string taskId)
{
    await _mergeLock.WaitAsync();
    try
    {
        var result = await _branchManager.MergeTaskBranchAsync(taskId, _featureBranch);

        switch (result)
        {
            case MergeResult.Success:
                _completedTasks++;
                if (_completedTasks == _totalTasks)
                    await TransitionToReadyForPr();
                break;

            case MergeResult.Conflict:
                _uiEvents.Publish("agui.run.merge-conflict", taskId: taskId);
                // Task stays in needs-gatekeeper state
                break;
        }
    }
    finally
    {
        _mergeLock.Release();
    }
}
```

### 8. Backward Compatibility

- `POST /a2a/tasks` still works — submits a single task without a run. Uses
  current flat `swarm/{taskId}` branch behavior (no feature branch, no merge-back).
- `WorkspaceBranchManager.EnsureWorktreeAsync(taskId)` without `parentBranch`
  defaults to current HEAD.
- Existing `TaskCoordinatorActor` behavior is unchanged when not part of a run.

### 9. Events

New AG-UI events emitted by `RunCoordinatorActor`:

| Event | When | Payload |
|-------|------|---------|
| `agui.run.accepted` | Run created | `runId`, `featureBranch`, `title` |
| `agui.run.decomposing` | Decomposer started | `runId` |
| `agui.run.executing` | Tasks dispatched | `runId`, `taskCount` |
| `agui.run.task-merged` | Task merged to feature branch | `runId`, `taskId` |
| `agui.run.merge-conflict` | Merge failed | `runId`, `taskId`, `conflictDetails` |
| `agui.run.ready-for-pr` | All tasks done, branch pushed | `runId`, `featureBranch` |
| `agui.run.done` | Gatekeeper marked done | `runId` |

## Implementation Tasks

1. `RunSpan` and `RunSpanStatus` types (shared with RFC-013)
2. `SwarmRole.Decomposer` — new role enum value
3. Decomposer prompt template
4. `RunCoordinatorActor` — run lifecycle state machine, merge gate
5. `POST /a2a/runs` endpoint + `GET` endpoints
6. `DispatcherActor` changes — create `RunCoordinatorActor` on run submission
7. `WorkspaceBranchManager.EnsureWorktreeAsync(taskId, parentBranch)` overload
8. `WorkspaceBranchManager.MergeTaskBranchAsync(taskId, targetBranch)` method
9. Feature branch creation/push logic in `RunCoordinatorActor`
10. AG-UI events for run lifecycle
11. `TaskCoordinatorActor` changes — notify parent `RunCoordinatorActor` on completion
12. Unit tests: RunCoordinatorActor lifecycle, merge gate serialization, conflict handling
13. Unit tests: WorkspaceBranchManager merge-back
14. Integration test: end-to-end run with 2 tasks, verify feature branch, verify merge order
15. Dogfood: submit RFC-013 as a run, verify decomposition and merge flow

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Dedicated Decomposer role | Not reusing Planner | Decomposition is a distinct concern — different prompt, different output format |
| Sequential merge gate | Not immediate or collect-then-merge | Mirrors real team behavior — concurrent work, serialized integration |
| PR-ready signal | Not automatic PR creation | Gatekeeper stays in control; handles partial success gracefully |
| RunCoordinatorActor as parent of TaskCoordinators | Not peer actor | Natural ownership — run owns its tasks, feature branch, merge gate |
| `parentBranch` parameter | Not always fork from HEAD | Enables tree-structured branching; backward compat when null |

## Out of Scope

- Automatic PR creation (gatekeeper decides)
- Task dependency ordering (all tasks concurrent by default)
- Cross-run branch management (each run is independent)
- Partial run completion / cherry-pick (gatekeeper handles manually)
- Decomposer retry / human-in-the-loop editing of task list

## Open Questions

- Should the decomposer output include task dependencies (DAG) or just a flat list?
- How to handle runs where some tasks fail — does the feature branch still get pushed?
- Should `POST /a2a/runs` accept a `taskCount` hint so the gatekeeper can validate
  the decomposer's output before execution begins?
- Branch slug generation — sanitize from title, or user-provided?
