using Akka.Actor;
using Akka.TestKit.Xunit2;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime.Tests;

public sealed class SwarmAgentActorTests : TestKit
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder => { });

    private RuntimeOptions CreateRuntimeOptions()
    {
        return new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "subscription-cli-fallback",
            CliAdapterOrder = ["local-echo"],
            SandboxMode = "host"
        };
    }

    [Fact]
    public void RegistersCapabilitiesOnStart()
    {
        var registry = Sys.ActorOf(Props.Create(() => new CapabilityRegistryActor(_loggerFactory)));
        var telemetry = new RuntimeTelemetry(CreateRuntimeOptions(), _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(CreateRuntimeOptions(), _loggerFactory, telemetry);

        _ = Sys.ActorOf(Props.Create(() => new SwarmAgentActor(
            CreateRuntimeOptions(),
            _loggerFactory,
            engine,
            telemetry,
            registry,
            new[] { SwarmRole.Planner, SwarmRole.Reviewer })));

        AwaitAssert(() =>
        {
            registry.Tell(new GetCapabilitySnapshot());
            var snapshot = ExpectMsg<CapabilitySnapshot>();
            Assert.Contains(snapshot.Agents, a => a.Capabilities.Contains(SwarmRole.Planner));
            Assert.Contains(snapshot.Agents, a => a.Capabilities.Contains(SwarmRole.Reviewer));
        }, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void RoutesRolesDynamicallyThroughRegistry()
    {
        var options = CreateRuntimeOptions();
        var registry = Sys.ActorOf(Props.Create(() => new CapabilityRegistryActor(_loggerFactory)));
        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);

        _ = Sys.ActorOf(Props.Create(() => new SwarmAgentActor(
            options,
            _loggerFactory,
            engine,
            telemetry,
            registry,
            new[] { SwarmRole.Planner, SwarmRole.Researcher })));
        AwaitAgentRegistration(registry);

        registry.Tell(new ExecuteRoleTask("task-plan", SwarmRole.Planner, "Plan", "desc", null, null));
        var planResult = ExpectMsg<RoleTaskSucceeded>();
        Assert.Equal(SwarmRole.Planner, planResult.Role);

        registry.Tell(new ExecuteRoleTask("task-research", SwarmRole.Researcher, "Research", "desc", null, null));
        var researchResult = ExpectMsg<RoleTaskSucceeded>();
        Assert.Equal(SwarmRole.Researcher, researchResult.Role);
    }

    [Theory]
    [InlineData(SwarmRole.Planner)]
    [InlineData(SwarmRole.Builder)]
    [InlineData(SwarmRole.Reviewer)]
    [InlineData(SwarmRole.Orchestrator)]
    public void ExistingCoreRolesRemainSupported(SwarmRole role)
    {
        var options = CreateRuntimeOptions();
        var registry = Sys.ActorOf(Props.Create(() => new CapabilityRegistryActor(_loggerFactory)));
        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);

        _ = Sys.ActorOf(Props.Create(() => new SwarmAgentActor(
            options,
            _loggerFactory,
            engine,
            telemetry,
            registry,
            new[] { SwarmRole.Planner, SwarmRole.Builder, SwarmRole.Reviewer, SwarmRole.Orchestrator })));
        AwaitAgentRegistration(registry);

        registry.Tell(new ExecuteRoleTask(
            $"task-{role}",
            role,
            "Task",
            "Description",
            "plan",
            "build",
            "ACTION: Plan\nREASON: start"));

        var result = ExpectMsg<RoleTaskSucceeded>();
        Assert.Equal(role, result.Role);
    }

    private void AwaitAgentRegistration(IActorRef registry)
    {
        AwaitAssert(() =>
        {
            registry.Tell(new GetCapabilitySnapshot());
            var snapshot = ExpectMsg<CapabilitySnapshot>();
            Assert.NotEmpty(snapshot.Agents);
        }, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loggerFactory.Dispose();
        }

        base.Dispose(disposing);
    }
}
