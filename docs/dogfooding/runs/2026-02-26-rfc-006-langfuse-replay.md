# RFC-006 Dogfooding Run — Langfuse Replay Engine

**Date:** 2026-02-26
**RFC:** rfc-006 (Langfuse as replay engine)
**Adapter chain:** copilot → cline → kimi → local-echo
**Worktree isolation:** enabled (first run with per-task worktrees)
**Commit:** 0a0f4bd

## Tasks Submitted (6)

| # | Task | Status | Adapter |
|---|------|--------|---------|
| 1 | ILangfuseApiClient interface + HttpLangfuseApiClient | Done | copilot |
| 2 | ILangfuseScoreWriter interface + implementation | Done | copilot |
| 3 | ILangfuseSimilarityQuery interface + implementation | Done | copilot |
| 4 | Wire ILangfuseScoreWriter into TaskCoordinatorActor | Done | copilot |
| 5 | Add Langfuse context to RolePromptFactory | Done | copilot |
| 6 | Unit tests for Langfuse integration | Done | copilot |

**Result:** 6/6 tasks completed, 0 failures

## Gatekeeper Fixes

### Blocking (3 issues fixed)

1. **32 CS0854 expression tree errors** — Swarm added optional `ILangfuseScoreWriter?` parameter to DispatcherActor and TaskCoordinatorActor constructors. Expression trees in `Props.Create(() => new Actor(...))` cannot use optional arguments. Added explicit `null` to all 32 call sites across 8 test files.

2. **HttpClient lifetime misuse** — `HttpLangfuseApiClient` was registered as singleton but stored an `HttpClient` from `IHttpClientFactory.CreateClient()` in a field, defeating handler rotation and causing potential socket exhaustion. Fixed: store `IHttpClientFactory`, create client per-request.

3. **Missing feature gate** — Langfuse services (HttpClient, ApiClient, ScoreWriter) were unconditionally registered regardless of `LangfuseTracingEnabled`. Fixed: gate DI registration behind `bootstrapOptions.LangfuseTracingEnabled`. Auth configured in `AddHttpClient` builder callback.

### Should-Fix (2 issues fixed)

4. **Double exception swallowing** — Both `HttpLangfuseApiClient.PostScoreAsync` and `LangfuseScoreWriter.WriteReviewerVerdictAsync` had try/catch, making the outer catch dead code. Fixed: removed catch from transport layer, domain layer is single catch point.

5. **Missing role gate** — Langfuse context in `RolePromptFactory.BuildPrompt` had no role restriction (would inject into Orchestrator, Researcher, etc.). Fixed: restricted to Planner/Builder/Reviewer.

### Also Fixed

6. **Dead DI registration** — `ILangfuseSimilarityQuery` was registered as `Scoped` (doesn't work with actor system) and never resolved anywhere. Removed registration, kept source files for future wiring.

## Observations

- **Worktree isolation worked** — 0 task failures (vs 4/6 in previous RFC-006 attempt without worktrees)
- **CS0854 pattern recurred** — Despite explicit task instructions warning about expression trees, builder still added optional constructor params. This is an LLM instruction-following limitation.
- **HttpClient anti-pattern** — Builder used singleton + stored HttpClient from factory, a known .NET anti-pattern. Suggests builder doesn't have deep ASP.NET Core DI awareness.
- **Code quality was solid** — File-scoped namespaces, sealed records, PascalCase, proper interface design. The swarm follows conventions well.
- **All 6 tasks completed on copilot** — No adapter fallback needed. Copilot CLI handled all tasks.

## Test Results

- Before: 583 tests
- After: 591 tests (+8 new Langfuse integration tests)
- All passing
