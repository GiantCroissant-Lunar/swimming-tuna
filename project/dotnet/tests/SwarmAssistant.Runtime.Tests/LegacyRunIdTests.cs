using SwarmAssistant.Runtime.Tasks;

namespace SwarmAssistant.Runtime.Tests;

public sealed class LegacyRunIdTests
{
    [Fact]
    public void Resolve_WhenRunIdIsNull_ReturnsSyntheticId()
    {
        var result = LegacyRunId.Resolve(null, "task-abc");

        Assert.Equal("legacy-task-abc", result);
    }

    [Fact]
    public void Resolve_WhenRunIdIsEmpty_ReturnsSyntheticId()
    {
        var result = LegacyRunId.Resolve(string.Empty, "task-xyz");

        Assert.Equal("legacy-task-xyz", result);
    }

    [Fact]
    public void Resolve_WhenRunIdIsWhitespace_ReturnsSyntheticId()
    {
        var result = LegacyRunId.Resolve("   ", "task-ws");

        Assert.Equal("legacy-task-ws", result);
    }

    [Fact]
    public void Resolve_WhenRunIdIsPresent_ReturnsRunIdUnchanged()
    {
        var result = LegacyRunId.Resolve("run-existing-42", "task-abc");

        Assert.Equal("run-existing-42", result);
    }

    [Fact]
    public void Resolve_IsDeterministic_SameInputGivesSameOutput()
    {
        var first = LegacyRunId.Resolve(null, "task-determinism");
        var second = LegacyRunId.Resolve(null, "task-determinism");

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(null, "task-1", "legacy-task-1")]
    [InlineData("", "task-2", "legacy-task-2")]
    [InlineData("run-real", "task-3", "run-real")]
    public void Resolve_ParameterisedCases(string? runId, string taskId, string expected)
    {
        Assert.Equal(expected, LegacyRunId.Resolve(runId, taskId));
    }

    [Fact]
    public void Prefix_IsLegacyDash()
    {
        Assert.Equal("legacy-", LegacyRunId.Prefix);
    }
}
