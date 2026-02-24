# RFC-001: Agent-as-Endpoint Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Give every SwarmAgentActor its own A2A HTTP endpoint so agents are externally addressable — the foundation for multi-agent peer communication.

**Architecture:** Each SwarmAgentActor gets a unique `agentId` and spawns a lightweight ASP.NET Core `WebApplication` on a dedicated port. The agent exposes `GET /.well-known/agent-card.json`, `POST /a2a/tasks`, and `GET /a2a/health`. The existing runtime-level A2A endpoints remain as the orchestrator's endpoints. This is additive — the current orchestrator flow still works unchanged.

**Tech Stack:** .NET 9, ASP.NET Core Minimal API, Akka.NET, xUnit + Akka.TestKit

**Dogfooding:** This plan is structured as tasks a SwarmAssistant planner→builder→reviewer pipeline could execute. Each task is self-contained with clear inputs, outputs, and verification.

---

## Task 1: Add Agent Endpoint Config Options

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Configuration/RuntimeOptions.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/ConfigurationTests.cs` (new)

**Step 1: Write the failing test**

```csharp
// ConfigurationTests.cs
namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Runtime.Configuration;

public sealed class ConfigurationTests
{
    [Fact]
    public void AgentEndpoint_DefaultsAreCorrect()
    {
        var opts = new RuntimeOptions();
        Assert.False(opts.AgentEndpointEnabled);
        Assert.Equal("8001-8032", opts.AgentEndpointPortRange);
        Assert.Equal(30, opts.AgentHeartbeatIntervalSeconds);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "ConfigurationTests"`
Expected: FAIL — properties do not exist

**Step 3: Write minimal implementation**

Add to `RuntimeOptions.cs` after the existing `A2AAgentCardPath` property:

```csharp
public bool AgentEndpointEnabled { get; init; }
public string AgentEndpointPortRange { get; init; } = "8001-8032";
public int AgentHeartbeatIntervalSeconds { get; init; } = 30;
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "ConfigurationTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Configuration/RuntimeOptions.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/ConfigurationTests.cs
git commit -m "feat(rfc-001): add AgentEndpoint config options"
```

---

## Task 2: Define Agent Card Data Model

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Runtime/Agents/AgentCard.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentCardTests.cs` (new)

**Step 1: Write the failing test**

```csharp
// AgentCardTests.cs
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
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "AgentCardTests"`
Expected: FAIL — AgentCard type does not exist

**Step 3: Write minimal implementation**

```csharp
// AgentCard.cs
namespace SwarmAssistant.Runtime.Agents;

using System.Text.Json.Serialization;
using SwarmAssistant.Contracts.Messaging;

public sealed record AgentCard
{
    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; }

    [JsonPropertyName("capabilities")]
    public required SwarmRole[] Capabilities { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("sandboxLevel")]
    public required int SandboxLevel { get; init; }

    [JsonPropertyName("endpointUrl")]
    public required string EndpointUrl { get; init; }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "AgentCardTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Agents/AgentCard.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentCardTests.cs
git commit -m "feat(rfc-001): add AgentCard data model"
```

---

## Task 3: Build Agent Endpoint Host

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Runtime/Agents/AgentEndpointHost.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentEndpointHostTests.cs` (new)

**Step 1: Write the failing test**

```csharp
// AgentEndpointHostTests.cs
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
        var url = _host.BaseUrl;
        var response = await _client.GetAsync($"{url}/.well-known/agent-card.json");

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
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "AgentEndpointHostTests"`
Expected: FAIL — AgentEndpointHost does not exist

**Step 3: Write minimal implementation**

```csharp
// AgentEndpointHost.cs
namespace SwarmAssistant.Runtime.Agents;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public sealed class AgentEndpointHost
{
    private readonly AgentCard _card;
    private readonly int _requestedPort;
    private WebApplication? _app;
    private readonly ConcurrentQueue<AgentTaskRequest> _taskQueue = new();

    public string BaseUrl => _app?.Urls.First() ?? throw new InvalidOperationException("Host not started");

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
    }

    public async Task StopAsync()
    {
        if (_app is not null)
            await _app.StopAsync();
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
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "AgentEndpointHostTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Agents/AgentEndpointHost.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentEndpointHostTests.cs
git commit -m "feat(rfc-001): add AgentEndpointHost with A2A endpoints"
```

---

## Task 4: Add Port Allocator

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Runtime/Agents/PortAllocator.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/PortAllocatorTests.cs` (new)

**Step 1: Write the failing test**

```csharp
// PortAllocatorTests.cs
namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Runtime.Agents;

public sealed class PortAllocatorTests
{
    [Fact]
    public void Allocate_ReturnsPortsInRange()
    {
        var allocator = new PortAllocator("8001-8032");

        var port1 = allocator.Allocate();
        var port2 = allocator.Allocate();

        Assert.Equal(8001, port1);
        Assert.Equal(8002, port2);
    }

    [Fact]
    public void Release_MakesPortAvailable()
    {
        var allocator = new PortAllocator("8001-8003");

        var p1 = allocator.Allocate();
        var p2 = allocator.Allocate();
        allocator.Release(p1);
        var p3 = allocator.Allocate();

        Assert.Equal(p1, p3);
    }

    [Fact]
    public void Allocate_ThrowsWhenExhausted()
    {
        var allocator = new PortAllocator("8001-8002");

        allocator.Allocate();
        allocator.Allocate();

        Assert.Throws<InvalidOperationException>(() => allocator.Allocate());
    }

    [Fact]
    public void Parse_HandlesRangeFormat()
    {
        var allocator = new PortAllocator("9000-9004");
        Assert.Equal(9000, allocator.Allocate());
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "PortAllocatorTests"`
Expected: FAIL — PortAllocator does not exist

**Step 3: Write minimal implementation**

```csharp
// PortAllocator.cs
namespace SwarmAssistant.Runtime.Agents;

public sealed class PortAllocator
{
    private readonly int _min;
    private readonly int _max;
    private readonly SortedSet<int> _available;
    private readonly object _lock = new();

    public PortAllocator(string range)
    {
        var parts = range.Split('-');
        _min = int.Parse(parts[0]);
        _max = int.Parse(parts[1]);
        _available = new SortedSet<int>(Enumerable.Range(_min, _max - _min + 1));
    }

    public int Allocate()
    {
        lock (_lock)
        {
            if (_available.Count == 0)
                throw new InvalidOperationException(
                    $"No ports available in range {_min}-{_max}");

            var port = _available.Min;
            _available.Remove(port);
            return port;
        }
    }

    public void Release(int port)
    {
        lock (_lock)
        {
            if (port >= _min && port <= _max)
                _available.Add(port);
        }
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "PortAllocatorTests"`
Expected: PASS

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Agents/PortAllocator.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/PortAllocatorTests.cs
git commit -m "feat(rfc-001): add PortAllocator for agent endpoint ports"
```

---

## Task 5: Add Agent Identity to SwarmAgentActor

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/SwarmAgentActor.cs`
- Modify: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/SwarmAgentActorTests.cs`

**Step 1: Write the failing test**

Add to `SwarmAgentActorTests.cs`:

```csharp
[Fact]
public void Agent_HasUniqueIdentity()
{
    var options = CreateOptions();
    var registry = Sys.ActorOf(Props.Create(() =>
        new CapabilityRegistryActor(_loggerFactory)));

    var agent = Sys.ActorOf(Props.Create(() =>
        new SwarmAgentActor(
            options,
            _agentFrameworkRoleEngine,
            _telemetry,
            registry,
            [SwarmRole.Builder],
            _loggerFactory,
            agentId: "builder-01")));

    AwaitAgentRegistration(registry);
    registry.Tell(new GetCapabilitySnapshot());
    var snapshot = ExpectMsg<CapabilitySnapshot>();

    Assert.Contains(snapshot.Agents, a => a.AgentId == "builder-01");
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "Agent_HasUniqueIdentity"`
Expected: FAIL — constructor doesn't accept agentId parameter

**Step 3: Write minimal implementation**

Modify `SwarmAgentActor.cs` constructor to accept optional `agentId`:

```csharp
// Add field
private readonly string _agentId;

// Update constructor signature to add: string? agentId = null
// In constructor body:
_agentId = agentId ?? $"agent-{Guid.NewGuid():N}"[..16];
```

Update `AdvertiseCapability()` to include `_agentId` in the advertisement message.

Update `AgentCapabilityAdvertisement` record in SwarmMessages.cs to include `AgentId` property.

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "Agent_HasUniqueIdentity"`
Expected: PASS

**Step 5: Update existing tests to pass with new optional parameter**

Run: `dotnet test project/dotnet/SwarmAssistant.sln`
Expected: All existing tests still PASS (agentId is optional with default)

**Step 6: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Actors/SwarmAgentActor.cs \
       project/dotnet/src/SwarmAssistant.Contracts/Messaging/SwarmMessages.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/SwarmAgentActorTests.cs
git commit -m "feat(rfc-001): add agentId identity to SwarmAgentActor"
```

---

## Task 6: Wire Agent Endpoint into SwarmAgentActor Lifecycle

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/SwarmAgentActor.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentEndpointIntegrationTests.cs` (new)

**Step 1: Write the failing test**

```csharp
// AgentEndpointIntegrationTests.cs
namespace SwarmAssistant.Runtime.Tests;

using System.Net.Http.Json;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Agents;
using SwarmAssistant.Contracts.Messaging;

public sealed class AgentEndpointIntegrationTests : TestKit, IAsyncDisposable
{
    private readonly HttpClient _client = new();

    [Fact]
    public async Task Agent_ExposesEndpoint_WhenEnabled()
    {
        var options = CreateOptions(agentEndpointEnabled: true);
        var registry = Sys.ActorOf(Props.Create(() =>
            new CapabilityRegistryActor(LoggerFactory)));

        var agent = Sys.ActorOf(Props.Create(() =>
            new SwarmAgentActor(
                options,
                CreateRoleEngine(),
                CreateTelemetry(),
                registry,
                [SwarmRole.Builder],
                LoggerFactory,
                agentId: "builder-endpoint-test",
                httpPort: 0)));

        // Wait for endpoint to be ready
        await Task.Delay(1000);

        // Query the agent's capability advertisement to get its endpoint URL
        registry.Tell(new GetCapabilitySnapshot());
        var snapshot = ExpectMsg<CapabilitySnapshot>();
        var agentInfo = snapshot.Agents.First(a => a.AgentId == "builder-endpoint-test");

        Assert.NotNull(agentInfo.EndpointUrl);

        var response = await _client.GetAsync($"{agentInfo.EndpointUrl}/.well-known/agent-card.json");
        Assert.True(response.IsSuccessStatusCode);

        var card = await response.Content.ReadFromJsonAsync<AgentCard>();
        Assert.Equal("builder-endpoint-test", card!.AgentId);
    }

    [Fact]
    public async Task Agent_DoesNotExposeEndpoint_WhenDisabled()
    {
        var options = CreateOptions(agentEndpointEnabled: false);
        var registry = Sys.ActorOf(Props.Create(() =>
            new CapabilityRegistryActor(LoggerFactory)));

        var agent = Sys.ActorOf(Props.Create(() =>
            new SwarmAgentActor(
                options,
                CreateRoleEngine(),
                CreateTelemetry(),
                registry,
                [SwarmRole.Builder],
                LoggerFactory,
                agentId: "builder-no-endpoint")));

        await Task.Delay(500);
        registry.Tell(new GetCapabilitySnapshot());
        var snapshot = ExpectMsg<CapabilitySnapshot>();
        var agentInfo = snapshot.Agents.First(a => a.AgentId == "builder-no-endpoint");

        Assert.Null(agentInfo.EndpointUrl);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await Task.CompletedTask;
    }

    // Helper methods to create test options, role engine, telemetry
    // Follow existing patterns in SwarmAgentActorTests.cs
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "AgentEndpointIntegrationTests"`
Expected: FAIL — SwarmAgentActor doesn't create HTTP endpoints

**Step 3: Write minimal implementation**

In `SwarmAgentActor.cs`:

```csharp
// Add fields
private AgentEndpointHost? _endpointHost;
private readonly int? _httpPort;
private readonly bool _endpointEnabled;

// Update constructor to accept httpPort
// In PreStart(), after AdvertiseCapability():
if (_endpointEnabled && _httpPort.HasValue)
{
    var card = new AgentCard
    {
        AgentId = _agentId,
        Name = "swarm-assistant",
        Version = "phase-12",
        Protocol = "a2a",
        Capabilities = _capabilities,
        Provider = _options.CliAdapterOrder?.FirstOrDefault() ?? "local-echo",
        SandboxLevel = 0,
        EndpointUrl = $"http://127.0.0.1:{_httpPort.Value}"
    };
    _endpointHost = new AgentEndpointHost(card, _httpPort.Value);
    _endpointHost.StartAsync(CancellationToken.None).Wait();
}
```

Update `AdvertiseCapability()` to include `EndpointUrl` when endpoint is enabled.

In `PostStop()`, stop the endpoint host:

```csharp
protected override void PostStop()
{
    _endpointHost?.StopAsync().Wait();
    base.PostStop();
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "AgentEndpointIntegrationTests"`
Expected: PASS

**Step 5: Run full test suite**

Run: `dotnet test project/dotnet/SwarmAssistant.sln`
Expected: All tests PASS

**Step 6: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Actors/SwarmAgentActor.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentEndpointIntegrationTests.cs
git commit -m "feat(rfc-001): wire AgentEndpointHost into SwarmAgentActor lifecycle"
```

---

## Task 7: Wire Port Allocation in Worker.cs Bootstrap

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Worker.cs`
- Test: verify via existing integration test + manual smoke test

**Step 1: Write the failing test**

This is a bootstrap integration — test by running the runtime with the new config enabled.

Create a configuration test:

```csharp
// In ConfigurationTests.cs, add:
[Fact]
public void AgentEndpoint_PortRange_ParsedCorrectly()
{
    var allocator = new PortAllocator("8001-8005");
    var ports = Enumerable.Range(0, 5).Select(_ => allocator.Allocate()).ToList();
    Assert.Equal([8001, 8002, 8003, 8004, 8005], ports);
}
```

**Step 2: Modify Worker.cs to allocate ports**

In `Worker.cs`, after the SwarmAgentActor pool creation (around line 162):

```csharp
PortAllocator? portAllocator = null;
if (options.AgentEndpointEnabled)
{
    portAllocator = new PortAllocator(options.AgentEndpointPortRange);
    _logger.LogInformation("Agent endpoints enabled, port range: {Range}",
        options.AgentEndpointPortRange);
}
```

Update the agent creation loop to pass unique `agentId` and `httpPort` from the allocator to each SwarmAgentActor instance.

Note: The current pool uses `SmallestMailboxPool` router which creates anonymous children. To assign per-agent identity, switch from pool router to individually-created actors:

```csharp
if (options.AgentEndpointEnabled)
{
    // Create individual agents with identity (not pooled router)
    var agents = new List<IActorRef>();
    for (int i = 0; i < swarmAgentPoolSize; i++)
    {
        var agentId = $"agent-{i:D2}";
        var port = portAllocator!.Allocate();
        var agent = actorSystem.ActorOf(
            Props.Create(() => new SwarmAgentActor(
                options, agentFrameworkRoleEngine, telemetry,
                capabilityRegistry, capabilities, loggerFactory,
                agentId: agentId, httpPort: port)),
            $"swarm-agent-{agentId}");
        agents.Add(agent);
    }
    // Use round-robin or smallest-mailbox selection for dispatch
}
else
{
    // Keep existing pool router (backward compatible)
}
```

**Step 3: Run full test suite**

Run: `dotnet test project/dotnet/SwarmAssistant.sln`
Expected: All tests PASS

**Step 4: Manual smoke test**

```bash
Runtime__AgentEndpointEnabled=true \
  DOTNET_ENVIRONMENT=Local \
  dotnet run --project project/dotnet/src/SwarmAssistant.Runtime --no-launch-profile
```

Verify in logs: "Agent endpoints enabled" and check:
```bash
curl -s http://127.0.0.1:8001/.well-known/agent-card.json
curl -s http://127.0.0.1:8001/a2a/health
```

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Worker.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/ConfigurationTests.cs
git commit -m "feat(rfc-001): wire port allocation and per-agent identity in bootstrap"
```

---

## Task 8: Update AGENTS.md and Docs

**Files:**
- Modify: `AGENTS.md`
- Modify: `project/README.md`
- Modify: `project/dotnet/README.md`

**Step 1: Add new config flags to AGENTS.md**

Add `AgentEndpointEnabled`, `AgentEndpointPortRange`, `AgentHeartbeatIntervalSeconds` to the runtime config table.

**Step 2: Add to project/README.md config list**

Add the three new properties to the "Runtime config includes:" list.

**Step 3: Add to project/dotnet/README.md**

Add to "Runtime Flags" list and add a new section:

```markdown
## Agent Endpoints (RFC-001)

When `Runtime__AgentEndpointEnabled=true`, each SwarmAgentActor spawns its own
HTTP endpoint within the configured port range.

```bash
export Runtime__AgentEndpointEnabled=true
export Runtime__AgentEndpointPortRange=8001-8032

# Query individual agent:
curl -s http://127.0.0.1:8001/.well-known/agent-card.json
curl -s http://127.0.0.1:8001/a2a/health
curl -s -X POST http://127.0.0.1:8001/a2a/tasks \
  -H 'content-type: application/json' \
  -d '{"title":"test task"}'
```
```

**Step 4: Commit**

```bash
git add AGENTS.md project/README.md project/dotnet/README.md
git commit -m "docs(rfc-001): document agent endpoint config and usage"
```

---

## Task 9: End-to-End Dogfood Test

**Files:**
- Create: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentEndpointE2ETests.cs`

**Step 1: Write the e2e test**

```csharp
// AgentEndpointE2ETests.cs
namespace SwarmAssistant.Runtime.Tests;

using System.Net.Http.Json;
using SwarmAssistant.Runtime.Agents;

public sealed class AgentEndpointE2ETests : IAsyncDisposable
{
    private readonly HttpClient _client = new();

    [Fact]
    public async Task TwoAgents_CanDiscoverEachOther()
    {
        // Create two agent endpoints on different ports
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

        // Agent 1 can query Agent 2's card
        var response = await _client.GetAsync(
            $"{host2.BaseUrl}/.well-known/agent-card.json");
        var discoveredCard = await response.Content.ReadFromJsonAsync<AgentCard>();

        Assert.Equal("builder-01", discoveredCard!.AgentId);

        // Agent 1 can submit task to Agent 2
        var taskResponse = await _client.PostAsJsonAsync(
            $"{host2.BaseUrl}/a2a/tasks",
            new { title = "Build the API", description = "Based on planner output" });

        Assert.True(taskResponse.IsSuccessStatusCode);

        await host1.StopAsync();
        await host2.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await Task.CompletedTask;
    }
}
```

**Step 2: Run test**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "AgentEndpointE2ETests"`
Expected: PASS — two agents can discover and call each other via HTTP

**Step 3: Commit**

```bash
git add project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentEndpointE2ETests.cs
git commit -m "test(rfc-001): add e2e test for agent discovery and task submission"
```

---

## Task 10: Final Verification and Cleanup

**Step 1: Run full test suite**

```bash
dotnet test project/dotnet/SwarmAssistant.sln
```
Expected: All tests PASS

**Step 2: Run linter**

```bash
task lint
```
Expected: No new warnings

**Step 3: Run pre-commit hooks**

```bash
pre-commit run --all-files
```
Expected: All hooks pass

**Step 4: Verify backward compatibility**

```bash
# Without agent endpoints (existing behavior unchanged)
DOTNET_ENVIRONMENT=Local \
  dotnet run --project project/dotnet/src/SwarmAssistant.Runtime --no-launch-profile
```

Verify: runtime starts normally, existing A2A endpoints work, no agent endpoints spawned.

**Step 5: Final commit if any cleanup needed**

```bash
git commit -m "refactor(rfc-001): cleanup and final verification"
```

---

## Summary

| Task | What | New Files | Modified Files |
|------|------|-----------|----------------|
| 1 | Config options | ConfigurationTests.cs | RuntimeOptions.cs |
| 2 | AgentCard model | AgentCard.cs, AgentCardTests.cs | — |
| 3 | Endpoint host | AgentEndpointHost.cs, tests | — |
| 4 | Port allocator | PortAllocator.cs, tests | — |
| 5 | Agent identity | — | SwarmAgentActor.cs, SwarmMessages.cs, tests |
| 6 | Wire endpoint into actor | integration tests | SwarmAgentActor.cs |
| 7 | Bootstrap wiring | — | Worker.cs |
| 8 | Documentation | — | AGENTS.md, READMEs |
| 9 | E2E dogfood test | AgentEndpointE2ETests.cs | — |
| 10 | Final verification | — | — |

**Total: 10 tasks, ~6 new files, ~5 modified files, TDD throughout.**
