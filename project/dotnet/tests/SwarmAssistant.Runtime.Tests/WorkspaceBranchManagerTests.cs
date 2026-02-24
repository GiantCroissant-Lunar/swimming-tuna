using SwarmAssistant.Runtime.Execution;

namespace SwarmAssistant.Runtime.Tests;

public sealed class WorkspaceBranchManagerTests
{
    [Fact]
    public void BranchName_FromTaskId_ProducesValidGitBranch()
    {
        var result = WorkspaceBranchManager.BranchNameForTask("task-abc-123");

        Assert.Equal("swarm/task-abc-123", result);
    }

    [Fact]
    public void BranchName_SanitizesSpecialChars()
    {
        var result = WorkspaceBranchManager.BranchNameForTask("hello world!@#foo");

        Assert.StartsWith("swarm/", result);
        Assert.DoesNotContain(" ", result);
        Assert.DoesNotContain("!", result);
    }

    [Fact]
    public async Task CreateBranch_WhenDisabled_ReturnsNull()
    {
        var manager = new WorkspaceBranchManager(enabled: false);

        var result = await manager.EnsureBranchAsync("task-abc-123");

        Assert.Null(result);
    }

}
