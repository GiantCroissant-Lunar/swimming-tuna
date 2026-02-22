namespace SwarmAssistant.Contracts.Planning;

public sealed record GoapPlanResult(
    IReadOnlyList<IGoapAction>? RecommendedPlan,
    IReadOnlyList<IGoapAction>? AlternativePlan,
    bool DeadEnd
);
