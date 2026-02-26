using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Execution;

namespace SwarmAssistant.Runtime.Tests;

public sealed class SubscriptionCliRoleExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_LocalEchoOnly_ReturnsDeterministicOutput()
    {
        var options = new RuntimeOptions
        {
            CliAdapterOrder = ["local-echo"],
            SandboxMode = "host"
        };

        using var loggerFactory = LoggerFactory.Create(builder => { });
        var executor = new SubscriptionCliRoleExecutor(options, loggerFactory);

        var result = await executor.ExecuteAsync(
            new ExecuteRoleTask(
                "task-1",
                SwarmRole.Planner,
                "Implement CLI routing",
                "Use subscription CLIs with fallback",
                null,
                null),
            CancellationToken.None);

        Assert.Equal("local-echo", result.AdapterId);
        Assert.Contains("[LocalEcho/Planner]", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_NoKnownAdapters_Throws()
    {
        var options = new RuntimeOptions
        {
            CliAdapterOrder = ["unknown-adapter"],
            SandboxMode = "host"
        };

        using var loggerFactory = LoggerFactory.Create(builder => { });
        var executor = new SubscriptionCliRoleExecutor(options, loggerFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(
                new ExecuteRoleTask(
                    "task-1",
                    SwarmRole.Builder,
                    "Implement CLI routing",
                    "Use subscription CLIs with fallback",
                    "plan-output",
                    null),
                CancellationToken.None));

        Assert.Contains("No CLI adapter succeeded", exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WithWorkspacePath_ReturnsOutput()
    {
        var options = new RuntimeOptions
        {
            CliAdapterOrder = ["local-echo"],
            SandboxMode = "host"
        };

        using var loggerFactory = LoggerFactory.Create(builder => { });
        var executor = new SubscriptionCliRoleExecutor(options, loggerFactory);

        var result = await executor.ExecuteAsync(
            new ExecuteRoleTask(
                "task-worktree-1",
                SwarmRole.Builder,
                "Build feature",
                "Build in isolated worktree",
                "plan-output",
                null,
                WorkspacePath: "/tmp/worktree-test"),
            CancellationToken.None);

        Assert.Equal("local-echo", result.AdapterId);
        Assert.Contains("[LocalEcho/Builder]", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_WithRoleModelMapping_ResolvesModelForRole()
    {
        var options = new RuntimeOptions
        {
            CliAdapterOrder = ["local-echo"],
            SandboxMode = "host",
            RoleModelMapping = new Dictionary<string, RoleModelPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["Planner"] = new()
                {
                    Model = "anthropic/claude-sonnet-4-6",
                    Reasoning = "high"
                }
            }
        };

        using var loggerFactory = LoggerFactory.Create(builder => { });
        var executor = new SubscriptionCliRoleExecutor(options, loggerFactory);

        var result = await executor.ExecuteAsync(
            new ExecuteRoleTask(
                "task-model-1",
                SwarmRole.Planner,
                "Plan task",
                "Use role model mapping",
                null,
                null),
            CancellationToken.None);

        Assert.NotNull(result.Model);
        Assert.Equal("claude-sonnet-4-6", result.Model!.Id);
        Assert.Equal("anthropic", result.Model.Provider);
        Assert.Equal("high", result.Reasoning);
    }

    [Fact]
    public void NormalizeOutput_StripsAnsiAndTrims()
    {
        var normalized = SubscriptionCliRoleExecutor.NormalizeOutput("\u001b[0mhi\u001b[0m\r\n");
        Assert.Equal("hi", normalized);
    }

    [Fact]
    public void FindRejectedOutputMatch_ReturnsCommonAuthSnippet()
    {
        var match = SubscriptionCliRoleExecutor.FindRejectedOutputMatch(
            "Authorization failed, please check your login status",
            []);

        Assert.Equal("authorization failed", match);
    }

    [Fact]
    public void FindRejectedOutputMatch_ReturnsAdapterSpecificSnippet()
    {
        var match = SubscriptionCliRoleExecutor.FindRejectedOutputMatch(
            "token expired",
            ["token expired"]);

        Assert.Equal("token expired", match);
    }
}
