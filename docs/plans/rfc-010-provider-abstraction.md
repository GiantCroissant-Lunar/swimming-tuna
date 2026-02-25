# RFC-010: Provider Abstraction & Model-Aware Execution

**Status:** Draft
**Date:** 2026-02-25
**Dependencies:** RFC-004 (Agent Registry), RFC-006 (Langfuse Replay), RFC-007 (Budget-Aware Lifecycle)
**Implements:** Multi-agent evolution Phase D

## Problem

SwarmAssistant's CLI adapter abstraction hides which AI model is executing each role.
The `AdapterDefinition` record knows the command to run (`copilot`, `cline`, `kimi`)
but not the model behind it. This creates several gaps:

1. **No model selection per role** — cannot say "use Claude Sonnet for planning, GPT-4o
   for building"
2. **No token reporting** — CLI adapters don't report token usage, so RFC-007 budget
   enforcement is blind
3. **No cost tracking** — Langfuse spans lack model/token data for CLI-executed roles
4. **No reasoning control** — cannot adjust thinking depth per role/task complexity

Meanwhile, CLI tools already support model and mode flags:

| CLI | Model flag | Mode/Phase | Reasoning |
|-----|-----------|------------|-----------|
| Copilot | `COPILOT_MODEL` env, profile-based | (default) | `COPILOT_REASONING_EFFORT` (low/medium/high/xhigh) |
| Cline | `--model` | `--oneshot`, plan/act in VSCode | (via model) |
| Claude Code | `--model` | (integrated plan+act) | `--thinking` level |
| Aider | `--model`, `--architect-model` | architect/code split | (via model) |
| Pi-agent | `--provider`, `--model`, pattern globs | (via extensions) | `--thinking` (off/minimal/low/medium/high/xhigh) |

And pi-agent demonstrates a clean provider registry that separates model identity from
execution mechanism.

## Proposal

### 1. Model as First-Class Entity

Add a `ModelSpec` type that describes an AI model independently of how it's invoked:

```csharp
public sealed record ModelSpec
{
    public required string Id { get; init; }           // "claude-sonnet-4-6", "gpt-4o"
    public required string Provider { get; init; }     // "anthropic", "openai", "google"
    public string? DisplayName { get; init; }          // "Claude Sonnet 4.6"
    public ModelCapabilities Capabilities { get; init; } = new();
    public ModelCost Cost { get; init; } = new();
}

public sealed record ModelCapabilities
{
    public bool Reasoning { get; init; }               // Extended thinking support
    public string[] InputModalities { get; init; } = ["text"];
    public int ContextWindow { get; init; } = 200_000;
    public int MaxOutputTokens { get; init; } = 8_192;
}

public sealed record ModelCost
{
    public decimal InputPerMillionTokens { get; init; }    // USD
    public decimal OutputPerMillionTokens { get; init; }
    public decimal CacheReadPerMillionTokens { get; init; }
    public string CostType { get; init; } = "per-token";  // "per-token", "subscription", "free"
}
```

### 2. Extended Adapter Definition

Evolve `AdapterDefinition` to support model and mode passthrough:

```csharp
private sealed record AdapterDefinition(
    // ... existing fields ...
    string Id,
    string ProbeCommand,
    string[] ProbeArgs,
    string ExecuteCommand,
    string[] ExecuteArgs,
    string[] RejectOutputSubstrings,
    bool IsInternal = false,

    // NEW: model/mode support
    string? ModelFlag { get; init; }           // "--model" or null
    string? ModelEnvVar { get; init; }         // "COPILOT_MODEL" or null
    string? ModeFlag { get; init; }            // "--mode" or null
    string? ReasoningFlag { get; init; }       // "--thinking" or null
    string? ReasoningEnvVar { get; init; }     // "COPILOT_REASONING_EFFORT" or null
    bool SupportsTokenReporting { get; init; } // Can parse token usage from output
    string? TokenOutputPattern { get; init; }  // Regex to extract token counts
);
```

