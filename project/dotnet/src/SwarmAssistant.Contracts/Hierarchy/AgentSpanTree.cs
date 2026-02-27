namespace SwarmAssistant.Contracts.Hierarchy;

public sealed record AgentSpanTree
{
    public required AgentSpan Span { get; init; }
    public IReadOnlyList<AgentSpanTree> Children { get; init; } = [];
}
