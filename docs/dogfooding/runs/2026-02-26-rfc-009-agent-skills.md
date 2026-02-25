# Dogfooding Run Log

- **Date:** 2026-02-26
- **Operator:** @apprenticegc
- **Run goal:** Implement RFC-009 Phase 1 — agent skills layer (skill models, parser, index, matcher, prompt injection, seed skills)
- **Runtime profile:** Local
- **Adapter order:** copilot → cline → kimi → local-echo
- **ArcadeDB enabled:** no
- **Langfuse tracing enabled:** no
- **Fault injection:** SimulateBuilderFailure=false, SimulateReviewerFailure=false

## Self-Improvement Run Metadata

- **Target component:** runtime
- **Phase under test:** Phase D (multi-agent evolution)
- **Expected output artifact:** `project/dotnet/src/SwarmAssistant.Runtime/Skills/` directory + RolePromptFactory Layer 5
- **Eval criteria:** All tests pass (baseline 471), new skill matching tests, skills injected into role prompts
- **Baseline metrics:** 471 tests, build <10s

## Tasks Submitted

| # | taskId | Title | Final state | Notes |
|---|--------|-------|-------------|-------|
| 1 | task-84166faa | Skill model types (SkillDefinition, MatchedSkill records) | done | ~9min, planner found existing code from concurrent builders |
| 2 | task-0d8be4ab | Skill file parser (YAML frontmatter + markdown body) | done | ~7min, copilot+kimi adapters |
| 3 | task-6cd6a829 | Skill index builder (scan directory, build tag index) | done | ~10min, last to complete |
| 4 | task-fd9ee03a | Skill matcher (tag overlap + role filter → ranked skills) | done | ~6min, copilot adapter |
| 5 | task-86b5844e | RolePromptFactory Layer 5 (BuildSkillContext, 4KB budget) | done | ~7min, planner+builder+review cycle |
| 6 | task-c6bc67e9 | Seed initial skills from gatekeeper patterns | done | ~6min, plan-only (files created by earlier builders) |

## Observations

- All 6 tasks completed via planner→builder→reviewer pipeline in ~10 minutes total
- Swarm planners reported "ALREADY IMPLEMENTED" for tasks 2/4/5/6 because earlier concurrent builders had already created the files in the shared workspace — no worktree isolation
- Multiple CLI adapters used: copilot (primary), kimi (secondary), cline (tertiary)
- GOAP plan for all tasks: Plan → Build → Review → Finalize
- No task failures or escalations

## Replay Findings

### Swarm output quality
- **Implementation code**: Good quality — sealed records, file-scoped namespaces, proper validation, YamlDotNet integration, regex source generators
- **Test code**: 29 failures from spec-vs-code drift (see gatekeeper fixes below)
- **Seed skills**: Well-structured YAML frontmatter + markdown body, correct tag coverage

### Gatekeeper fixes required (29 test failures)

| Category | Count | Root Cause |
|----------|-------|------------|
| SkillDefinitionTests | 5 | `Assert.Throws<ArgumentException>` vs `ArgumentNullException` subclass |
| SkillFileParserTests | 6 | Invalid `scope: local`, missing `roles:` in YAML, BOM regex edge case |
| SkillMatcherTests | 13 | Invalid scope values (`"backend"`, `"build"`, `"test"`) |
| SkillIndexBuilderTests | 5 | Invalid `scope: local` and `roles: []` in test YAML |

**Root pattern**: Builder created strict scope validation in SkillDefinition (`"global"` or `"task:*"`) but tests across all files used arbitrary scope values. Classic spec-vs-code drift — the swarm's #1 failure pattern.

## Retro

### What went well

- Swarm produced 2,932 lines of working implementation code across 24 files
- All 5 C# source files (models, parser, index, matcher) were architecturally sound
- 85 new tests written with good coverage of edge cases
- Code followed project conventions (sealed records, file-scoped namespaces, PascalCase)
- Concurrent task processing worked — all 6 tasks ran through the pipeline

### What did not go well

- **No test verification**: 29 test failures passed through the entire pipeline undetected — the swarm has no build/test gate
- **No worktree isolation**: All builders shared the same workspace, causing planners to see "already implemented" for code that concurrent builders wrote
- **Scope validation mismatch**: Builder added strict validation to SkillDefinition but test files used invalid values — same pattern as RFC-005
- **Trailing whitespace**: Pre-commit hooks caught whitespace issues in 5 swarm-produced files

### Action items

| Item | Owner | GitHub issue |
|------|-------|--------------|
| Add Verify GOAP step (build+test gate) | done (this session) | — |
| Add worktree isolation per task | next session | — |
| Add pre-commit hook awareness to builder prompt | backlog | — |
| Update gatekeeper guide with scope validation pattern | backlog | — |
