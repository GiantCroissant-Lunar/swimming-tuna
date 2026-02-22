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
}