Updated adapter definitions:

```csharp
["copilot"] = new(
    // ... existing ...
    ModelFlag: null,                          // Uses env var instead
    ModelEnvVar: "COPILOT_MODEL",
    ModeFlag: null,
    ReasoningFlag: null,
    ReasoningEnvVar: "COPILOT_REASONING_EFFORT",
    SupportsTokenReporting: false,
    TokenOutputPattern: null
),

["cline"] = new(
    // ... existing ...
    ModelFlag: "--model",
    ModelEnvVar: null,
    ModeFlag: null,
    ReasoningFlag: null,
    ReasoningEnvVar: null,
    SupportsTokenReporting: false,
    TokenOutputPattern: null
),
```

### 3. Role-to-Model Mapping

Allow configuration of which model to use per role:

```json
{
  "Runtime": {
    "RoleModelMapping": {
      "Planner": { "model": "claude-sonnet-4-6", "reasoning": "high" },
      "Builder": { "model": "claude-sonnet-4-6", "reasoning": "medium" },
      "Reviewer": { "model": "claude-haiku-4-5", "reasoning": "low" },
      "Orchestrator": { "model": "claude-haiku-4-5", "reasoning": "low" },
      "Debugger": { "model": "claude-sonnet-4-6", "reasoning": "high" }
    }
  }
}
```

When executing a role, the executor resolves:
1. Check `RoleModelMapping[role]` for model preference
2. Find adapters that support the requested model (or pass it as a flag)
3. Set reasoning level via flag or env var
4. Execute with model/reasoning context

### 4. Three Execution Modes

```
subscription-cli-fallback   ← existing: copilot/cline/kimi subprocess
api-direct                  ← NEW: direct LLM API calls with API keys
hybrid                      ← NEW: subscription for cheap roles, API for specific models
```

#### API-Direct Mode

For roles that need precise model control, token reporting, or specific capabilities:

```csharp
public interface IModelProvider
{
    string ProviderId { get; }                     // "anthropic", "openai"
    Task<bool> ProbeAsync(CancellationToken ct);   // Validate API key
    Task<ModelResponse> ExecuteAsync(
        ModelSpec model,
        string prompt,
        ModelExecutionOptions options,
        CancellationToken ct);
}

public sealed record ModelResponse
{
    public required string Output { get; init; }
    public required TokenUsage Usage { get; init; }
    public string? ModelId { get; init; }          // Actual model used (may differ from requested)
    public TimeSpan Latency { get; init; }
}

public sealed record TokenUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CacheReadTokens { get; init; }
    public int CacheWriteTokens { get; init; }
}
```

Providers register at startup:

```csharp
services.AddSingleton<IModelProvider>(new AnthropicProvider(apiKey));
services.AddSingleton<IModelProvider>(new OpenAIProvider(apiKey));
```

#### Hybrid Mode

The executor tries in order:
1. If `RoleModelMapping[role]` specifies a model and an API provider is available → api-direct
2. Otherwise → subscription-cli-fallback with model hint via env/flag

This lets you use free subscription CLIs for most work, but switch to API for
tasks needing specific models (e.g., Opus for complex planning).

### 5. Token Reporting to Langfuse

With api-direct mode, token usage is known precisely:

```csharp
// After execution, enrich the Langfuse span
activity?.SetTag("gen_ai.usage.input_tokens", response.Usage.InputTokens);
activity?.SetTag("gen_ai.usage.output_tokens", response.Usage.OutputTokens);
activity?.SetTag("gen_ai.response.model", response.ModelId);
activity?.SetTag("gen_ai.usage.cost_usd", CalculateCost(model, response.Usage));
```

For CLI adapters that don't report tokens, estimate from prompt length:

```csharp
activity?.SetTag("gen_ai.usage.input_tokens_estimated", EstimateTokens(prompt));
activity?.SetTag("gen_ai.usage.output_tokens_estimated", EstimateTokens(output));
activity?.SetTag("gen_ai.cost_source", "estimated");
```

