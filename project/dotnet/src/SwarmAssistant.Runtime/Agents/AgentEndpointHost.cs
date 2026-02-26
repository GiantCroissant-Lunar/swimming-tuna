
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace SwarmAssistant.Runtime.Agents;
public sealed class AgentEndpointHost : IAsyncDisposable
{
    private readonly AgentCard _card;
    private readonly int _requestedPort;
    private WebApplication? _app;
    private string? _baseUrl;
    private readonly ConcurrentQueue<AgentTaskRequest> _taskQueue = new();

    public string BaseUrl => _baseUrl ?? throw new InvalidOperationException("Host not started");

    public AgentEndpointHost(AgentCard card, int port)
    {
        _card = card;
        _requestedPort = port;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{_requestedPort}");
        _app = builder.Build();

        _app.MapGet("/.well-known/agent-card.json", () => Results.Ok(_card));

        _app.MapGet("/a2a/health", () => Results.Ok(new
        {
            ok = true,
            agentId = _card.AgentId,
            capabilities = _card.Capabilities.Select(c => c.ToString()).ToArray()
        }));

        _app.MapPost("/a2a/tasks", (AgentTaskRequest request) =>
        {
            var taskId = request.TaskId ?? Guid.NewGuid().ToString("N")[..12];
            _taskQueue.Enqueue(request with { TaskId = taskId });
            return Results.Accepted($"/a2a/tasks/{taskId}", new { taskId, status = "queued" });
        });

        await _app.StartAsync(ct);

        var addresses = _app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();
        _baseUrl = addresses?.Addresses.FirstOrDefault() ?? _app.Urls.First();
    }

    public async Task StopAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
            _baseUrl = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    public bool TryDequeueTask(out AgentTaskRequest? task)
    {
        return _taskQueue.TryDequeue(out task);
    }
}

public sealed record AgentTaskRequest
{
    public string? TaskId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
}
