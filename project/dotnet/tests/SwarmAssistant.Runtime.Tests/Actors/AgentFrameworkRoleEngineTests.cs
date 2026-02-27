using FluentAssertions;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Execution;
using SwarmAssistant.Runtime.Telemetry;
using Xunit;

namespace SwarmAssistant.Runtime.Tests.Actors;

public sealed class AgentFrameworkRoleEngineTests
{
    private readonly ILoggerFactory _loggerFactory;

    public AgentFrameworkRoleEngineTests()
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
    }

    [Fact]
    public async Task ExecuteAsync_CliMode_ReturnsResult()
    {
        var options = new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "subscription-cli-fallback",
            CliAdapterOrder = ["local-echo"]
        };

        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);
        var command = new ExecuteRoleTask(
            TaskId: "task-1",
            Role: SwarmRole.Builder,
            Title: "Test Task",
            Description: "Test Description",
            PlanningOutput: null,
            BuildOutput: null,
            RunId: "run-1"
        );

        var result = await engine.ExecuteAsync(command, CancellationToken.None);

        result.Should().NotBeNull();
        result.Output.Should().NotBeNullOrEmpty();
        result.AdapterId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ApiDirectMode_ThrowsWhenProviderMissing()
    {
        var options = new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "api-direct",
            RoleModelMapping = new Dictionary<string, RoleModelPreference>
            {
                ["builder"] = new RoleModelPreference { Model = "test-model" }
            },
            ApiProviderOrder = []
        };

        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);
        var command = new ExecuteRoleTask(
            TaskId: "task-2",
            Role: SwarmRole.Builder,
            Title: "Test Task",
            Description: "Test Description",
            PlanningOutput: null,
            BuildOutput: null,
            Prompt: "Test prompt",
            RunId: "run-1"
        );

        Func<Task> act = async () => await engine.ExecuteAsync(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No model provider registered*");
    }

    [Fact]
    public async Task ExecuteAsync_WithException_CompletesSpanWithFailedStatus()
    {
        var options = new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "unsupported-mode"
        };

        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);
        var command = new ExecuteRoleTask(
            TaskId: "task-3",
            Role: SwarmRole.Builder,
            Title: "Test Task",
            Description: "Test Description",
            PlanningOutput: null,
            BuildOutput: null,
            RunId: "run-1"
        );

        Func<Task> act = async () => await engine.ExecuteAsync(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unsupported AgentFrameworkExecutionMode*");
    }
}
