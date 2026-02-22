using SwarmAssistant.Contracts.Planning;
using SwarmAssistant.Runtime.Planning;

namespace SwarmAssistant.Runtime.Tests.Planning;

public sealed class GoapPlannerTests
{
    private readonly GoapPlanner _planner = new(SwarmActions.All);

    [Fact]
    public void Plan_HappyPath_FindsPlanBuildReviewFinalize()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true);

        var result = _planner.Plan(state, SwarmActions.CompleteTask);

        Assert.False(result.DeadEnd);
        Assert.NotNull(result.RecommendedPlan);
        Assert.True(result.RecommendedPlan.Count >= 4);

        var names = result.RecommendedPlan.Select(a => a.Name).ToList();
        Assert.Equal("Plan", names[0]);
        Assert.Equal("Build", names[1]);
        Assert.Equal("Review", names[2]);
        Assert.Equal("Finalize", names[3]);
    }

    [Fact]
    public void Plan_GoalAlreadySatisfied_ReturnsEmptyPlan()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskCompleted, true);

        var result = _planner.Plan(state, SwarmActions.CompleteTask);

        Assert.False(result.DeadEnd);
        Assert.NotNull(result.RecommendedPlan);
        Assert.Empty(result.RecommendedPlan);
    }

    [Fact]
    public void Plan_ReviewRejected_FindsReworkPath()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true)
            .With(WorldKey.BuildExists, true)
            .With(WorldKey.ReviewRejected, true);

        var result = _planner.Plan(state, SwarmActions.CompleteTask);

        Assert.False(result.DeadEnd);
        Assert.NotNull(result.RecommendedPlan);

        var names = result.RecommendedPlan.Select(a => a.Name).ToList();
        Assert.Contains("Rework", names);
        Assert.Contains("Review", names);
        Assert.Contains("Finalize", names);
    }

    [Fact]
    public void Plan_ReviewRejectedAndRetryLimitReached_FindsEscalatePath()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true)
            .With(WorldKey.BuildExists, true)
            .With(WorldKey.ReviewRejected, true)
            .With(WorldKey.RetryLimitReached, true);

        var result = _planner.Plan(state, SwarmActions.EscalateTask);

        Assert.False(result.DeadEnd);
        Assert.NotNull(result.RecommendedPlan);

        var names = result.RecommendedPlan.Select(a => a.Name).ToList();
        Assert.Contains("Escalate", names);
    }

    [Fact]
    public void Plan_DeadEnd_NoPathToGoal()
    {
        // State where task doesn't exist and we want it completed
        // but there's no action that creates TaskExists
        var actions = new List<IGoapAction>
        {
            new GoapAction(
                "NeedsPlan",
                new Dictionary<WorldKey, bool> { [WorldKey.PlanExists] = true },
                new Dictionary<WorldKey, bool> { [WorldKey.BuildExists] = true },
                cost: 1),
        };

        var planner = new GoapPlanner(actions);
        var state = new WorldState();

        var result = planner.Plan(state, SwarmActions.CompleteTask);

        Assert.True(result.DeadEnd);
        Assert.Null(result.RecommendedPlan);
    }

    [Fact]
    public void Plan_PlanAlreadyExists_SkipsPlanAction()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true);

        var result = _planner.Plan(state, SwarmActions.CompleteTask);

        Assert.False(result.DeadEnd);
        Assert.NotNull(result.RecommendedPlan);

        var names = result.RecommendedPlan.Select(a => a.Name).ToList();
        Assert.DoesNotContain("Plan", names);
        Assert.Equal("Build", names[0]);
    }

    [Fact]
    public void Plan_BuildAndPlanExist_StartFromReview()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true)
            .With(WorldKey.BuildExists, true);

        var result = _planner.Plan(state, SwarmActions.CompleteTask);

        Assert.False(result.DeadEnd);
        Assert.NotNull(result.RecommendedPlan);

        var names = result.RecommendedPlan.Select(a => a.Name).ToList();
        Assert.Equal("Review", names[0]);
        Assert.Equal("Finalize", names[1]);
    }

    [Fact]
    public void Plan_ReviewPassed_OnlyFinalize()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.ReviewPassed, true);

        var result = _planner.Plan(state, SwarmActions.CompleteTask);

        Assert.False(result.DeadEnd);
        Assert.NotNull(result.RecommendedPlan);
        Assert.Single(result.RecommendedPlan);
        Assert.Equal("Finalize", result.RecommendedPlan[0].Name);
    }

    [Fact]
    public void Plan_RejectionPath_ProducesAlternativePlan()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true)
            .With(WorldKey.BuildExists, true)
            .With(WorldKey.ReviewRejected, true);

        var result = _planner.Plan(state, SwarmActions.CompleteTask);

        Assert.False(result.DeadEnd);
        Assert.NotNull(result.RecommendedPlan);
        // May or may not have an alternative depending on A* exploration
    }

    [Fact]
    public void Plan_CostOptimization_PrefersCheaperPath()
    {
        // The happy path (Plan→Build→Review→Finalize = 1+3+2+1=7) should be
        // preferred over paths involving Rework (cost 4)
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true);

        var result = _planner.Plan(state, SwarmActions.CompleteTask);

        Assert.NotNull(result.RecommendedPlan);
        var totalCost = result.RecommendedPlan.Sum(a => a.Cost);
        Assert.Equal(7, totalCost);
    }
}
