using FluentAssertions;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Hierarchy;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Execution;
using SwarmAssistant.Runtime.Telemetry;
using Xunit;

namespace SwarmAssistant.Runtime.Tests;

public sealed class AgentFrameworkRoleEngineSpanTests
{
    [Fact]
    public async Task ExecuteAsync_ApiDirect_CreatesAndCompletesSpan()
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
            new ExecuteRoleTask("task-1", SwarmRole.Planner, "plan", "desc", null, null, RunId: "run-1"),
            CancellationToken.None);

        result.AdapterId.Should().Be("api-openai");
        result.Output.Should().Be("api-direct-output");
    }

    [Fact]
    public async Task ExecuteAsync_SubscriptionCli_CreatesAndCompletesSpan()
    {
        var options = new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "subscription-cli-fallback",
            CliAdapterOrder = ["local-echo"],
            SandboxMode = "host"
        };

        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var telemetry = new RuntimeTelemetry(options, loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, loggerFactory, telemetry);

        var result = await engine.ExecuteAsync(
            new ExecuteRoleTask("task-2", SwarmRole.Builder, "build", "desc", null, null, RunId: "run-1"),
            CancellationToken.None);

        result.AdapterId.Should().Be("local-echo");
        result.Output.Should().Contain("[LocalEcho/Builder]");
    }

    [Fact]
    public async Task ExecuteAsync_Hybrid_ApiDirectPath_CreatesAndCompletesSpan()
    {
        var options = new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "hybrid",
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
        var provider = new FakeModelProvider("openai", "hybrid-output");
        var engine = new AgentFrameworkRoleEngine(options, loggerFactory, telemetry, [provider]);

        var result = await engine.ExecuteAsync(
            new ExecuteRoleTask("task-3", SwarmRole.Planner, "plan", "desc", null, null, RunId: "run-1"),
            CancellationToken.None);

        result.AdapterId.Should().Be("api-openai");
        result.Output.Should().Be("hybrid-output");
    }

    [Fact]
    public async Task ExecuteAsync_Hybrid_CliFallbackPath_CreatesAndCompletesSpan()
    {
        var options = new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "hybrid",
            CliAdapterOrder = ["local-echo"],
            SandboxMode = "host"
        };

        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var telemetry = new RuntimeTelemetry(options, loggerFactory);
        var engine = new AgentFrameworkRoleEngine(
            options,
            loggerFactory,
            telemetry,
            [new FakeModelProvider("openai", "unused")]);

        var result = await engine.ExecuteAsync(
            new ExecuteRoleTask("task-4", SwarmRole.Builder, "build", "desc", null, null, RunId: "run-1"),
            CancellationToken.None);

        result.AdapterId.Should().Be("local-echo");
        result.Output.Should().Contain("[LocalEcho/Builder]");
    }

    [Fact]
    public async Task ExecuteAsync_InProcessWorkflow_CreatesAndCompletesSpan()
    {
        var options = new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "in-process-workflow",
            CliAdapterOrder = ["local-echo"],
            SandboxMode = "host"
        };

        using var loggerFactory = LoggerFactory.Create(_ => { });
        using var telemetry = new RuntimeTelemetry(options, loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, loggerFactory, telemetry);

        var result = await engine.ExecuteAsync(
            new ExecuteRoleTask("task-5", SwarmRole.Reviewer, "review", "desc", null, null, RunId: "run-1"),
            CancellationToken.None);

        result.AdapterId.Should().Be("local-echo");
        result.Output.Should().Contain("[LocalEcho/Reviewer]");
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
                Usage = new Runtime.Execution.TokenUsage
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
