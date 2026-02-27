# RFC-014 Dogfood Incident Follow-up (Compile Gate)

**Date:** 2026-02-27
**Context:** RFC-014 swarm run with pi-first adapter flow

## What happened

During an in-flight RFC-014 run, generated code introduced a compile error:

- `project/dotnet/src/SwarmAssistant.Runtime/Execution/WorkspaceBranchManager.cs`
- `EnsureWorktreeAsync(...)` called `RunGitAsync(args)` with `List<string>`
- `RunGitAsync` expects `string[]`

Error surfaced on runtime restart/build:

- `CS1503: cannot convert from 'System.Collections.Generic.List<string>' to 'string[]'`

## Root cause

1. **Code defect in generated change**
   The new parent-branch worktree logic built arguments as `List<string>` but did not convert to array at callsite.

2. **Operational timing confusion**
   The runtime was restarted while tasks were still active.
   At that point, tasks were not yet terminal (`completed=0`), but the workspace contained partial generated edits.

## Why tasks were not validly "done"

GOAP still enforces verify/review/finalize ordering:

- `Review` requires `BuildCompiles=true`
- `Finalize` requires `ReviewPassed=true`

So this was not a successful completed task set passing with a broken build; this was an in-progress workspace snapshot being rebuilt after interruption.

## Immediate remediation applied

1. Fixed compile error in `WorkspaceBranchManager` by passing `args.ToArray()` to `RunGitAsync`.
2. Added kickoff guard in `scripts/dogfood-rfc014.sh` to block new RFC-014 runs if active tasks exist (unless explicitly overridden with `ALLOW_ACTIVE_TASKS=1`).

## Follow-up adjustments (recommended)

1. **Enforce "no active tasks" at API level**
   Add optional server-side guard for `POST /runs` and/or a "force" override flag.

2. **Persist explicit verify/build evidence in task terminal metadata**
   Include last verify result (build/test pass/fail) in final task snapshot to reduce ambiguity.

3. **Runbook requirement**
   Do not restart runtime during active swarm runs unless recovery mode is explicitly invoked and logged.

---

## Additional obstacle discovered (pi + Kimi provider)

### What happened

When running RFC-014 with `pi` first in adapter order, execution repeatedly fell back to `kilo`/`kimi`.
Direct `pi` probe reproduced the failure:

- `pi --model kimi-coding/kimi-for-coding -p "Reply with exactly: ok"`
- Result: `403 Kimi For Coding is currently only available for Coding Agents ...`

### Root cause

1. **Provider config mismatch in initial setup script**
   `scripts/setup-pi-kimi.sh` previously overrode only `baseUrl`. For built-in `kimi-coding`, this retained default API/model behavior and could produce endpoint mismatch errors (`resource_not_found`).

2. **Provider-side policy restriction**
   Even with corrected provider config (`openai-completions` + `kimi-for-coding`), Kimi For Coding currently denies `pi` with an allowlist-style policy message, while `kimi`/`kilo` continue to work.

### Follow-up changes applied

1. Updated `scripts/setup-pi-kimi.sh` to write a full provider override:
   - `api: openai-completions`
   - `apiKey: KIMI_API_KEY`
   - `authHeader: true`
   - upsert model `kimi-for-coding` metadata
2. Added a `pi` preflight probe to fail fast with actionable diagnostics.
3. Added explicit guidance in output to use supported adapters (`kilo`/`kimi`) first when provider policy blocks `pi`.

---

## Additional obstacle discovered (pi + z.ai runtime hinting)

### What happened

After switching pi to z.ai `glm-4.7`, runtime still fell back from `pi` to `kilo`/`kimi` even though direct preflight succeeded.

### Root cause

`SubscriptionCliRoleExecutor.ApplyModelExecutionHints(...)` only passed `--model <id>` for all adapters.
For pi, `--model glm-4.7` without `--provider zai` can resolve to a different provider (observed `opencode`) and fail auth.

### Remediation applied

1. Added `ProviderFlag` support to adapter definitions.
2. Configured pi adapter with `ProviderFlag="--provider"`.
3. Updated model hint injection to pass provider before model for adapters that define a provider flag.
4. Added runtime tests in `SubscriptionCliRoleExecutorTests` to verify pi receives `--provider zai --model glm-4.7`.
