# RFC-012: Pi Adapter & Self-Extending Agent Patterns

**Status:** Draft
**Date:** 2026-02-26
**Dependencies:** RFC-010 (Provider Abstraction)
**References:** https://lucumr.pocoo.org/2026/1/31/pi/

## Problem

The swarm has five CLI adapters (copilot, cline, kimi, kilo, local-echo) but no Pi agent
adapter. Pi is a minimal coding agent by Mario Zechner with a distinctive philosophy:
4 built-in tools (Read, Write, Edit, Bash), self-extending through code generation, and
session-based execution with branching.

Beyond adding an adapter, Pi's design philosophy raises questions about whether
SwarmAssistant agents should be able to extend their own capabilities at runtime.

## Proposal

### Part 1: Pi CLI Adapter

Add Pi as a 6th entry in `AdapterDefinitions`:

```csharp
["pi"] = new(
    Id: "pi",
    ProbeCommand: "pi",
    ProbeArgs: ["--help"],
    ExecuteCommand: "pi",
    ExecuteArgs: ["--print", "--prompt", "{{prompt}}"],
    RejectOutputSubstrings: ["error: no api key", "error: authentication"],
    IsInternal: false,
    ModelFlag: "--model",
    ModelEnvVar: null,
    ModeFlag: null,
    ReasoningFlag: "--thinking",
    ReasoningEnvVar: null)
```

**Execution flags:**
- `--print` — non-interactive mode, outputs result to stdout (similar to kimi `--print`)
- `--prompt` — task prompt injection
- `--model` — model selection (e.g., `claude-sonnet-4-6`, `gpt-4o`)
- `--thinking` — reasoning depth: `off`, `minimal`, `low`, `medium`, `high`, `xhigh`

**Provider mapping** (for `RoleModelMapping.AdapterProviderFallbacks`):

```csharp
["pi"] = "pi-agent"  // or match by provider name in model spec
```

### Part 2: Pi-Specific Behaviors

Pi has unique capabilities that differ from other adapters:

**Session persistence:** Pi writes session files that can be inspected post-hoc.
Unlike other adapters where we only get stdout, Pi sessions contain structured
conversation history. Future RFC-013 work can parse these for sub-agent visibility.

**Extension system:** Pi auto-generates and loads extensions during execution.
When the swarm sends a task to Pi, it may create tools that persist across
invocations in the same session. This is a feature, not a bug — but the swarm
should be aware that Pi's capability set can grow during a run.

**`/control` sub-agents:** Pi can spawn sub-agents via its `/control` extension.
One Pi instance sends prompts to another. This maps to Level 2 in RFC-013's
hierarchy model. Detection: parse Pi's stdout for sub-agent spawn markers.

### Part 3: Architectural Lessons (Research Spike)

Pi demonstrates several patterns worth evaluating for SwarmAssistant:

| Pi Pattern | SwarmAssistant Equivalent | Gap |
|------------|--------------------------|-----|
| Self-extending tools | Agents have fixed roles | Agents can't create new tools |
| Session branching | Worktree isolation (PR #155) | No conversation branching |
| Minimal core (4 tools) | Rich prompt per role | Prompts may be over-specified |
| `/control` sub-agents | `SUBTASK:` spawning | No peer-to-peer agent comms |
| Hot reload extensions | Static adapter definitions | No runtime adapter changes |

**Research questions (not implementation):**
1. Should builder agents be able to register new tools during a run?
   (e.g., builder creates a test harness, registers it as a tool for debugger)
2. Should the swarm support "session branching" — a side-quest where an agent
   explores an alternative approach without consuming the main task's context?
3. Is the swarm's rich per-role prompt template (AGENTS.md + code context +
   sibling context + skills) over-specified compared to Pi's minimal approach?

These questions inform future RFC direction but require no code changes now.

## Implementation Tasks

1. Add `AdapterDefinition` for Pi in `SubscriptionCliRoleExecutor.cs`
2. Add `"pi"` to provider fallback mapping in `RoleModelMapping`
3. Verify Pi CLI is installed and probe-able on dev machines
4. Add Pi to default adapter order (after kilo, before local-echo):
   `["copilot", "cline", "kimi", "kilo", "pi", "local-echo"]`
5. Dogfood: submit 1 task with `PreferredAdapter: "pi"`, verify output
6. Document research spike findings in `docs/research/self-extending-agents.md`

## Configuration

```json
{
  "Runtime": {
    "CliAdapterOrder": ["kilo", "pi", "kimi", "", "", ""]
  }
}
```

Or to test Pi specifically:

```json
{
  "Runtime": {
    "CliAdapterOrder": ["pi", "", "", "", "", ""]
  }
}
```

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Adapter first, philosophy later | Ship adapter, research spike separately | Pragmatic — get Pi working in the swarm before redesigning around it |
| `--print` mode | Not interactive/session mode | Matches swarm's subprocess execution model |
| `--thinking` for reasoning | Not `--variant` (kilo pattern) | Pi uses `--thinking` flag natively |
| Research spike not implementation | Document questions, don't code | Self-extending agents is a paradigm shift; needs design before code |

## Out of Scope

- Pi session file parsing (deferred to RFC-013)
- Self-extending agent implementation
- Pi `/control` integration with swarm dispatch
- Pi extension marketplace or sharing

## Open Questions

- Does Pi support `--no-color` or equivalent for clean stdout parsing?
- What are Pi's rejection output patterns (auth failures, rate limits)?
- Can Pi be configured to use a specific provider via env var (like COPILOT_MODEL)?
- Should Pi's session files be stored in `.swarm/memory/` alongside memvid stores?
