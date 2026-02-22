using SwarmAssistant.Contracts.Planning;
using SwarmAssistant.Runtime.Planning;

namespace SwarmAssistant.Runtime.Tests.Planning;

public sealed class SwarmActionsTests
{
    [Fact]
    public void All_ContainsSixActions()
    {
        Assert.Equal(6, SwarmActions.All.Count);
    }

    [Theory]
    [InlineData("Plan")]
    [InlineData("Build")]
    [InlineData("Review")]
    [InlineData("Rework")]
    [InlineData("Escalate")]
    [InlineData("Finalize")]
    public void All_ContainsAction(string actionName)
    {
        Assert.Contains(SwarmActions.All, a => a.Name == actionName);
    }

    [Fact]
    public void Plan_HasCorrectPreconditionsAndEffects()
    {
        Assert.Single(SwarmActions.Plan.Preconditions);
        Assert.True(SwarmActions.Plan.Preconditions[WorldKey.TaskExists]);

        Assert.Single(SwarmActions.Plan.Effects);
        Assert.True(SwarmActions.Plan.Effects[WorldKey.PlanExists]);

        Assert.Equal(1, SwarmActions.Plan.Cost);
    }

    [Fact]
    public void Rework_ClearsReviewRejected()
    {
        Assert.True(SwarmActions.Rework.Preconditions[WorldKey.ReviewRejected]);
        Assert.False(SwarmActions.Rework.Preconditions[WorldKey.RetryLimitReached]);

        Assert.False(SwarmActions.Rework.Effects[WorldKey.ReviewRejected]);
        Assert.True(SwarmActions.Rework.Effects[WorldKey.ReworkAttempted]);
    }

    [Fact]
    public void Escalate_RequiresRetryLimitReached()
    {
        Assert.True(SwarmActions.Escalate.Preconditions[WorldKey.ReviewRejected]);
        Assert.True(SwarmActions.Escalate.Preconditions[WorldKey.RetryLimitReached]);
        Assert.True(SwarmActions.Escalate.Effects[WorldKey.TaskBlocked]);
    }

    [Fact]
    public void CompleteTask_GoalRequiresTaskCompleted()
    {
        Assert.Single(SwarmActions.CompleteTask.TargetState);
        Assert.True(SwarmActions.CompleteTask.TargetState[WorldKey.TaskCompleted]);
    }

    [Fact]
    public void EscalateTask_GoalRequiresTaskBlocked()
    {
        Assert.Single(SwarmActions.EscalateTask.TargetState);
        Assert.True(SwarmActions.EscalateTask.TargetState[WorldKey.TaskBlocked]);
    }
}
