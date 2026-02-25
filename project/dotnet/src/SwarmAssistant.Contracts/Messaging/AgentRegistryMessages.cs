namespace SwarmAssistant.Contracts.Messaging;

public sealed record AgentHeartbeat(string AgentId);

public sealed record DeregisterAgent(string AgentId);

public sealed record QueryAgents(
    IReadOnlyList<SwarmRole>? Capabilities,
    string? Prefer);

public sealed record QueryAgentsResult(
    IReadOnlyList<AgentRegistryEntry> Agents);

public sealed record AgentRegistryEntry(
    string AgentId,
    string ActorPath,
    IReadOnlyList<SwarmRole> Capabilities,
    int CurrentLoad,
    string? EndpointUrl,
    ProviderInfo? Provider,
    SandboxLevel SandboxLevel,
    BudgetEnvelope? Budget,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastHeartbeat,
    int ConsecutiveFailures,
    CircuitBreakerState CircuitBreakerState);
