# Post-Replay Dogfooding: Diagnostic Sprint + Context Fix

Date: 2026-02-24

## Problem

Issues #123-#126 delivered replay event persistence, unified run semantics, OpenAPI
codegen sync, and smoke test hardening. The runtime can now **observe** what happened
during a task execution. However, the runtime cannot yet **produce useful code changes**
on its own codebase. The planner-builder-reviewer pipeline executes, but the builder's
CLI adapter lacks sufficient codebase context to generate targeted file modifications.

## Prerequisites

- #123: Runtime lifecycle events wired to `ITaskExecutionEventWriter` (merged)
- #124: Run existence semantics unified across endpoints (merged)
- #125: `runId` added to A2A submit + TaskSnapshot OpenAPI schemas (merged)
- #126: Dogfood smoke tests assert non-empty replay feeds (merged)

## Goal

Enable SwarmAssistant to execute real tasks on its own codebase through the
planner -> builder -> reviewer pipeline, producing actual code changes.

## Success Criteria

- Submit a real task via `POST /a2a/tasks` with a `runId`
- Planner produces a file-scoped, actionable plan
- Builder executes the plan via CLI adapter and produces file changes on a working branch
- Reviewer validates the changes against the plan
- Replay feed shows the full lifecycle with meaningful content
- Human can approve/reject via HITL action

## Non-Goals

- Multi-task parallelism or swarm scaling
- Godot UI polish
- New actor types or protocol changes
- Direct API key support

## Design

### Approach: Hybrid Diagnostic Sprint (Approach C)

1. Run a diagnostic end-to-end task to identify where the pipeline breaks
2. Fix the critical path (context injection + workspace isolation)
3. Validate with a second real task

This avoids speculative engineering while building the right foundations.

### Phase 1: Diagnostic End-to-End Run

**Purpose:** Submit one real task through the full pipeline and trace where it breaks.

**Steps:**
1. Boot runtime with ArcadeDB enabled (`task run:aspire` or manual docker + runtime)
2. Submit a low-risk task via `POST /a2a/tasks` — e.g., "Add a unit test for
   LegacyRunId.Resolve when input is null"
3. Instrument each pipeline stage with structured logging to capture:
   - What context the planner received (CodeIndexActor output, prompt content)
   - What plan the planner produced (structured output vs free-text)
   - What CLI command the builder invoked (adapter selection, args, env)
   - What the CLI adapter returned (stdout, stderr, exit code)
   - What the reviewer saw and decided (diff content, verdict, confidence)
4. Collect replay feed via `GET /runs/{runId}/events`
5. Write diagnostic report documenting success/degradation/failure points

**Deliverable:** `docs/plans/diagnostic-report.md`

**Key question answered:** Where in the planner->builder->reviewer chain does real
code generation break down?

### Phase 2: Context Injection Fix

**Purpose:** Fix the critical path identified in Phase 1. Based on reference project
analysis (Overstory two-layer overlays, BMAD-METHOD project-context pattern), the most
likely gap is context injection.

**Expected fix areas (confirmed by Phase 1 diagnostic):**

#### 2a. Planner Context Enrichment

Feed the planner role with:
- Structural code index from `CodeIndexActor` (file list, class/method signatures)
- AGENTS.md conventions and architecture summary
- File-scoped constraints (which files to touch, which to avoid)

Modify `TaskCoordinatorActor` planner role dispatch to include enriched context in the
role prompt.

#### 2b. Builder Task Overlay

Inspired by Overstory's two-layer agent definition pattern:
- **Base layer:** Role instructions (how to be a builder) — exists in `RolePromptFactory`
- **Task layer:** Per-task context injected into the CLI adapter call:
  - Target files from planner output
  - Relevant code snippets from code index
  - Acceptance criteria from original task
  - Project conventions from AGENTS.md

Modify CLI adapter invocation to pass task overlay as context/prompt.

#### 2c. Workspace Isolation

Ensure the builder operates on an isolated branch:
- Create `git checkout -b swarm/task-{taskId}` before builder executes
- CLI adapter runs within that branch context
- Changes are inspectable before merge
- Reviewer evaluates the diff on the working branch

**Deliverable:** Runtime changes wiring context into planner and builder stages.

### Phase 3: Validation Run

**Purpose:** Execute a second real task to confirm the fixes work.

**Steps:**
1. Pick a different task type than Phase 1
2. Submit through the same pipeline
3. Verify:
   - Replay feed is non-empty (smoke tests from #126)
   - Plan references specific files and is actionable
   - Builder produces a diff on the working branch
   - Reviewer evaluates the diff meaningfully
   - HITL approval/rejection works end-to-end
4. If it passes, the swarm is dogfood-ready for scoped tasks

**Deliverable:** Second diagnostic report or confirmation that the loop works.

## Dependency Graph

```text
#123 (replay events) --+
#124 (run semantics) --+--> Phase 1 (diagnostic) --> Phase 2 (context fix) --> Phase 3 (validation)
#125 (OpenAPI sync)  --+
#126 (smoke tests)   --+
```

## Reference Project Patterns Adopted

| Pattern | Source | Application |
|---------|--------|-------------|
| Two-layer agent definition | Overstory | Base role prompt + per-task overlay |
| Project context injection | BMAD-METHOD | AGENTS.md fed to planner |
| Workspace isolation | Overstory | Git branch per task |
| Diagnostic-first approach | All three | Prove before engineering |

## Out of Scope

- Container-level isolation (NanoClaw pattern) — deferred until basic dogfooding works
- Expertise curation (Overstory mulch pattern) — deferred to post-dogfood
- Multi-agent collaboration (BMAD-METHOD party mode) — deferred
- Tiered merge resolution — deferred until multi-task parallel execution
