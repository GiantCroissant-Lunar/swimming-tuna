using SwarmAssistant.Contracts.Planning;
using SwarmAssistant.Runtime.Planning;

namespace SwarmAssistant.Runtime.Tests.Planning;

public sealed class VerifyActionTests
{
    private readonly GoapPlanner _planner = new(SwarmActions.All);

    [Fact]
    public void Verify_HasCorrectPreconditionsAndEffects()
    {
        Assert.Single(SwarmActions.Verify.Preconditions);
        Assert.True(SwarmActions.Verify.Preconditions[WorldKey.BuildExists]);

        Assert.Single(SwarmActions.Verify.Effects);
        Assert.True(SwarmActions.Verify.Effects[WorldKey.BuildCompiles]);

        Assert.Equal(SwarmActions.BaseCosts.Verify, SwarmActions.Verify.Cost);
        Assert.Equal("Verify", SwarmActions.Verify.Name);
    }

    [Fact]
    public void Planner_RecommendsVerify_AfterBuildExists()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true)
            .With(WorldKey.BuildExists, true);

        var result = _planner.Plan(state, SwarmActions.CompleteTask);

        Assert.False(result.DeadEnd);
        Assert.NotNull(result.RecommendedPlan);

        var names = result.RecommendedPlan.Select(a => a.Name).ToList();
        Assert.Equal("Verify", names[0]);
    }

    [Fact]
    public void Review_RequiresBuildCompiles_CannotSkipVerify()
    {
        Assert.True(SwarmActions.Review.Preconditions[WorldKey.BuildCompiles]);

        // Create a state where BuildExists is true but BuildCompiles is false
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true)
            .With(WorldKey.BuildExists, true)
            .With(WorldKey.BuildCompiles, false);

        var result = _planner.Plan(state, SwarmActions.CompleteTask);

        Assert.False(result.DeadEnd);
        Assert.NotNull(result.RecommendedPlan);

        var names = result.RecommendedPlan.Select(a => a.Name).ToList();
        // Verify must appear before Review
        var verifyIndex = names.IndexOf("Verify");
        var reviewIndex = names.IndexOf("Review");
        Assert.True(verifyIndex >= 0, "Verify action must be in the plan");
        Assert.True(reviewIndex >= 0, "Review action must be in the plan");
        Assert.True(verifyIndex < reviewIndex, "Verify must come before Review");
    }

    [Fact]
    public void Rework_ResetsBuildCompiles_MustReVerify()
    {
        Assert.True(SwarmActions.Rework.Effects.ContainsKey(WorldKey.BuildCompiles));
        Assert.False(SwarmActions.Rework.Effects[WorldKey.BuildCompiles]);

        // Simulate a state after rework: build exists, BuildCompiles reset, review rejection cleared
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true)
            .With(WorldKey.BuildExists, true)
            .With(WorldKey.BuildCompiles, false)
            .With(WorldKey.ReworkAttempted, true);

        var result = _planner.Plan(state, SwarmActions.CompleteTask);

        Assert.False(result.DeadEnd);
        Assert.NotNull(result.RecommendedPlan);

        var names = result.RecommendedPlan.Select(a => a.Name).ToList();
        Assert.Contains("Verify", names);
        Assert.Contains("Review", names);
    }

    [Fact]
    public void FullPlan_PlanBuildVerifyReviewFinalize()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true);

        var result = _planner.Plan(state, SwarmActions.CompleteTask);

        Assert.False(result.DeadEnd);
        Assert.NotNull(result.RecommendedPlan);
        Assert.Equal(5, result.RecommendedPlan.Count);

        var names = result.RecommendedPlan.Select(a => a.Name).ToList();
        Assert.Equal("Plan", names[0]);
        Assert.Equal("Build", names[1]);
        Assert.Equal("Verify", names[2]);
        Assert.Equal("Review", names[3]);
        Assert.Equal("Finalize", names[4]);
    }

    [Fact]
    public void Verify_AlreadyCompiles_SkipsVerify()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true)
            .With(WorldKey.BuildExists, true)
            .With(WorldKey.BuildCompiles, true);

        var result = _planner.Plan(state, SwarmActions.CompleteTask);

        Assert.False(result.DeadEnd);
        Assert.NotNull(result.RecommendedPlan);

        var names = result.RecommendedPlan.Select(a => a.Name).ToList();
        Assert.DoesNotContain("Verify", names);
        Assert.Equal("Review", names[0]);
        Assert.Equal("Finalize", names[1]);
    }
}
