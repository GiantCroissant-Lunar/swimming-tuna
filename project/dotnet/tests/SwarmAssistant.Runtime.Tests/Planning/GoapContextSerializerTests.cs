using SwarmAssistant.Contracts.Planning;
using SwarmAssistant.Runtime.Planning;

namespace SwarmAssistant.Runtime.Tests.Planning;

public sealed class GoapContextSerializerTests
{
    [Fact]
    public void Serialize_HappyPathPlan_IncludesRecommendedPlan()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true);

        var planner = new GoapPlanner(SwarmActions.All);
        var result = planner.Plan(state, SwarmActions.CompleteTask);

        var output = GoapContextSerializer.Serialize(state, result);

        Assert.Contains("GOAP Analysis:", output);
        Assert.Contains("Recommended plan:", output);
        Assert.Contains("Plan", output);
        Assert.Contains("Build", output);
        Assert.Contains("total cost:", output);
    }

    [Fact]
    public void Serialize_DeadEnd_IndicatesNoPath()
    {
        var state = new WorldState();
        var result = new GoapPlanResult(null, null, DeadEnd: true);

        var output = GoapContextSerializer.Serialize(state, result);

        Assert.Contains("dead end", output);
        Assert.Contains("Dead end: true", output);
    }

    [Fact]
    public void Serialize_IncludesWorldState()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true);

        var result = new GoapPlanResult(
            Array.Empty<IGoapAction>(), null, DeadEnd: false);

        var output = GoapContextSerializer.Serialize(state, result);

        Assert.Contains("Current world state:", output);
        Assert.Contains("TaskExists=true", output);
        Assert.Contains("PlanExists=true", output);
    }

    [Fact]
    public void Serialize_GoalSatisfied_ShowsGoalAlreadySatisfied()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskCompleted, true);

        var result = new GoapPlanResult(
            Array.Empty<IGoapAction>(), null, DeadEnd: false);

        var output = GoapContextSerializer.Serialize(state, result);

        Assert.Contains("goal already satisfied", output);
    }
}
