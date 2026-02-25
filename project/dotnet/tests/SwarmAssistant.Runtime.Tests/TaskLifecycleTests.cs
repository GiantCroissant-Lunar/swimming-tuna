using SwarmAssistant.Runtime.Actors;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tests;

public sealed class TaskLifecycleTests
{
    [Theory]
    [InlineData(TaskState.Queued, TaskState.Planning)]
    [InlineData(TaskState.Planning, TaskState.Building)]
    [InlineData(TaskState.Building, TaskState.Verifying)]
    [InlineData(TaskState.Verifying, TaskState.Reviewing)]
    [InlineData(TaskState.Reviewing, TaskState.Done)]
    public void Next_SuccessfulProgression_MovesToExpectedStatus(TaskState current, TaskState expected)
    {
        var next = TaskLifecycle.Next(current, success: true);

        Assert.Equal(expected, next);
    }

    [Fact]
    public void Next_FailureAlwaysMovesToBlocked()
    {
        var next = TaskLifecycle.Next(TaskState.Building, success: false);

        Assert.Equal(TaskState.Blocked, next);
    }
}
