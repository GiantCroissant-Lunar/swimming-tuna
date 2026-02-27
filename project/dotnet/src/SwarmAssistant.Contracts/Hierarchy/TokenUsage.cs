namespace SwarmAssistant.Contracts.Hierarchy;

public sealed record TokenUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int? ReasoningTokens { get; init; }
    public int? CacheReadTokens { get; init; }
    public int? CacheWriteTokens { get; init; }
}
