using SwarmAssistant.Runtime.Actors;

namespace SwarmAssistant.Runtime.Tests;

public sealed class GlobalBlackboardKeysTests
{
    [Fact]
    public void TaskAvailable_FormatsKeyCorrectly()
    {
        var result = GlobalBlackboardKeys.TaskAvailable("task-01");
        Assert.Equal("task.available:task-01", result);
    }

    [Fact]
    public void TaskClaimed_FormatsKeyCorrectly()
    {
        var result = GlobalBlackboardKeys.TaskClaimed("task-01");
        Assert.Equal("task.claimed:task-01", result);
    }

    [Fact]
    public void TaskComplete_FormatsKeyCorrectly()
    {
        var result = GlobalBlackboardKeys.TaskComplete("task-01");
        Assert.Equal("task.complete:task-01", result);
    }

    [Fact]
    public void ArtifactProduced_FormatsKeyCorrectly()
    {
        var result = GlobalBlackboardKeys.ArtifactProduced("task-01");
        Assert.Equal("artifact.produced:task-01", result);
    }

    [Fact]
    public void HelpNeeded_FormatsKeyCorrectly()
    {
        var result = GlobalBlackboardKeys.HelpNeeded("agent-01");
        Assert.Equal("help.needed:agent-01", result);
    }
}
