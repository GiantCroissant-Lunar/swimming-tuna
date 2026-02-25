using SwarmAssistant.Runtime.Execution;

namespace SwarmAssistant.Runtime.Tests;

public sealed class ContainerLifecycleManagerTests
{
    [Fact]
    public void BuildRunArgs_IncludesWorkspaceMount()
    {
        var args = ContainerLifecycleManager.BuildRunArgs(
            "test-image",
            "/host/workspace",
            1.0,
            "512m",
            30);

        Assert.Contains("-v", args);
        var volumeIndex = Array.IndexOf(args, "-v");
        Assert.True(volumeIndex >= 0);
        Assert.Equal("/host/workspace:/workspace:rw", args[volumeIndex + 1]);
    }

    [Fact]
    public void BuildRunArgs_IncludesResourceLimits()
    {
        var args = ContainerLifecycleManager.BuildRunArgs(
            "test-image",
            "/workspace",
            2.5,
            "1g",
            60);

        Assert.Contains("--cpus=2.5", args);
        Assert.Contains("--memory=1g", args);
        Assert.Contains("--stop-timeout=60", args);
    }

    [Fact]
    public void BuildRunArgs_IncludesAutoRemove()
    {
        var args = ContainerLifecycleManager.BuildRunArgs(
            "test-image",
            "/workspace",
            1.0,
            "512m",
            30);

        Assert.Contains("--rm", args);
    }

    [Fact]
    public void BuildRunArgs_IncludesImageName()
    {
        var args = ContainerLifecycleManager.BuildRunArgs(
            "my-custom-image",
            "/workspace",
            1.0,
            "512m",
            30);

        Assert.Contains("my-custom-image", args);
        Assert.Equal("my-custom-image", args[^1]);
    }
}
