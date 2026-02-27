# Agent API, Hierarchy & Run Orchestration — Design

**Date:** 2026-02-26 (updated 2026-02-27)
**RFCs:** RFC-011, RFC-012, RFC-013, RFC-014, RFC-015

## Context

SwarmAssistant agents execute via CLI adapters and API-direct providers. The
swarm treats each agent as a black box — sub-agents spawned internally are
invisible. Additionally, the run lifecycle (accept design doc → decompose →
execute → merge → PR) is entirely manual, performed by the gatekeeper.

This design covers five RFCs that build the full agent hierarchy — from data
model to orchestration to observability.

## Five RFCs

### RFC-011: API-Direct Activation & Native Providers (Done)
- `AnthropicModelProvider` (native Messages API)
- `hybrid` execution mode in dogfood
- End-to-end token reporting to Langfuse

### RFC-012: Pi Adapter (Done)
- Pi as 6th CLI adapter with `--print`/`--prompt` execution
- `pi-agent` provider fallback

### RFC-013: Agent Hierarchy Model (Draft)
- `AgentSpan` — flat record with `ParentSpanId`, levels, kinds, flavors
- `RunSpan` — run-level container above task coordinators
- `AgentSpanCollector` — span lifecycle, flat storage, tree projection on demand
- `TokenUsage` — shared token metrics type
- **Shared foundation** for both RFC-014 and RFC-015

### RFC-014: Run Orchestration & Tree-Structured Git (Draft)
- `POST /a2a/runs` — accept design doc, initiate run
- `RunCoordinatorActor` — run lifecycle state machine
- `SwarmRole.Decomposer` — LLM decomposes doc into tasks
- Tree-structured git: feature branch (L0) → task worktrees (L1) → merge-back
- Sequential merge gate (`SemaphoreSlim`) for serialized integration
- `agui.run.ready-for-pr` signal for gatekeeper

### RFC-015: Multi-Level Tracing & Enrichment (Draft)
- `IOutputEnricher` per adapter: parse kilo JSON, Claude Code JSONL, Cline UI messages
- `EnrichmentPipeline` — post-hoc enrichment after role execution
- Nested Langfuse spans mapped from `AgentSpan` tree
- Godot `agent-hierarchy` A2UI payload for RFC-008 Layer 5

## Implementation Order

```
RFC-011 (done) → RFC-012 (done) → RFC-013 → RFC-014 → RFC-015
                                      │
                                      ├── RFC-014 (doing the work)
                                      └── RFC-015 (observing the work)
```

RFC-013 first: shared data model unlocks both others.
RFC-014 next: highest practical value — automates the manual gatekeeper workflow.
RFC-015 last: observability polish, independent from RFC-014.
RFC-014 and RFC-015 can be parallelized after RFC-013 lands.

## Full Hierarchy Mental Model

```
RunSpan (run — RFC-014)
│
└── AgentSpan Level 0: TaskCoordinatorActor (one per task)
    │
    ├── AgentSpan Level 1: kilo (CLI, Builder role)
    │   ├── Level 2: tool-call (read file)          ← RFC-015 enrichment
    │   ├── Level 2: tool-call (write file)
    │   └── Level 2: tool-call (run tests)
    │
    ├── AgentSpan Level 1: anthropic-api (API-direct, Planner role)
    │   └── (no sub-agents — single prompt/response)
    │
    ├── AgentSpan Level 1: claude-code (CLI, Builder role)
    │   ├── Level 2: sub-agent (Task: research)     ← RFC-015 enrichment
    │   ├── Level 2: sub-agent (Task: implement)    ← CoWork
    │   └── Level 2: sub-agent (Task: test)
    │
    └── AgentSpan Level 1: pi (CLI, Reviewer role)
        └── Level 2: sub-agent (/control: verify)   ← RFC-015 enrichment
```

## Git Tree Structure (RFC-014)

```
main
 └── feat/rfc-012-pi-adapter              ← L0: RunCoordinatorActor
      ├── swarm/task-001                  ← L1: builder worktree (merges back)
      ├── swarm/task-002                  ← L1: builder worktree (merges back)
      └── swarm/task-003                  ← L1: builder worktree (merges back)
                                          ← push + agui.run.ready-for-pr
```

## Structured Output Availability (RFC-015)

| Adapter | Token data | Sub-agent visibility | Enricher |
|---------|-----------|---------------------|----------|
| Kilo | `--format json` live + `kilo export` post-hoc | Tool calls | KiloOutputEnricher |
| Claude Code | Session JSONL (~/.claude/projects/) | Task sub-agents | ClaudeCodeOutputEnricher |
| Cline | `ui_messages.json` post-hoc | API calls | ClineOutputEnricher |
| Copilot | Logs only (timing, no tokens) | None | NoOpOutputEnricher |
| Kimi | None | None | NoOpOutputEnricher |
| Pi | TBD (session files) | TBD (/control) | NoOpOutputEnricher (v1) |
| API-direct | Full (from ModelResponse) | N/A | N/A (direct) |
