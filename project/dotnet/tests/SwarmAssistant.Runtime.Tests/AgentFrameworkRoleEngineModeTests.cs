using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Execution;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime.Tests;

public sealed class AgentFrameworkRoleEngineModeTests
{
    [Fact]
    public async Task ExecuteAsync_ApiDirect_UsesRegisteredModelProvider()
    {
        var options = new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "api-direct",
            RoleModelMapping = new Dictionary<string, RoleModelPreference>
            {
                ["Planner"] = new()
                {
                    Model = "openai/gpt-4o-mini",
                    Reasoning = "medium"
                }
            }
        };

        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var telemetry = new RuntimeTelemetry(options, loggerFactory);
        var provider = new FakeModelProvider("openai", "api-direct-output");
        var engine = new AgentFrameworkRoleEngine(options, loggerFactory, telemetry, [provider]);

        var result = await engine.ExecuteAsync(
            new ExecuteRoleTask("task-1", SwarmRole.Planner, "plan", "desc", null, null),
            CancellationToken.None);

        Assert.Equal("api-openai", result.AdapterId);
        Assert.Equal("api-direct-output", result.Output);
        Assert.NotNull(result.Model);
        Assert.Equal("gpt-4o-mini", result.Model!.Id);
        Assert.Equal("medium", result.Reasoning);
    }

    [Fact]
    public async Task ExecuteAsync_Hybrid_FallsBackToCliWhenProviderMissing()
    {
        var options = new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "hybrid",
            CliAdapterOrder = ["local-echo"],
            SandboxMode = "host",
            RoleModelMapping = new Dictionary<string, RoleModelPreference>
            {
                ["Planner"] = new()
                {
                    Model = "anthropic/claude-sonnet-4-6",
                    Reasoning = "high"
                }
            }
        };

        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var telemetry = new RuntimeTelemetry(options, loggerFactory);
        var engine = new AgentFrameworkRoleEngine(
            options,
            loggerFactory,
            telemetry,
            [new FakeModelProvider("openai", "unused")]);

        var result = await engine.ExecuteAsync(
            new ExecuteRoleTask("task-2", SwarmRole.Planner, "plan", "desc", null, null),
            CancellationToken.None);

        Assert.Equal("local-echo", result.AdapterId);
        Assert.Contains("[LocalEcho/Planner]", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ApiDirect_ThrowsWhenRoleModelMissing()
    {
        var options = new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "api-direct"
        };

        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var telemetry = new RuntimeTelemetry(options, loggerFactory);
        var engine = new AgentFrameworkRoleEngine(
            options,
            loggerFactory,
            telemetry,
            [new FakeModelProvider("openai", "unused")]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.ExecuteAsync(
                new ExecuteRoleTask("task-3", SwarmRole.Planner, "plan", "desc", null, null),
                CancellationToken.None));

        Assert.Contains("RoleModelMapping", exception.Message);
    }

    private sealed class FakeModelProvider(string providerId, string output) : IModelProvider
    {
        public string ProviderId { get; } = providerId;

        public Task<bool> ProbeAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<ModelResponse> ExecuteAsync(
            ModelSpec model,
            string prompt,
            ModelExecutionOptions options,
            CancellationToken ct)
        {
            return Task.FromResult(new ModelResponse
            {
                Output = output,
                Usage = new TokenUsage
                {
                    InputTokens = 12,
                    OutputTokens = 34
                },
                ModelId = model.Id,
                Latency = TimeSpan.FromMilliseconds(10)
            });
        }
    }
}