### 6. Agent Card Integration

The agent card (RFC-001) gains model awareness:

```json
{
  "agentId": "builder-01",
  "capabilities": ["build", "code-generation"],
  "provider": {
    "adapter": "copilot",
    "type": "subscription",
    "model": "claude-sonnet-4-6",
    "reasoning": "medium"
  },
  "costModel": {
    "type": "subscription",
    "ratePerMillionTokens": 0
  }
}
```

This feeds into RFC-004 registry queries:

```
GET /a2a/registry/agents?capability=build&prefer=cheapest
→ Returns agents sorted by costModel.ratePerMillionTokens
→ Subscription agents ($0) rank above API agents
```

## Pi-Agent Patterns Adopted

| Pi-agent pattern | Adaptation |
|-----------------|------------|
| Provider registry (`api-registry.ts`) | `IModelProvider` interface + DI registration |
| Model type with capabilities + cost | `ModelSpec` record |
| Stream abstraction (simple + full) | `IModelProvider.ExecuteAsync` (non-streaming for CLI compat) |
| Model resolver with pattern matching | `RoleModelMapping` config (simpler, role-based) |
| Dynamic API key resolution | `IApiKeyResolver` for env var + secrets |

## CLI Mode Mapping

For adapters that support execution modes, map SwarmRole to CLI-native phases:

```csharp
private static string? ResolveCliMode(SwarmRole role, AdapterDefinition adapter)
{
    if (adapter.ModeFlag is null) return null;

    return role switch
    {
        SwarmRole.Planner => "plan",        // Read-only analysis
        SwarmRole.Builder => "act",         // Write code
        SwarmRole.Reviewer => "plan",       // Read-only analysis
        SwarmRole.Researcher => "plan",
        SwarmRole.Debugger => "act",        // Need to test fixes
        SwarmRole.Tester => "act",          // Need to write tests
        SwarmRole.Orchestrator => "plan",
        _ => null
    };
}
```

For aider-style architect/code split:

```csharp
// Aider adapter (future)
["aider"] = new(
    ...
    ModelFlag: "--model",
    ModeFlag: null,                // Aider uses --architect-model instead
    // For planner role: use --architect-model
    // For builder role: use --model (code mode)
)
```

## Implementation Tasks

1. `ModelSpec`, `ModelCapabilities`, `ModelCost` record types
2. Extended `AdapterDefinition` with model/mode/reasoning fields
3. `RoleModelMapping` configuration in `RuntimeOptions`
4. Model/mode/reasoning passthrough in `SubscriptionCliRoleExecutor`
5. `IModelProvider` interface + `AnthropicProvider` implementation
6. `OpenAIProvider` implementation
7. `IApiKeyResolver` (env var lookup, future: secrets manager)
8. Hybrid execution mode in `AgentFrameworkRoleEngine`
9. Token usage enrichment in Langfuse spans
10. Agent card model field + registry query integration

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| API-direct as separate mode | Not replacing CLI adapters | CLI adapters are free (subscription); API costs money |
| Role-based model mapping | Not per-task | Roles have predictable complexity; simpler config |
| Non-streaming API calls | Match CLI adapter interface | Streaming adds complexity without value for batch roles |
| Token estimation for CLI | Rough char/4 estimate | Better than nothing for budget tracking |

## Out of Scope

- Model fine-tuning or LoRA adapters
- Multi-modal input (images, audio) for agents
- Real-time streaming to dashboard during execution
- Custom model hosting (Ollama, vLLM) — future extension via `IModelProvider`

## Open Questions

- Should API keys be stored in RuntimeOptions or a separate secrets config?
- For hybrid mode, should the user or the system decide when to use API vs CLI?
- How to handle models that don't exist on a given provider (graceful fallback)?
- Should model selection be part of the contract-net bidding (agents bid with their model)?
