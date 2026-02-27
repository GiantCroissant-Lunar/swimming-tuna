# Dogfooding Run Log

- **Date:** 2026-02-27
- **Operator:** @apprenticegc
- **Run goal:** Implement RFC-014 remaining tasks (merge-back, lifecycle, endpoints, tests)
- **Runtime profile:** Dogfood
- **Adapter order:** pi → kilo → kimi → (empty) → (empty)
- **ArcadeDB enabled:** no
- **Langfuse tracing enabled:** no
- **Fault injection:** SimulateBuilderFailure=false, SimulateReviewerFailure=false

## Self-Improvement Run Metadata

- **Target component:** runtime
- **Phase under test:** RFC-014 (run orchestration & tree-structured git)
- **Expected output artifact:** RunCoordinatorActor lifecycle, MergeTaskBranchAsync, run endpoints
- **Eval criteria:** All tests pass (baseline 661), new RFC-014 tests, build succeeds
- **Baseline metrics:** 661 tests, build <10s

## Tasks Submitted

### Run 1 (run-750c0272, budget: 500K tokens)

| # | taskId | Title | Final state | Notes |
|---|--------|-------|-------------|-------|
| 1 | task-e01dc464 | MergeTaskBranchAsync + MergeResult | done | Completed via pipeline but no file changes in worktree |
| 2 | task-f8bcc431 | RunCoordinatorActor lifecycle | budget-exhausted | Builder blocked after budget hit |
| 3 | task-3e717c56 | Run endpoints | budget-exhausted | Builder blocked after budget hit |
| 4 | task-8aae7002 | RunCoordinatorActor unit tests | done | Completed via pipeline but no file changes in worktree |

### Run 2 (run-5ab1281d, budget: 2M tokens)

| # | taskId | Title | Final state | Notes |
|---|--------|-------|-------------|-------|
| 2 | task-5266ce6f | RunCoordinatorActor lifecycle (re-submit) | building (stuck) | Builder→orchestrator rework loop |
| 3 | task-7d1146bf | Run endpoints (re-submit) | building (stuck) | Builder→orchestrator rework loop |

## Observations

- **Budget exhaustion:** 500K tokens insufficient for 4 tasks via GLM-4.7 (pi/kilo adapters). Tasks 1+4 consumed the budget, leaving 2+3 blocked.
- **Empty worktrees:** All 4 completed tasks (done status) produced no file changes in their worktrees. The builder executes through the CLI adapter but changes don't materialize as file modifications.
- **Orchestrator rework loop:** After re-submitting with 2M budget, tasks entered a builder→orchestrator→rework cycle without converging. Stall detected after 50+ ticks.
- **Kimi adapter worked:** After pi/kilo exhausted, kimi CLI successfully executed builder and orchestrator roles.
- **Memvid error:** Non-blocking `MV999: Sketch track is invalid` errors during sibling context encoding.

## Gatekeeper Fixes

### Full implementation (all remaining RFC-014 tasks)

After swarm produced no usable output, gatekeeper implemented all 8 remaining design tasks:

1. **MergeResult enum + MergeTaskBranchAsync** — merge-back with conflict detection and branch cleanup
2. **RunCoordinatorActor lifecycle** — state machine (accepted→executing→ready-for-pr→done), merge gate via SemaphoreSlim, feature branch creation/push
3. **AG-UI events** — all 7 run lifecycle events (accepted, executing, task-merged, merge-conflict, ready-for-pr, done)
4. **RunConfigured + RunMarkDone messages** — branch configuration and done marker
5. **Extended POST /runs** — document, baseBranch, branchPrefix fields; featureBranch in response
6. **GET /runs list** + **POST /runs/{runId}/done** endpoints
7. **RunRegistry extensions** — ListRuns, MarkDone, UpdateFeatureBranch
8. **8 new tests** — MergeResult enum values, merge branch-not-found, RunRegistry create/list/done/featureBranch

## Retro

### What went well

- swarm-dev skill worked as expected — consistent workflow, clear steps
- Runtime started and accepted tasks without issues
- Pre-flight checks caught budget exhaustion before manual debugging

### What did not go well

- **0% usable swarm output** — all tasks produced no file changes despite "done" status
- **Workspace leak confirmed** — builder writes go somewhere but not to worktrees
- **Budget miscalculation** — 500K tokens only covers ~2 tasks with GLM-4.7
- **Orchestrator rework loop** — no convergence after multiple builder rounds
- **Adapter limit** — GLM-4.7 has 3-instance concurrency limit

### Action items

| Item | Owner | GitHub issue |
|------|-------|--------------|
| P0: Builder produces no file changes in worktree | investigate | — |
| P1: Orchestrator rework loop (no convergence) | investigate | — |
| P1: Default budget too low for GLM-4.7 (500K→2M) | config change | — |
| P2: Memvid MV999 sketch track error | investigate | — |
| Update swarm-dev skill with budget guidance | docs | — |
