# Session Handover: Worktree Isolation + Swarm Evaluation → Next

**Date:** 2026-02-26
**From session:** PR #151 merge, swarm improvements, RFC-006 dogfooding, worktree isolation
**Branch:** `feat/rfc-006-langfuse-replay` (PR #155 open, review comments pending)
**Tests:** 583 passing (baseline was 578)

## What This Session Delivered

### Merged PR #151 (RFC-009 agent skills + Verify GOAP step)

Squash-merged to main. 578 tests. Addressed 6 of 7 remaining coderabbit comments before merge (skipped test naming convention — project-wide concern, 413+ methods).

### Merged PR #152 (swarm improvements)

Two quick wins before dogfooding RFC-006:

- Enabled `VerifySolutionPath` in `appsettings.Local.json`
- Added Code Quality section to builder prompt in `RolePromptFactory.cs` (trailing whitespace, EOF newlines)

### RFC-006 Dogfooding Run (6 tasks)

Submitted 6 Langfuse integration tasks to the swarm. All 6 completed. Gatekeeper review found significant issues (see Agent Swarm Issues below). Swarm output stashed: `git stash list` → `stash@{0}: On main: RFC-006 swarm output + gatekeeper fixes`.

### PR #155: Per-task git worktree isolation

Implemented worktree isolation so concurrent builders don't share workspace. 11 files, 258 insertions, 5 new tests.

## PR #155 Review Comments (6 from coderabbit)

### 1. WorkspaceBranchManager.cs:201 — Terminate timed-out git processes (Major)

`RunGitAsync` missing timeout handling. If `process.WaitForExitAsync` times out, the process isn't killed.

**Fix:** Add try/catch around `WaitForExitAsync`, kill process on timeout (similar to `GitArtifactCollector.RunGitAsync` pattern).

### 2. TaskCoordinatorActor.cs:1114 — Reuse worktree on builder retries (Major)

When `"Build"` is redispatched (retry path), the code tries to create a new worktree which will fail because the branch already exists. Should reuse existing worktree.

**Fix:** Check if `_worktreePath` is already set before calling `EnsureWorktreeAsync`. Only create on first Build dispatch.

### 3. WorkspaceBranchManager.cs:100 — Check git exit codes before logging success (Major)

`RemoveWorktreeAsync` logs successful removal/deletion even when git commands fail.

**Fix:** Check exit code from `RunGitAsync` before logging success messages.

### 4. SubscriptionCliRoleExecutor.cs:142 — Treat empty/whitespace workspace as "not provided" (Minor)

`command.WorkspacePath ?? _workspacePath` doesn't handle empty/whitespace strings.

**Fix:** Use `string.IsNullOrWhiteSpace(command.WorkspacePath) ? _workspacePath : command.WorkspacePath`.

### 5. SubscriptionCliRoleExecutorTests.cs:88 — Test doesn't validate workspace propagation (Minor)

`local-echo` adapter is internal and bypasses process launch, so the test doesn't actually verify `WorkingDirectory` is set.

**Fix:** Consider testing via a mock adapter or verifying the template variable dictionary directly. Alternatively, accept as smoke test documenting the API surface.

### 6. WorkspaceBranchManager.cs:201 — Duplicate of #1 (high-priority marker)

Same as comment #1 — terminate timed-out git processes.

## Agent Swarm Issues Observed During RFC-006 Dogfooding

### Compilation failures (P0 — swarm produces uncompilable code)

| Issue | Example | Root Cause |
|-------|---------|------------|
| Hallucinated enum values | `SwarmRole.Verifier` (doesn't exist) | Builder LLM invents API surface |
| Missing interface implementations | `MockLangfuseApiClient` missing `PostScoreAsync` | Builder doesn't check full interface |
| Expression tree limitation (CS0854) | Added optional params to `DispatcherActor` constructor | ~50 cascading errors in Akka `Props.Create(() => ...)` calls |
| Wrong argument types | `CancellationToken` where `string` expected | Builder doesn't verify method signatures |

### FluentAssertions API hallucination (P0 — 14 errors)

Builder used `.WithParameterName()` and `.ContainKey()` which don't exist in the project's FluentAssertions version. Had to replace all with xUnit `Assert.Throws`/`Assert.True` patterns.

### Verify step ineffective due to shared workspace (P1 — now fixed)

All 6 concurrent tasks shared the same workspace. `dotnet build` ran against interleaved changes from all tasks, making build verification meaningless. **Fixed by worktree isolation (PR #155).**

### Duplicate test directories

Swarm created both root-level test files and a `Langfuse/` subdirectory with duplicate test files having wrong constructor signatures. Had to delete the duplicates.

### No self-correction capability

The swarm completed all 6 tasks with "success" status, but gatekeeper found every task had issues. The Verify step couldn't catch errors because of shared workspace. Even with worktree isolation, the swarm lacks:

- **Cross-task awareness** — builder doesn't know what other tasks are modifying
- **API verification** — no mechanism to verify that used APIs actually exist
- **Dependency awareness** — adding params to shared types breaks downstream consumers
- **Test framework awareness** — doesn't know which FluentAssertions version/methods are available

## Agent Swarm Evaluation: Gaps After RFC-001 through RFC-009

Half the RFCs are implemented. The swarm can execute tasks through the GOAP pipeline, but agents don't truly collaborate. Current state:

### What works

- GOAP lifecycle: Plan → Build → Verify → Review → Finalize (with Rework loops)
- Role dispatch through CLI adapters (copilot, cline, kimi, local-echo)
- Agent registry with heartbeat and capability advertisement (RFC-004)
- Peer message routing contracts (RFC-005)
- Skill matching and injection into prompts (RFC-009)
- Build + test verification gate (RFC-009 Verify step)
- Worktree isolation per task (PR #155, pending merge)

### What's missing for true agent collaboration

| Gap | Impact | Potential Fix |
|-----|--------|---------------|
| **No shared context between tasks** | Builder A doesn't know Builder B modified the same file | Blackboard signals for file-level locks or change notifications |
| **No API/type awareness** | Builders hallucinate APIs, create breaking changes | Code index integration (RFC-006 CodeIndex), or pre-dispatch static analysis |
| **No dependency graph** | Adding params to shared types causes cascading failures | Task dependency declarations, topological execution ordering |
| **Reviewer doesn't catch compilation errors** | Reviewer is LLM-based, doesn't run build | Verify step now handles this, but needs worktree isolation working |
| **No learning from failures** | Same hallucination patterns repeat across runs | RFC-006 Langfuse feedback loop (stashed), outcome tracking |
| **Agents can't request help** | Builder stuck on compilation error has no escalation path | Peer-to-peer messaging (RFC-005 contracts exist, not wired) |
| **No incremental verification** | Verify runs full build+test, slow for large solutions | Incremental build, affected-test-only runs |

### Recommended priorities before RFC-006

1. **Merge PR #155** (worktree isolation) — unblocks reliable Verify step
2. **Evaluate worktree isolation** — re-run RFC-006 dogfooding with isolation enabled, compare failure rate
3. **Consider code-aware builder prompts** — inject project type info, available APIs, existing method signatures into builder context
4. **Consider pre-dispatch validation** — lightweight static check before sending to builder (e.g., "these types exist, these methods have these signatures")

## Known Issues (updated)

### P0 — RFC-001 `/a2a/tasks` per-agent endpoint (unchanged)

Enqueues but nothing consumes outside tests.

### P1 — AG-UI contract drift (unchanged)

Added `"verifying"` in RFC-009. Other undeclared events remain.

### P1 — review-resolve `max_patch_size:1024` (unchanged)

`gh-aw` framework silently drops large patches. Not configurable.

### P2 — RFC-003 host allowlist incomplete (unchanged)

Linux/container host filtering not fully enforced.

### P2 — Swarm pre-commit hook awareness (partially addressed)

Builder prompt now includes Code Quality section (PR #152). Not yet verified if swarm output is clean.

## Next Session Pre-flight

1. Address 6 PR #155 review comments (list above — 3 major, 3 minor)
2. Merge PR #155 after review approval
3. Verify 583+ tests pass on main after merge
4. Re-evaluate: dogfood RFC-006 again with worktree isolation, or address swarm gaps first
5. Apply stashed RFC-006 changes if dogfooding passes, or iterate on swarm quality

## Implementation Order

RFC-006 → RFC-010 → RFC-007 → RFC-008

(May need to interleave swarm infrastructure improvements based on dogfooding results)

## Lessons from This Session

1. **Verify step is useless without worktree isolation** — concurrent tasks sharing workspace means build verification tests against mixed state
2. **34% → ~100% gatekeeper intervention** — every RFC-006 task needed manual fixes, worse than RFC-009 (34% failure rate)
3. **FluentAssertions hallucination is a new pattern** — builder doesn't know available API surface
4. **Swarm "success" ≠ actual success** — all 6 tasks reported success, all 6 had compilation errors
5. **Builder prompt improvements help** — trailing whitespace eliminated (0 vs 5 files from RFC-009), but new failure patterns emerged
6. **Half the RFCs done, agents still don't cowork** — infrastructure is solid, but agents lack shared context, API awareness, and cross-task coordination

## Files Quick Reference

| Purpose | Path |
|---------|------|
| Worktree isolation PR | PR #155 (`feat/rfc-006-langfuse-replay`) |
| WorkspaceBranchManager | `project/dotnet/src/SwarmAssistant.Runtime/Execution/WorkspaceBranchManager.cs` |
| TaskCoordinatorActor | `project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs` |
| BuildVerifier | `project/dotnet/src/SwarmAssistant.Runtime/Execution/BuildVerifier.cs` |
| SubscriptionCliRoleExecutor | `project/dotnet/src/SwarmAssistant.Runtime/Execution/SubscriptionCliRoleExecutor.cs` |
| GitArtifactCollector | `project/dotnet/src/SwarmAssistant.Runtime/Execution/GitArtifactCollector.cs` |
| RFC-006 stashed output | `git stash list` → `stash@{0}` (on main workspace) |
| Previous handover | `docs/handover/2026-02-26-rfc-009-to-next.md` |
