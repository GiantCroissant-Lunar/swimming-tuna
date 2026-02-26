namespace SwarmAssistant.Runtime.Execution;

public sealed record ModelSpec
{
    public required string Id { get; init; }
    public required string Provider { get; init; }
    public string? DisplayName { get; init; }
    public ModelCapabilities Capabilities { get; init; } = new();
    public ModelCost Cost { get; init; } = new();
}

public sealed record ModelCapabilities
{
    public bool Reasoning { get; init; }
    public string[] InputModalities { get; init; } = ["text"];
    public int ContextWindow { get; init; } = 200_000;
    public int MaxOutputTokens { get; init; } = 8_192;
}

public sealed record ModelCost
{
    public decimal InputPerMillionTokens { get; init; }
    public decimal OutputPerMillionTokens { get; init; }
    public decimal CacheReadPerMillionTokens { get; init; }
    public string CostType { get; init; } = "subscription";
}

public sealed record RoleModelPreference
{
    public string? Model { get; init; }
    public string? Reasoning { get; init; }
}

public sealed record ModelExecutionOptions
{
    public string? Reasoning { get; init; }
    public string? Mode { get; init; }
}

public sealed record ModelResponse
{
    public required string Output { get; init; }
    public required TokenUsage Usage { get; init; }
    public string? ModelId { get; init; }
    public TimeSpan Latency { get; init; }
}

public sealed record TokenUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CacheReadTokens { get; init; }
    public int CacheWriteTokens { get; init; }
}
