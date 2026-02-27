# Finding: Builder Produces Zero File Changes in Worktrees

**Date:** 2026-02-27
**Severity:** P0
**Status:** Root cause identified, fix deferred (post RFC-014/015/016)
**Affects:** All dogfood runs using `RoleModelMapping` with non-native models

## Summary

RFC-014 dogfood run produced 0/4 usable task outputs. All tasks reached "done" status through the planner→builder→reviewer pipeline, but worktree directories had zero `git diff` or `git status` changes. The builder CLI adapter returned non-empty text output (describing code changes) without actually modifying files.

## Root Cause

The `RoleModelMapping` configuration added in the RFC-014 dogfood config forces all roles to use `zai/glm-4.7`. This injects `--model glm-4.7` into every CLI adapter invocation, overriding the adapter's native model.

Previous successful dogfood runs (RFC-006, RFC-012) had **no `RoleModelMapping`** — adapters used their default models and produced file changes reliably.

### How it breaks each adapter

| Adapter | Default (working) | With RoleModelMapping | Failure mode |
|---------|-------------------|-----------------------|--------------|
| kimi | kimi-2.5 (native, fixed) | `--model glm-4.7` injected | Forces non-native model; kimi's tool-calling is built for kimi-2.5 |
| kilo | kilo's default | `--model glm-4.7` (missing `zai/` prefix) | `--model` expects `provider/model` format; can't route to provider |
| pi | provider-agnostic | `--provider zai --model glm-4.7` (correct flags) | GLM-4.7 may not generate proper tool calls through pi's framework |

### The mechanism

1. `ApplyModelExecutionHints()` in `SubscriptionCliRoleExecutor` splits `"zai/glm-4.7"` into `Provider="zai"` and `Id="glm-4.7"` via `RoleModelMapping.ParseModelSpec()`
2. For adapters **with** `ProviderFlag` (pi): passes `--provider zai --model glm-4.7` — correct
3. For adapters **without** `ProviderFlag` (kilo, kimi): passes only `--model glm-4.7` — no provider context
4. The adapter runs, the model produces text output describing code changes but doesn't invoke the agent's file write/edit tools
5. `SubscriptionCliRoleExecutor` considers non-empty output a success
6. No files modified → orchestrator sees no diff → triggers rework loop (P1)

### Evidence

| Run | Config | Adapter | Model | File Changes |
|-----|--------|---------|-------|--------------|
| RFC-006 dogfood | No RoleModelMapping | copilot | default (GitHub Copilot) | 6/6 |
| RFC-012 dogfood | No RoleModelMapping | kilo → kimi | defaults | 3/3 |
| **RFC-014 dogfood** | **RoleModelMapping: zai/glm-4.7** | **pi → kimi** | **GLM-4.7 forced** | **0/4** |

### Config diff

```diff
 // RFC-012 dogfood (worked)
 {
   "CliAdapterOrder": ["kilo", "kimi", "", "", ""],
+  // No RoleModelMapping — adapters use native models
 }

 // RFC-014 dogfood (broken)
 {
   "CliAdapterOrder": ["pi", "kilo", "kimi", "", ""],
+  "RoleModelMapping": {
+    "Orchestrator": { "Model": "zai/glm-4.7", "Reasoning": "low" },
+    "Planner": { "Model": "zai/glm-4.7", "Reasoning": "high" },
+    "Builder": { "Model": "zai/glm-4.7", "Reasoning": "medium" },
+    "Reviewer": { "Model": "zai/glm-4.7", "Reasoning": "low" }
+  }
 }
```

## Secondary Issue: P1 Orchestrator Rework Loop

The rework loop is a **direct consequence** of the zero file changes:

1. Builder "completes" with text output but no file modifications
2. Orchestrator evaluates the result, sees no actual code changes
3. Orchestrator decides "Rework" — sends builder back
4. Builder runs again, same result (text only, no files)
5. Loop repeats for 50+ ticks without convergence

**Fix:** Solving the builder file change issue eliminates the rework loop.

## Cascading Impact

- **Dogfood validation blocked** for all RFCs requiring builder output
- **Budget waste** — tokens consumed for text-only output with no usable artifact
- **False positive pipeline** — tasks reach "done" status despite producing nothing

## Fix Plan (Deferred)

### Immediate workaround

Remove `RoleModelMapping` from `appsettings.Dogfood.json` to restore native adapter models. This restores the working behavior from RFC-012 dogfood.

### Future: Adapter model compatibility matrix

CLI adapters fall into two categories that need different handling:

| Category | Adapters | Model behavior |
|----------|----------|----------------|
| Multi-model | pi, kilo, cline, droid | Support arbitrary `--provider`/`--model` overrides |
| Fixed-model | kimi (kimi-2.5 only) | Should ignore or reject model overrides |
| Constrained-model | copilot, codex | Support a specific set of models within their ecosystem |

The `RoleModelMapping` → `ApplyModelExecutionHints` pipeline needs to be model-compatibility-aware:

1. **Adapter capability config** — each adapter declares whether it accepts arbitrary models, a fixed set, or none
2. **Skip injection for fixed-model adapters** — don't pass `--model` to kimi
3. **Format-aware injection** — kilo expects `provider/model`, pi expects separate `--provider`/`--model`
4. **Validation** — warn at startup if RoleModelMapping specifies a model incompatible with the active adapter order

### Additional hardening

1. **Post-builder file change detection** — after builder CLI returns, check `git status --porcelain` in the worktree; treat zero changes as a build failure rather than success
2. **Output classification** — detect whether builder output contains evidence of tool execution vs. text-only description

## Key Files

| File | Relevance |
|------|-----------|
| `project/dotnet/src/SwarmAssistant.Runtime/Execution/SubscriptionCliRoleExecutor.cs` | Model hint injection, adapter definitions, process execution |
| `project/dotnet/src/SwarmAssistant.Runtime/Execution/RoleModelMapping.cs` | Model spec parsing, provider resolution |
| `project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs` | Builder dispatch, worktree path assignment |
| `project/dotnet/src/SwarmAssistant.Runtime/appsettings.Dogfood.json` | RoleModelMapping config |

## Related

- P0 worktree issue in `docs/handover/2026-02-27-rfc-014-to-next.md`
- Adapter configuration history in `docs/handover/2026-02-27-rfc-014-compile-gate-followup.md`
- RFC-014 dogfood run log in `docs/dogfooding/runs/2026-02-27-rfc-014-run-orchestration.md`
