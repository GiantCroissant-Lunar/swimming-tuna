using System.Net;
using System.Net.Http.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Agents;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime.Tests;

public sealed class AgentEndpointIntegrationTests : TestKit
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder => { });

    private static RuntimeOptions CreateOptions(bool endpointEnabled) =>
        new()
        {
            AgentFrameworkExecutionMode = "subscription-cli-fallback",
            CliAdapterOrder = ["local-echo"],
            SandboxMode = "host",
            AgentEndpointEnabled = endpointEnabled
        };

    [Fact]
    public async Task AgentWithEndpointEnabled_ExposesAgentCard()
    {
        var options = CreateOptions(endpointEnabled: true);
        var registry = Sys.ActorOf(
            Props.Create(() => new AgentRegistryActor(_loggerFactory, null, null, 30)),
            "cap-reg-endpoint");
        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);

        const string agentId = "endpoint-test-agent";
        var agent = Sys.ActorOf(
            Props.Create(() => new SwarmAgentActor(
                options,
                _loggerFactory,
                engine,
                telemetry,
                registry,
                new[] { SwarmRole.Builder },
                default(TimeSpan),
                agentId,
                0)),
            "endpoint-agent");

        // Wait for the agent to register and include an endpoint URL
        string? endpointUrl = null;
        AwaitAssert(() =>
        {
            registry.Tell(new GetCapabilitySnapshot());
            var snapshot = ExpectMsg<CapabilitySnapshot>();
            var entry = snapshot.Agents.FirstOrDefault(a => a.AgentId == agentId);
            Assert.NotNull(entry);
            Assert.NotNull(entry.EndpointUrl);
            Assert.StartsWith("http://127.0.0.1:", entry.EndpointUrl);
            endpointUrl = entry.EndpointUrl;
        }, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200));

        // Query the agent card from the HTTP endpoint
        using var client = new HttpClient();
        var response = await client.GetAsync($"{endpointUrl}/.well-known/agent-card.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var card = await response.Content.ReadFromJsonAsync<AgentCard>();
        Assert.NotNull(card);
        Assert.Equal(agentId, card.AgentId);
        Assert.Equal("swarm-assistant", card.Name);
        Assert.Equal("a2a", card.Protocol);
        Assert.Contains(SwarmRole.Builder, card.Capabilities);

        // Verify health endpoint as well
        var healthResponse = await client.GetAsync($"{endpointUrl}/a2a/health");
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);

        // Clean up: stop the actor so the endpoint shuts down
        Sys.Stop(agent);
    }

    [Fact]
    public void AgentWithEndpointDisabled_DoesNotExposeEndpointUrl()
    {
        var options = CreateOptions(endpointEnabled: false);
        var registry = Sys.ActorOf(
            Props.Create(() => new AgentRegistryActor(_loggerFactory, null, null, 30)),
            "cap-reg-no-endpoint");
        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);

        const string agentId = "no-endpoint-agent";
        _ = Sys.ActorOf(
            Props.Create(() => new SwarmAgentActor(
                options,
                _loggerFactory,
                engine,
                telemetry,
                registry,
                new[] { SwarmRole.Builder },
                default(TimeSpan),
                agentId,
                0)),
            "no-endpoint-agent");

        AwaitAssert(() =>
        {
            registry.Tell(new GetCapabilitySnapshot());
            var snapshot = ExpectMsg<CapabilitySnapshot>();
            var entry = snapshot.Agents.FirstOrDefault(a => a.AgentId == agentId);
            Assert.NotNull(entry);
            Assert.Null(entry.EndpointUrl);
        }, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void AgentWithEndpointEnabled_NoPort_DoesNotExposeEndpointUrl()
    {
        var options = CreateOptions(endpointEnabled: true);
        var registry = Sys.ActorOf(
            Props.Create(() => new AgentRegistryActor(_loggerFactory, null, null, 30)),
            "cap-reg-no-port");
        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);

        const string agentId = "no-port-agent";
        _ = Sys.ActorOf(
            Props.Create(() => new SwarmAgentActor(
                options,
                _loggerFactory,
                engine,
                telemetry,
                registry,
                new[] { SwarmRole.Planner },
                default(TimeSpan),
                agentId,
                null)),
            "no-port-agent");

        AwaitAssert(() =>
        {
            registry.Tell(new GetCapabilitySnapshot());
            var snapshot = ExpectMsg<CapabilitySnapshot>();
            var entry = snapshot.Agents.FirstOrDefault(a => a.AgentId == agentId);
            Assert.NotNull(entry);
            Assert.Null(entry.EndpointUrl);
        }, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200));
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
