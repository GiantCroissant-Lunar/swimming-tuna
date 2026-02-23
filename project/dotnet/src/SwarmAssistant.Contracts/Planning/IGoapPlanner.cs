namespace SwarmAssistant.Contracts.Planning;

public interface IGoapPlanner
{
    GoapPlanResult Plan(IWorldState current, IGoal goal);

    GoapPlanResult Plan(
        IWorldState current,
        IGoal goal,
        IReadOnlyDictionary<string, double>? costAdjustments);
}
