namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Runtime.Agents;
using SwarmAssistant.Contracts.Messaging;

public sealed class AgentCardTests
{
    [Fact]
    public void Build_ReturnsCardWithCorrectFields()
    {
        var card = new AgentCard
        {
            AgentId = "builder-01",
            Name = "swarm-builder",
            Version = "phase-12",
            Protocol = "a2a",
            Capabilities = [SwarmRole.Builder],
            Provider = "copilot",
            SandboxLevel = 0,
            EndpointUrl = "http://localhost:8001"
        };

        Assert.Equal("builder-01", card.AgentId);
        Assert.Contains(SwarmRole.Builder, card.Capabilities);
        Assert.Equal("http://localhost:8001", card.EndpointUrl);
    }

    [Fact]
    public void Serializes_ToExpectedJson()
    {
        var card = new AgentCard
        {
            AgentId = "builder-01",
            Name = "swarm-builder",
            Version = "phase-12",
            Protocol = "a2a",
            Capabilities = [SwarmRole.Builder],
            Provider = "copilot",
            SandboxLevel = 0,
            EndpointUrl = "http://localhost:8001"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(card);
        Assert.Contains("\"agentId\"", json);
        Assert.Contains("builder-01", json);
    }
}
