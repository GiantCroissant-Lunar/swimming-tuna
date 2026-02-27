namespace SwarmAssistant.Contracts.Hierarchy;

public enum RunSpanStatus
{
    Accepted,
    Decomposing,
    Executing,
    Merging,
    ReadyForPr,
    Done,
    Failed
}

public sealed record RunSpan
{
    public required string RunId { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public RunSpanStatus Status { get; init; } = RunSpanStatus.Accepted;
    public string? FeatureBranch { get; init; }
    public string? BaseBranch { get; init; }
}
