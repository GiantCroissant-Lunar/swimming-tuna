namespace SwarmAssistant.Runtime.Dto;

public sealed record AgentRegistryEntryDto(
    string AgentId,
    string[] Capabilities,
    string Status,
    ProviderInfoDto? Provider,
    int SandboxLevel,
    BudgetInfoDto? Budget,
    string? EndpointUrl,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastHeartbeat,
    int ConsecutiveFailures,
    string CircuitBreakerState);

public sealed record ProviderInfoDto(
    string Adapter,
    string Type,
    string? Plan);

public sealed record BudgetInfoDto(
    string Type,
    long? TotalTokens,
    long? UsedTokens,
    double? RemainingFraction);
