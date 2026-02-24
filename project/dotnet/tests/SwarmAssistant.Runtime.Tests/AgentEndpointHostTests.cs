namespace SwarmAssistant.Runtime.Tests;

using System.Net;
using System.Net.Http.Json;
using SwarmAssistant.Runtime.Agents;
using SwarmAssistant.Contracts.Messaging;

public sealed class AgentEndpointHostTests : IAsyncDisposable
{
    private readonly AgentEndpointHost _host;
    private readonly HttpClient _client;

    public AgentEndpointHostTests()
    {
        var card = new AgentCard
        {
            AgentId = "test-agent-01",
            Name = "test-agent",
            Version = "phase-12",
            Protocol = "a2a",
            Capabilities = [SwarmRole.Builder],
            Provider = "local-echo",
            SandboxLevel = 0,
            EndpointUrl = "http://localhost:0"
        };
        _host = new AgentEndpointHost(card, port: 0);
        _client = new HttpClient();
    }

    [Fact]
    public async Task AgentCard_ReturnsCorrectJson()
    {
        await _host.StartAsync(CancellationToken.None);
        var response = await _client.GetAsync($"{_host.BaseUrl}/.well-known/agent-card.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var card = await response.Content.ReadFromJsonAsync<AgentCard>();
        Assert.NotNull(card);
        Assert.Equal("test-agent-01", card.AgentId);
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        await _host.StartAsync(CancellationToken.None);
        var response = await _client.GetAsync($"{_host.BaseUrl}/a2a/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TaskSubmit_AcceptsRequest()
    {
        await _host.StartAsync(CancellationToken.None);
        var response = await _client.PostAsJsonAsync(
            $"{_host.BaseUrl}/a2a/tasks",
            new { title = "test task", description = "test" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _client.Dispose();
    }
}
