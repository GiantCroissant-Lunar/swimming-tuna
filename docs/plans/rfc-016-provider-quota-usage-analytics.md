# RFC-016: Provider Quota Awareness & Usage Analytics

**Status:** Draft
**Date:** 2026-02-27
**Dependencies:** RFC-007 (Budget-Aware Lifecycle), RFC-011 (API-Direct),
  RFC-013 (Agent Hierarchy Model)
**Enhances:** RFC-015 (Multi-Level Tracing) when enrichers provide actual token counts

## Problem

The swarm tracks internal budget per agent (RFC-007) using character-based token
estimation. But it has no knowledge of external provider quotas — how many tokens
remain on the Anthropic API key, whether the rate limit window is about to reset,
or whether one provider has more capacity than another.

Additionally, there's no usage analytics across runs. The gatekeeper can't answer
"how much did RFC-012 cost?" or "are we getting more efficient over time?" without
manual calculation.

The spend side (what we consumed) and the available side (what's left) should
coexist so the runtime can make better dispatch decisions and the team can track
usage improvement.

## Proposal

### 1. Quota Capture from API Responses

When `AnthropicModelProvider` or `OpenAiModelProvider` returns a response, parse
the rate limit headers:

```
x-ratelimit-limit-tokens
x-ratelimit-remaining-tokens
x-ratelimit-limit-requests
x-ratelimit-remaining-requests
x-ratelimit-reset-tokens
```

Store in a new record:

```csharp
namespace SwarmAssistant.Contracts.Quota;

public sealed record ProviderQuota
{
    public required string ProviderId { get; init; }      // "anthropic", "openai"
    public long? LimitTokens { get; init; }
    public long? RemainingTokens { get; init; }
    public long? LimitRequests { get; init; }
    public long? RemainingRequests { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
}
```

For CLI adapters — no quota data initially. When RFC-015 enrichers land, they
can feed actual token counts back, but not provider-level quota.

### 2. ProviderQuotaTracker

Singleton that stores the latest quota snapshot per provider:

```csharp
namespace SwarmAssistant.Runtime.Quota;

internal sealed class ProviderQuotaTracker
{
    private readonly ConcurrentDictionary<string, ProviderQuota> _quotas = new();

    // Called by IModelProvider after each API response
    public void Update(string providerId, ProviderQuota quota);

    // Query current quota for a provider
    public ProviderQuota? Get(string providerId);

    // All providers
    public IReadOnlyDictionary<string, ProviderQuota> GetAll();

    // Convenience: remaining fraction [0.0, 1.0] for dispatch ranking
    // Returns null if no quota data available (CLI adapters)
    public double? GetRemainingFraction(string providerId);
}
```

Called by `IModelProvider.ExecuteAsync()` after each API response — parse headers,
update tracker.

### 3. Quota-Aware Dispatch

Extend `AgentRegistryActor.SelectAgent()` to include quota as a soft signal:

```
Current sort: filter exhausted → prefer healthy budget → cost rank → load
New sort:     filter exhausted → prefer healthy budget → cost rank → quota remaining → load
```

Providers with more remaining quota rank higher. If no quota data is available
(CLI adapters), they sort neutrally — no penalty, no boost.

```csharp
// In SelectAgent() ordering
.OrderBy(pair => ProviderCostRank(pair.Value.Provider?.Type))
.ThenByDescending(pair => _quotaTracker.GetRemainingFraction(
    pair.Value.Provider?.Id ?? "") ?? 0.5)  // neutral default
.ThenBy(pair => pair.Value.CurrentLoad)
```

This is a soft preference — low-quota providers are deprioritized but not blocked
until actually exhausted (consistent with RFC-007's approach).

### 4. BudgetEnvelope Integration

Connect actual token counts to the budget system. When API-direct provides exact
token usage, use it instead of character-based estimation:

```csharp
// Current: estimate from chars
var estimatedTokens = (long)Math.Ceiling(chars / (double)_options.BudgetCharsPerToken);

// New: prefer actual when available
var actualTokens = executionResult.Usage?.InputTokens + executionResult.Usage?.OutputTokens;
var tokens = actualTokens ?? (long)Math.Ceiling(chars / (double)_options.BudgetCharsPerToken);
```

API-direct calls get exact budget tracking. CLI adapters keep the estimation
fallback until RFC-015 enrichers provide actual counts.

### 5. Per-Run Usage Summary

New endpoint `GET /a2a/runs/{runId}/usage` aggregating from `AgentSpanCollector`:

```json
{
  "runId": "run-abc123",
  "title": "RFC-012 Pi Adapter",
  "totalTokens": { "input": 12400, "output": 3200, "total": 15600 },
  "totalCostUsd": 0.042,
  "taskCount": 3,
  "perTask": [
    {
      "taskId": "task-001",
      "title": "Add Pi adapter definition",
      "tokens": { "input": 5200, "output": 1400, "total": 6600 },
      "costUsd": 0.018,
      "roles": {
        "planner": { "tokens": 2100, "adapter": "kilo" },
        "builder": { "tokens": 4500, "adapter": "kilo" }
      }
    }
  ],
  "perProvider": {
    "kilo": { "tokens": 12000, "estimatedCostUsd": 0.0 },
    "api-anthropic": { "tokens": 3600, "costUsd": 0.042 }
  }
}
```

For standalone tasks (no run), same data via `GET /a2a/tasks/{taskId}/usage`.

### 6. Cross-Run Usage Trends

After each run completes, persist a usage summary to
`.swarm/analytics/runs/{runId}.json`. File-based — no ArcadeDB dependency.

New endpoint `GET /a2a/analytics/usage` returns aggregated trends:

```json
{
  "runs": [
    {
      "runId": "run-001",
      "title": "RFC-011",
      "totalTokens": 203000,
      "costUsd": 1.20,
      "taskCount": 3,
      "completedAt": "2026-02-26T22:51:56Z"
    },
    {
      "runId": "run-002",
      "title": "RFC-012",
      "totalTokens": 15600,
      "costUsd": 0.04,
      "taskCount": 2,
      "completedAt": "2026-02-27T23:38:38Z"
    }
  ],
  "totals": { "tokens": 218600, "costUsd": 1.24, "runs": 2, "tasks": 5 },
  "averages": { "tokensPerTask": 43720, "costPerTask": 0.248 }
}
```

Enables "are we getting more efficient?" comparisons across RFCs and over time.

## Implementation Tasks

1. `ProviderQuota` record in Contracts
2. `ProviderQuotaTracker` singleton in Runtime
3. Parse rate limit headers in `AnthropicModelProvider.ExecuteAsync()`
4. Parse rate limit headers in `OpenAiModelProvider.ExecuteAsync()`
5. Extend `AgentRegistryActor.SelectAgent()` with quota-aware soft ranking
6. Update `SwarmAgentActor.ConsumeBudgetForExecution()` to prefer actual tokens
7. `GET /a2a/runs/{runId}/usage` endpoint
8. `GET /a2a/tasks/{taskId}/usage` endpoint
9. Usage summary persistence to `.swarm/analytics/runs/`
10. `GET /a2a/analytics/usage` endpoint with cross-run aggregation
11. Unit tests: ProviderQuotaTracker update/get/remaining fraction
12. Unit tests: SelectAgent with quota ranking
13. Unit tests: actual vs estimated token preference in budget consumption
14. Unit tests: usage summary aggregation
15. Dogfood: run with API-direct, verify quota headers captured
16. Dogfood: compare estimated vs actual token counts

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Rate limit headers first | Not dashboard APIs | Free data from existing API calls; no extra HTTP requests |
| Soft dispatch preference | Not hard thresholds | Consistent with RFC-007 budget approach; no manual tuning |
| File-based analytics | Not ArcadeDB | Simpler; no dependency on optional subsystem |
| Actual tokens preferred | Estimation fallback | API-direct gives exact counts; CLI adapters keep estimation |
| Per-run + cross-run | Not live dashboard | RFC-015/Godot handles live; this RFC handles summaries |

## Out of Scope

- Live cost dashboard / burn rate projection (deferred to RFC-015 Godot integration)
- Provider dashboard API queries (account-level usage from Anthropic/OpenAI consoles)
- Cost allocation policies (charging sub-agent cost to parent budget)
- Budget auto-adjustment based on quota trends
- CLI adapter quota detection (requires adapter-specific solutions)

## Open Questions

- Should usage summaries include wall-clock time per task for efficiency analysis?
- How to handle rate limit resets — should the tracker clear stale data periodically?
- Should `GET /a2a/analytics/usage` support date range filtering?
- Per-provider cost models: hardcode token prices or make configurable?
