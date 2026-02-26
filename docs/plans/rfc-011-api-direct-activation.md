# RFC-011: API-Direct Activation & Native Providers

**Status:** Draft
**Date:** 2026-02-26
**Dependencies:** RFC-010 (Provider Abstraction), RFC-007 (Budget-Aware Lifecycle)
**Extends:** RFC-010 Phase 2

## Problem

RFC-010 laid the foundation: `IModelProvider`, `ModelSpec`, `OpenAiModelProvider`, and the
`api-direct`/`hybrid` execution modes. But dogfood still uses `subscription-cli-fallback`
exclusively. The API-direct path has never been tested end-to-end in a real run.

Gaps:
1. **Only one provider** — `OpenAiModelProvider` exists, but no native Anthropic provider
2. **Hybrid mode untested** — the mode switch exists but has no dogfood validation
3. **Token reporting incomplete** — `api-direct` sets OTLP tags but Langfuse still shows
   no LLM request/response data (P1 known issue)
4. **No provider probing in startup** — the runtime doesn't verify API keys are valid at boot

## Proposal

### 1. AnthropicModelProvider

Native implementation of `IModelProvider` for Anthropic's Messages API:

```csharp
internal sealed class AnthropicModelProvider : IModelProvider, IDisposable
{
    public string ProviderId => "anthropic";

    public async Task<bool> ProbeAsync(CancellationToken ct)
    {
        // POST /v1/messages with a minimal prompt to verify API key
        // or GET /v1/models if available
    }

    public async Task<ModelResponse> ExecuteAsync(
        ModelSpec model, string prompt,
        ModelExecutionOptions options, CancellationToken ct)
    {
        // POST https://api.anthropic.com/v1/messages
        // Headers: x-api-key, anthropic-version: 2023-06-01
        // Body: { model, messages: [{ role: "user", content: prompt }],
        //         max_tokens, thinking?: { type: "enabled", budget_tokens } }
        // Map response.usage → TokenUsage
    }
}
```

Key differences from `OpenAiModelProvider`:
- Auth via `x-api-key` header (not Bearer token)
- Extended thinking via `thinking` block (not `reasoning_effort` string)
- Response shape: `content[].text` (not `choices[].message.content`)
- Cache tokens: `cache_creation_input_tokens` + `cache_read_input_tokens`

### 2. Provider Registration & Probing

At startup, resolve providers from `Runtime:ApiProviderOrder`:

```csharp
// In AgentFrameworkRoleEngine or a dedicated ProviderRegistry
private IReadOnlyList<IModelProvider> BuildProviders()
{
    var providers = new List<IModelProvider>();
    foreach (var id in _options.ApiProviderOrder)
    {
        IModelProvider? provider = id switch
        {
            "openai" => BuildOpenAiProvider(),
            "anthropic" => BuildAnthropicProvider(),
            _ => null
        };
        if (provider is not null) providers.Add(provider);
    }
    return providers;
}
```

Probe all registered providers at startup and log results:

```
[INFO] Provider openai: probed OK (api.openai.com/v1)
[INFO] Provider anthropic: probed OK (api.anthropic.com/v1)
[WARN] Provider moonshot: skipped (no API key)
```

### 3. Activate Hybrid Mode for Dogfood

Update `appsettings.Dogfood.json`:

```json
{
  "Runtime": {
    "AgentFrameworkExecutionMode": "hybrid",
    "ApiProviderOrder": ["anthropic", "openai"],
    "RoleModelMapping": {
      "Planner": { "Model": "anthropic/claude-sonnet-4-6", "Reasoning": "high" },
      "Reviewer": { "Model": "anthropic/claude-haiku-4-5", "Reasoning": "low" }
    },
    "CliAdapterOrder": ["kilo", "kimi", "", "", ""]
  }
}
```

Hybrid dispatch logic (already stubbed in `ExecuteHybridAsync`):
1. Look up `RoleModelMapping[role]`
2. If mapping exists and provider is registered → `api-direct`
3. Otherwise → `subscription-cli-fallback`

This means: Planner and Reviewer use Anthropic API directly, Builder falls through to kilo CLI.

### 4. End-to-End Token Reporting

For `api-direct` calls, enrich the OTLP span with full token data:

```csharp
// Already partially implemented — ensure these tags are set:
activity?.SetTag("gen_ai.usage.input_tokens", response.Usage.InputTokens);
activity?.SetTag("gen_ai.usage.output_tokens", response.Usage.OutputTokens);
activity?.SetTag("gen_ai.usage.cache_read_tokens", response.Usage.CacheReadTokens);
activity?.SetTag("gen_ai.usage.cache_write_tokens", response.Usage.CacheWriteTokens);
activity?.SetTag("gen_ai.response.model", response.ModelId);
activity?.SetTag("gen_ai.usage.cost_usd", CalculateCostUsd(model, response.Usage));

// NEW: attach request/response for Langfuse LLM observation
activity?.SetTag("gen_ai.request.messages", SerializePrompt(prompt));
activity?.SetTag("gen_ai.response.content", response.Output);
```

This resolves the P1: "Langfuse shows no LLM request/response JSON" for API-direct calls.
CLI adapter calls remain estimation-only until RFC-013 adds structured output parsing.

### 5. Budget Integration

Feed real token counts from `api-direct` into RFC-007's `BudgetEnvelope`:

```csharp
// After api-direct execution
var tokenDelta = response.Usage.InputTokens + response.Usage.OutputTokens;
budgetTracker.RecordUsage(agentId, tokenDelta, response.Usage);
```

Currently budget tracking estimates tokens for CLI calls. API-direct gives exact numbers.

## Implementation Tasks

1. `AnthropicModelProvider` — native Messages API client
2. Provider startup probing with logged results
3. API key resolution: `Runtime:AnthropicApiKeyEnvVar` → env var lookup
4. Activate `hybrid` mode in dogfood config
5. End-to-end token reporting: request/response content in OTLP spans
6. Budget integration: feed real `TokenUsage` into `BudgetEnvelope`
7. Dogfood validation: submit 2 tasks, verify Planner uses API, Builder uses CLI

## Configuration

```json
{
  "Runtime": {
    "ApiProviderOrder": ["anthropic", "openai"],
    "AnthropicApiKeyEnvVar": "ANTHROPIC_API_KEY",  // pragma: allowlist secret
    "AnthropicBaseUrl": "https://api.anthropic.com/v1",
    "OpenAiApiKeyEnvVar": "OPENAI_API_KEY",  // pragma: allowlist secret
    "OpenAiBaseUrl": "https://api.openai.com/v1"
  }
}
```

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Native Anthropic provider | Not OpenAI-compat proxy | Extended thinking, cache tokens, and proper auth need native API |
| Hybrid as default dogfood mode | Not full api-direct | CLI adapters are free; use API only where model control matters |
| Probe at startup | Not lazy probe | Fail fast if API keys are missing/invalid |
| Request/response in OTLP | Not separate logging | Langfuse can render LLM observations from gen_ai.* tags |

## Out of Scope

- Google/Gemini provider (future extension via `IModelProvider`)
- Streaming API responses (batch mode sufficient for role execution)
- Multi-turn agent loops via API (single prompt→response per role)
- Custom model hosting (Ollama, vLLM) — separate RFC if needed

## Open Questions

- Should extended thinking budget be a fixed token count or percentage of role budget?
- For hybrid mode failures, should API errors fall back to CLI or fail the role?
- Max prompt size limits per provider (Anthropic 200k context vs OpenAI 128k)?
