# Agent API, Pi Adapter & Hierarchy Model — Design

**Date:** 2026-02-26
**RFCs:** RFC-011, RFC-012, RFC-013

## Context

SwarmAssistant agents currently execute only via CLI adapters (copilot, cline, kimi,
kilo, local-echo). The `api-direct` execution path exists but has never been activated
in dogfood. Additionally, the swarm treats each CLI agent as a black box — sub-agents
spawned internally by CLI tools (Claude Code's Task, Pi's /control) are invisible.

For the Godot dashboard (RFC-008 Layer 5), every agent should be representable as a
character — including sub-agents. This requires a hierarchical model of agent execution.

## Three RFCs

### RFC-011: API-Direct Activation & Native Providers
- Add `AnthropicModelProvider` (native Messages API)
- Activate `hybrid` execution mode in dogfood
- End-to-end token reporting to Langfuse
- Resolves P1: "Langfuse shows no LLM request/response JSON" for API calls

### RFC-012: Pi Adapter & Self-Extending Agent Patterns
- Add Pi as 6th CLI adapter
- Document Pi's design philosophy as research input
- Research spike: should agents self-extend at runtime?

### RFC-013: Agent Hierarchy Model & Multi-Level Tracing
- `AgentSpan` data model with levels: Coordinator → Agent → Sub-Agent
- `IOutputEnricher` per adapter: parse kilo JSON, Claude Code JSONL, Cline UI messages
- Nested Langfuse spans and Godot hierarchy payload
- Sub-agent flavors: Normal (independent) vs CoWork (peer coordination)

## Implementation Order

```
RFC-011 → RFC-012 → RFC-013
```

RFC-011 first: API-direct gives clean token data as baseline.
RFC-012 is small and independent but benefits from RFC-011's provider infrastructure.
RFC-013 depends on both: it models all agent types and their structured output.

## Agent Hierarchy Mental Model

```
Level 0: TaskCoordinatorActor (one per task)
├── Level 1: kilo (CLI, Builder role)
│   ├── Level 2: tool-call (read file)
│   ├── Level 2: tool-call (write file)
│   └── Level 2: tool-call (run tests)
├── Level 1: anthropic-api (API-direct, Planner role)
│   └── (no sub-agents — single prompt/response)
├── Level 1: claude-code (CLI, Builder role)
│   ├── Level 2: sub-agent (Task: research)
│   ├── Level 2: sub-agent (Task: implement)     ← CoWork
│   └── Level 2: sub-agent (Task: test)
└── Level 1: pi (CLI, Reviewer role)
    └── Level 2: sub-agent (/control: verify)
```

Godot renders each span as a character. Level 0 is the manager, Level 1 agents
are workers at desks, Level 2 sub-agents appear as helpers near their parent.

## Structured Output Availability

| Adapter | Token data | Sub-agent visibility | Enricher |
|---------|-----------|---------------------|----------|
| Kilo | `--format json` live + `kilo export` post-hoc | Tool calls | KiloOutputEnricher |
| Claude Code | Session JSONL (~/.claude/projects/) | Task sub-agents | ClaudeCodeOutputEnricher |
| Cline | `ui_messages.json` post-hoc | API calls | ClineOutputEnricher |
| Copilot | Logs only (timing, no tokens) | None | NoOpOutputEnricher |
| Kimi | None | None | NoOpOutputEnricher |
| Pi | TBD (session files) | TBD (/control) | NoOpOutputEnricher (v1) |
| API-direct | Full (from ModelResponse) | N/A | N/A (direct) |
