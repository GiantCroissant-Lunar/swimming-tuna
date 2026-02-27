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

    [Fact]
    public async Task EnsureWorktree_WhenDisabled_ReturnsNull()
    {
        var manager = new WorkspaceBranchManager(enabled: false, worktreeIsolation: true);

        var result = await manager.EnsureWorktreeAsync("task-abc-123");

        Assert.Null(result);
    }

    [Fact]
    public async Task EnsureWorktree_WhenWorktreeIsolationDisabled_ReturnsNull()
    {
        var manager = new WorkspaceBranchManager(enabled: true, worktreeIsolation: false);

        var result = await manager.EnsureWorktreeAsync("task-abc-123");

        Assert.Null(result);
    }

    [Fact]
    public async Task EnsureWorktree_WithParentBranch_WhenDisabled_ReturnsNull()
    {
        var manager = new WorkspaceBranchManager(enabled: false, worktreeIsolation: true);

        var result = await manager.EnsureWorktreeAsync("task-abc-123", "main");

        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveWorktree_WhenDisabled_DoesNotThrow()
    {
        var manager = new WorkspaceBranchManager(enabled: false, worktreeIsolation: true);

        await manager.RemoveWorktreeAsync("task-abc-123");
    }

    [Fact]
    public async Task RemoveWorktree_WhenWorktreeIsolationDisabled_DoesNotThrow()
    {
        var manager = new WorkspaceBranchManager(enabled: true, worktreeIsolation: false);

        await manager.RemoveWorktreeAsync("task-abc-123");
    }

    [Fact]
    public async Task CleanupBranch_WhenDisabled_DoesNotThrow()
    {
        var manager = new WorkspaceBranchManager(enabled: false);

        await manager.CleanupBranchAsync("task-abc-123");
    }

    [Fact]
    public async Task CleanupBranch_WhenEnabled_DoesNotThrow()
    {
        // Branch doesn't exist but method should handle gracefully
        var manager = new WorkspaceBranchManager(enabled: true);

        await manager.CleanupBranchAsync("task-nonexistent-xyz");
    }

    [Fact]
    public async Task MergeTaskBranch_WhenBranchNotFound_ReturnsBranchNotFound()
    {
        var manager = new WorkspaceBranchManager(enabled: true);

        var result = await manager.MergeTaskBranchAsync("task-nonexistent-xyz", "main");

        Assert.Equal(MergeResult.BranchNotFound, result);
    }

    [Fact]
    public void MergeResult_HasExpectedValues()
    {
        Assert.Equal(0, (int)MergeResult.Success);
        Assert.Equal(1, (int)MergeResult.Conflict);
        Assert.Equal(2, (int)MergeResult.BranchNotFound);
        Assert.Equal(3, (int)MergeResult.CheckoutFailed);
    }
}
