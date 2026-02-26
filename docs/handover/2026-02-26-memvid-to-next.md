# Handover: Memvid Run Memory Integration

**Date:** 2026-02-26
**From session:** PR #157 review + memvid design + implementation
**PR:** #159 (`feat/memvid-run-memory`) — open, awaiting bot review

## What Was Done This Session

### PR #157 (RFC-006 Langfuse) — Merged
- Addressed 5 bot review comments (2 from Gemini, 3 from CodeRabbit)
- Rejected 1 (fire-and-forget score writes — loses error visibility)
- Accepted: remove double exception swallowing, extract verdict helper, remove redundant guard, add `.Take(5)` bound + truncation for LLM context, full repo-relative paths in handover doc
- Squash-merged to main, 591 tests passing

### Memvid Architecture Discussion
- Discussed agent collaboration limits: resource constraints, lack of natural collaboration, coordination overhead
- Identified Langfuse gap: only receives OTel spans, not actual LLM request/response payloads
- Proposed three-layer capture: Langfuse (operational), memvid (product/replay), git (code artifacts)
- memvid v2 is a Rust rewrite with Python bindings — `.mv2` single-file format, no video encoding
- Decided: unified memvid format for all run knowledge (no separate JSON artifacts)

### Memvid Integration — PR #159 Open
- Design doc: `docs/plans/2026-02-26-memvid-run-memory-design.md`
- Implementation plan: `docs/plans/2026-02-26-memvid-run-memory.md`
- 13 commits, 15 tasks (14 completed, 1 deferred — Python tests need memvid-sdk installed)

**Key decisions:**
| Decision | Choice |
|----------|--------|
| Deployment | Subprocess CLI (not sidecar) |
| IPC | CLI invocations (not long-lived process) |
| File location | Inside worktree `.swarm/memory/` |
| Encoding trigger | Inline in actor (blocks until done) |
| Sibling format | Unified memvid (not separate JSON) |

**Components built:**
1. Python CLI (`project/infra/memvid-svc/`) — create, put, find, timeline, info
2. C# `MemvidClient` (`SwarmAssistant.Runtime/Memvid/`) — subprocess wrapper + typed models
3. `RuntimeOptions` — 6 new properties behind `MemvidEnabled` flag (default: off)
4. `TaskCoordinatorActor` — run.mv2 creation, task encoding, sibling context query
5. `RolePromptFactory` — 7th context layer for sibling task memory
6. `.gitignore` — `.swarm/memory/` excluded
7. `meta.json` — run index written alongside run.mv2

**Test count:** 606 (605 passed, 1 skipped integration test)

## Next Session Pre-flight

### 1. Address PR #159 Review Comments
- Bot reviewers (Gemini, CodeRabbit) will post comments
- Use receiving-code-review skill for evaluation
- CS0854 pattern likely to recur (8 test files updated for new constructor param)

### 2. Merge PR #159
- Squash-merge after review
- Verify 606+ tests on main

### 3. Decide Next Steps
Options to discuss with user:

**A. Install memvid-sdk and dogfood**
- pip install memvid-sdk, enable MemvidEnabled=true
- Run a swarm task and verify .mv2 files are created/queried
- Enable the skipped integration test

**B. Implement soft dependencies (Part 2 of cross-task coordination)**
- `TaskAssigned.DependsOn` field
- `DispatcherActor` holds dependent tasks until predecessors complete
- Complements the sibling context layer already built

**C. LLM capture proxy**
- Route adapter LLM traffic through local proxy
- Capture full request/response JSON into per-task .mv2 stores
- Enables the Godot replay use case discussed this session

**D. Continue to RFC-010 (provider abstraction)**
- Another agent is handling this — check status

### 4. Memvid + Godot future work (discussed, not implemented)
- `.mv2` files as game assets for the tycoon app
- Run recording/replay via memvid timeline + time-travel search
- Visual presentation of agent "memory" in Godot UI
- This is a separate effort after the integration is validated

## Known Issues

| Priority | Issue | Status |
|----------|-------|--------|
| P0 | RFC-001 /a2a/tasks per-agent endpoint unused outside tests | Open |
| P1 | CS0854 recurring — builder ignores expression tree warnings | Mitigated (sibling context) |
| P1 | AG-UI contract drift | Partially addressed |
| P1 | gh-aw review-resolve max_patch_size:1024 | Open |
| P1 | Langfuse shows no LLM request/response JSON | Open (memvid capture proxy planned) |
| P2 | RFC-003 host allowlist incomplete (Linux/container) | Open |
| P2 | Python CLI tests deferred (need memvid-sdk installed) | Open |

## Implementation Order
~~RFC-006~~ → ~~Memvid integration~~ → Soft dependencies / RFC-010 → RFC-007 → RFC-008

## Test Count
606 (on feature branch, main is at 591 until merge)
