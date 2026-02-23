using Akka.Actor;
using Akka.TestKit.Xunit2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Tasks;
using SwarmAssistant.Runtime.Telemetry;
using SwarmAssistant.Runtime.Ui;

namespace SwarmAssistant.Runtime.Tests;

/// <summary>
/// Tests for Phase 19 dynamic topology: SpawnAgent, AgentSpawned, AgentRetired,
/// SwarmAgentActor idle TTL, and RuntimeOptions auto-scaling configuration.
/// </summary>
public sealed class DynamicTopologyTests : TestKit
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder => { });

    private RuntimeOptions CreateOptions() => new RuntimeOptions
    {
        AgentFrameworkExecutionMode = "subscription-cli-fallback",
        CliAdapterOrder = ["local-echo"],
        SandboxMode = "host"
    };

    // ── RuntimeOptions auto-scaling properties ──────────────────────────────

    [Fact]
    public void AutoScaleEnabled_DefaultsToFalse()
    {
        var options = new RuntimeOptions();
        Assert.False(options.AutoScaleEnabled);
    }

    [Fact]
    public void AutoScaleOptions_CanBeConfigured()
    {
        var options = new RuntimeOptions
        {
            AutoScaleEnabled = true,
            MinPoolSize = 2,
            MaxPoolSize = 20,
            ScaleUpThreshold = 8,
            ScaleDownThreshold = 2
        };

        Assert.True(options.AutoScaleEnabled);
        Assert.Equal(2, options.MinPoolSize);
        Assert.Equal(20, options.MaxPoolSize);
        Assert.Equal(8, options.ScaleUpThreshold);
        Assert.Equal(2, options.ScaleDownThreshold);
    }

    [Fact]
    public void AutoScaleOptions_DefaultThresholds()
    {
        var options = new RuntimeOptions();

        Assert.Equal(1, options.MinPoolSize);
        Assert.Equal(16, options.MaxPoolSize);
        Assert.Equal(5, options.ScaleUpThreshold);
        Assert.Equal(1, options.ScaleDownThreshold);
    }

    // ── SpawnAgent / AgentSpawned ────────────────────────────────────────────

    [Fact]
    public void DispatcherActor_SpawnAgent_RepliesWithAgentSpawned()
    {
        var options = CreateOptions();
        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);
        var capabilityRegistry = Sys.ActorOf(
            Props.Create(() => new CapabilityRegistryActor(_loggerFactory)), "cap-reg-spawn");
        var uiEvents = new UiEventStream();
        var taskRegistry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);

        var dispatcher = Sys.ActorOf(
            Props.Create(() => new DispatcherActor(
                capabilityRegistry,
                capabilityRegistry,
                TestActor,
                TestActor,
                TestActor,
                engine,
                _loggerFactory,
                telemetry,
                uiEvents,
                taskRegistry,
                Microsoft.Extensions.Options.Options.Create(options),
                null,
                null)),
            "dispatcher-spawn-test");

        var caps = new[] { SwarmRole.Planner, SwarmRole.Researcher };
        dispatcher.Tell(new SpawnAgent(caps, TimeSpan.FromSeconds(30)));

        var spawned = ExpectMsg<AgentSpawned>(TimeSpan.FromSeconds(5));
        Assert.NotNull(spawned.AgentId);
        Assert.StartsWith("dynamic-agent-", spawned.AgentId);
        Assert.NotNull(spawned.AgentRef);
        Assert.NotEqual(ActorRefs.Nobody, spawned.AgentRef);
    }

    [Fact]
    public void DispatcherActor_SpawnedAgent_RegistersWithCapabilityRegistry()
    {
        var options = CreateOptions();
        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);
        var capabilityRegistry = Sys.ActorOf(
            Props.Create(() => new CapabilityRegistryActor(_loggerFactory)), "cap-reg-register");
        var uiEvents = new UiEventStream();
        var taskRegistry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);

        var dispatcher = Sys.ActorOf(
            Props.Create(() => new DispatcherActor(
                capabilityRegistry,
                capabilityRegistry,
                TestActor,
                TestActor,
                TestActor,
                engine,
                _loggerFactory,
                telemetry,
                uiEvents,
                taskRegistry,
                Microsoft.Extensions.Options.Options.Create(options),
                null,
                null)),
            "dispatcher-register-test");

        var caps = new[] { SwarmRole.Tester, SwarmRole.Debugger };
        dispatcher.Tell(new SpawnAgent(caps, TimeSpan.FromMinutes(5)));

        // Wait for the AgentSpawned reply
        ExpectMsg<AgentSpawned>(TimeSpan.FromSeconds(5));

        // The spawned SwarmAgentActor should advertise its capabilities to the registry
        AwaitAssert(() =>
        {
            capabilityRegistry.Tell(new GetCapabilitySnapshot());
            var snapshot = ExpectMsg<CapabilitySnapshot>();
            Assert.Contains(snapshot.Agents, a => a.Capabilities.Contains(SwarmRole.Tester));
        }, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
    }

    // ── SwarmAgentActor idle TTL ──────────────────────────────────────────────

    [Fact]
    public void SwarmAgentActor_WithIdleTtl_SelfTerminatesAfterTimeout()
    {
        var options = CreateOptions();
        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);
        var registry = Sys.ActorOf(
            Props.Create(() => new CapabilityRegistryActor(_loggerFactory)), "cap-reg-ttl");

        var ttl = TimeSpan.FromMilliseconds(300);
        var agent = Sys.ActorOf(
            Props.Create(() => new SwarmAgentActor(
                options,
                _loggerFactory,
                engine,
                telemetry,
                registry,
                new[] { SwarmRole.Planner },
                ttl)),
            "ttl-agent");

        Watch(agent);
        ExpectTerminated(agent, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SwarmAgentActor_WithNoTtl_DoesNotSelfTerminate()
    {
        var options = CreateOptions();
        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);
        var registry = Sys.ActorOf(
            Props.Create(() => new CapabilityRegistryActor(_loggerFactory)), "cap-reg-no-ttl");

        // No TTL (default = zero)
        var agentActor = Sys.ActorOf(Props.Create(() => new SwarmAgentActor(
                new RuntimeOptions(),
                _loggerFactory,
                engine,
                telemetry,
                registry,
                new[] { SwarmRole.Builder },
                default)),
            "no-ttl-agent");

        Watch(agentActor);
        // Actor should NOT terminate within a short window
        ExpectNoMsg(TimeSpan.FromMilliseconds(500));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loggerFactory.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class NoOpTaskMemoryWriter : ITaskMemoryWriter
    {
        public Task WriteAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
