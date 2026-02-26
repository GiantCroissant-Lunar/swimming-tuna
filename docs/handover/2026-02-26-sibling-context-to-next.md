# Handover: Sibling Context Validation + RFC-007 Landing

**Date:** 2026-02-26
**From session:** Sibling context dogfood validation + memvid query fixes
**Commits:** `eb93b2d` (sibling context fix), `912acd7` (RFC-007 from other agent)

## What Was Done This Session

### Sibling Context End-to-End Validation — Commit `eb93b2d`

**Root cause found:** `TryBuildSiblingContextAsync` used `_description` (full task description) as the memvid lex query. Long descriptions with domain-specific terms have zero keyword overlap with sibling content, causing lex search to return `{"results": []}` every time.

**Fixes applied:**
1. **Query strategy**: Changed from `_description` to `_title` — titles are short, keyword-rich, and overlap better with stored content
2. **Timeline fallback**: When `FindAsync` returns empty, falls back to `TimelineAsync` to guarantee context availability
3. **MemvidSearchMode default**: Changed from `"auto"` to `"lex"` — auto mode returns empty for small stores
4. **Kimi non-interactive**: Added `--print` flag (implicit `--yolo` auto-approval) to kimi adapter definition
5. **Kimi re-enabled**: Added back to dogfood `CliAdapterOrder`
6. **Info-level logging**: All sibling context code paths now log at Info level for visibility
7. **Test fix**: Updated `RuntimeOptions_MemvidDefaults_AreCorrect` for new `"lex"` default

**Validation run:**
- Task 1 (Add VerifyMaxRetries doc comment): Plan → Build → Verify(625 pass) → Review → Done
- Task 2 (Add MemvidSiblingMaxChunks doc comment): Plan → Build → Verify → Review → Done
- Task 2 log: `Built sibling context from 1 stores (391 chars)` — **confirmed working**

**Debugging insight:** `.NET Console logger outputs category prefix and message on SEPARATE lines`. Grep for message text directly (e.g., `grep -a 'Built sibling'`), not for task ID + message on same line.

### RFC-007 Budget-Aware Lifecycle — Commit `912acd7` (Other Agent)

PR #161 merged RFC-007 with:
- `BudgetEnvelope` record (token tracking, warning/hard thresholds, exhaustion detection)
- `AgentStatusResolver` (active → low-budget → exhausted → unhealthy status)
- `AgentRegistryActor` budget-aware agent selection
- `DispatcherActor` budget-aware dispatch (skip exhausted agents)
- `SwarmAgentActor` token usage tracking and budget reporting
- `TaskCoordinatorActor` learning context wired into role prompts
- `Worker` pool scaling awareness
- 6 new RuntimeOptions: `BudgetEnabled`, `BudgetType`, `BudgetTotalTokens`, `BudgetWarningThreshold`, `BudgetHardLimit`, `BudgetCharsPerToken`
- Memvid test infrastructure: `MemvidTestEnvironment`, `RequiresMemvidFactAttribute` (opt-in via `MEMVID_INTEGRATION_TESTS=1`)
- Test count: 642 passed, 3 skipped (645 total)

## Current State

| Item | Status |
|------|--------|
| Sibling context | Validated end-to-end |
| MemvidSearchMode | Default `"lex"` (was `"auto"`) |
| Kimi adapter | `--print` flag added, not yet dogfood-validated |
| RFC-007 budget | Merged, not yet dogfood-validated |
| RFC-010 provider abstraction | Merged (commit `c059721`) |
| Test count | 645 total (642 pass + 3 skip) |

## Key Files Changed This Session

| File | Change |
|------|--------|
| `TaskCoordinatorActor.cs` | Sibling query uses `_title`, timeline fallback, info logging |
| `RuntimeOptions.cs` | MemvidSearchMode `"auto"` → `"lex"`, doc comments |
| `SubscriptionCliRoleExecutor.cs` | Kimi adapter `--print` flag |
| `appsettings.Dogfood.json` | Re-enabled kimi in adapter order |
| `MemvidClientTests.cs` | Updated default assertion to `"lex"` |
| `cli.py` / `test_cli.py` | Minor ruff-format fixes, `cmd_info` test |

## Known Issues

| Priority | Issue | Status |
|----------|-------|--------|
| P0 | RFC-001 `/a2a/tasks` per-agent endpoint unused outside tests | Open |
| P1 | Orchestrator sometimes skips Build/Plan for simple tasks | By design, complicates sibling context |
| P1 | AG-UI contract drift | Partially addressed |
| P1 | `gh-aw` review-resolve `max_patch_size:1024` | Open |
| P1 | Langfuse shows no LLM request/response JSON | Open (capture proxy planned) |
| P1 | kilo Builder timeout at 300s for complex tasks | Open |
| P2 | RFC-003 host allowlist incomplete (Linux/container) | Open |
| P2 | memvid SDK `info` command: `object of type 'Memvid' has no len()` | Open |

## Memvid Query Behavior Reference

Tested against task-0da2 `.mv2` store (SanitizeMemvidQuery builder+reviewer output):

| Query | Mode | Result |
|-------|------|--------|
| `"test"` | lex | 3 results (score ~1.4) |
| `"unit"` | lex | results |
| `"SanitizeMemvidQuery unit test"` | lex | 3 results (score ~1.8) |
| `"builder reviewer"` | lex | results |
| `"implementation"` | lex | results |
| `"integration test"` | lex | **empty** (no "integration" in stored text) |
| `"MemvidClient test results"` | lex | **empty** |
| Long task description (~200 chars) | lex | **empty** (too many non-matching terms) |
| `"test"` | auto | 3 results |

**Takeaway:** Lex search requires at least one query term to appear in stored content. Titles work well; full descriptions usually fail.

## Implementation Order

~~RFC-006~~ → ~~Memvid integration~~ → ~~Memvid dogfood~~ → ~~Sibling context~~ → ~~RFC-010~~ → ~~RFC-007~~ → RFC-008

## Pre-flight for Next Session

1. RFC-007 budget features need dogfood validation
2. Kimi `--print` non-interactive mode needs dogfood validation
3. Consider RFC-008 (dashboard layers) or other swarm features
4. Test count: 645 (642 pass + 3 skip)
5. All on main, no open PRs
