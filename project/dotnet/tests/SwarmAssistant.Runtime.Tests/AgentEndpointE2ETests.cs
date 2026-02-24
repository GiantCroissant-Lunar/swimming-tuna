namespace SwarmAssistant.Runtime.Tests;

using System.Net;
using System.Net.Http.Json;
using SwarmAssistant.Runtime.Agents;
using SwarmAssistant.Contracts.Messaging;

public sealed class AgentEndpointE2ETests : IAsyncDisposable
{
    private readonly HttpClient _client = new();

    [Fact]
    public async Task TwoAgents_CanDiscoverEachOther()
    {
        // Create two agent endpoints on different ports (port 0 = OS picks free port)
        var card1 = new AgentCard
        {
            AgentId = "planner-01", Name = "planner",
            Version = "phase-12", Protocol = "a2a",
            Capabilities = [SwarmRole.Planner],
            Provider = "local-echo", SandboxLevel = 0,
            EndpointUrl = "http://localhost:0"
        };
        var card2 = new AgentCard
        {
            AgentId = "builder-01", Name = "builder",
            Version = "phase-12", Protocol = "a2a",
            Capabilities = [SwarmRole.Builder],
            Provider = "local-echo", SandboxLevel = 0,
            EndpointUrl = "http://localhost:0"
        };

        var host1 = new AgentEndpointHost(card1, port: 0);
        var host2 = new AgentEndpointHost(card2, port: 0);

        await host1.StartAsync(CancellationToken.None);
        await host2.StartAsync(CancellationToken.None);

        // Agent 1 discovers Agent 2's card via HTTP
        var response = await _client.GetAsync(
            $"{host2.BaseUrl}/.well-known/agent-card.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var discoveredCard = await response.Content.ReadFromJsonAsync<AgentCard>();
        Assert.Equal("builder-01", discoveredCard!.AgentId);
        Assert.Contains(SwarmRole.Builder, discoveredCard.Capabilities);

        // Agent 1 submits a task to Agent 2 via HTTP
        var taskResponse = await _client.PostAsJsonAsync(
            $"{host2.BaseUrl}/a2a/tasks",
            new { title = "Build the API", description = "Based on planner output" });
        Assert.Equal(HttpStatusCode.Accepted, taskResponse.StatusCode);

        // Verify Agent 2 received the task
        Assert.True(host2.TryDequeueTask(out var receivedTask));
        Assert.Equal("Build the API", receivedTask!.Title);

        await host1.StopAsync();
        await host2.StopAsync();
    }

    [Fact]
    public async Task Agent_HealthEndpoint_ReportsCapabilities()
    {
        var card = new AgentCard
        {
            AgentId = "reviewer-01", Name = "reviewer",
            Version = "phase-12", Protocol = "a2a",
            Capabilities = [SwarmRole.Reviewer, SwarmRole.Tester],
            Provider = "cline", SandboxLevel = 0,
            EndpointUrl = "http://localhost:0"
        };
        var host = new AgentEndpointHost(card, port: 0);
        await host.StartAsync(CancellationToken.None);

        var response = await _client.GetAsync($"{host.BaseUrl}/a2a/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("reviewer-01", json);
        Assert.Contains("Reviewer", json);
        Assert.Contains("Tester", json);

        await host.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await Task.CompletedTask;
    }
}
