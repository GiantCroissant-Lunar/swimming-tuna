using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Contracts.Hierarchy;

public enum AgentSpanKind
{
    Coordinator,
    CliAgent,
    ApiAgent,
    Decomposer,
    SubAgent,
    ToolCall
}

public enum SubAgentFlavor
{
    None,
    Normal,
    CoWork
}

public enum AgentSpanStatus
{
    Running,
    Completed,
    Failed,
    TimedOut
}

public sealed record AgentSpan
{
    public required string SpanId { get; init; }
    public string? ParentSpanId { get; init; }
    public required int Level { get; init; }
    public required AgentSpanKind Kind { get; init; }
    public required string TaskId { get; init; }
    public string? RunId { get; init; }
    public required string AgentId { get; init; }
    public string? AdapterId { get; init; }
    public SwarmRole? Role { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public AgentSpanStatus Status { get; init; } = AgentSpanStatus.Running;
    public TokenUsage? Usage { get; init; }
    public decimal? CostUsd { get; init; }
    public SubAgentFlavor Flavor { get; init; } = SubAgentFlavor.None;
}
