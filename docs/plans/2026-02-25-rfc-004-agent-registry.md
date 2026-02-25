# RFC-004: Agent Registry & Discovery — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Evolve `CapabilityRegistryActor` into a full Agent Registry with heartbeat health tracking, lifecycle signals, HTTP discovery endpoint, and a dashboard surface (RFC-008 Layer 1).

**Architecture:** Extend the existing `CapabilityRegistryActor` in-place — enrich registration data with provider/budget/health metadata, add heartbeat-based eviction, publish agent lifecycle events to the global blackboard, expose an HTTP query endpoint, and emit an AG-UI dashboard surface so the Godot UI can show the live agent list. Dogfooding: every task is verified by running the actual system.

**Tech Stack:** C# / .NET 9, Akka.NET actors, ASP.NET Core minimal API, xUnit + Akka.TestKit

**Dogfooding principle:** After each major task group, verify by booting the runtime (`task run:aspire` or `dotnet run`) and hitting the endpoints or observing AG-UI events.

---

## Task 1: Add Registry Contract Types

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Contracts/Messaging/ProviderInfo.cs`
- Create: `project/dotnet/src/SwarmAssistant.Contracts/Messaging/BudgetEnvelope.cs`
- Create: `project/dotnet/src/SwarmAssistant.Contracts/Messaging/CircuitBreakerState.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryTests.cs`

**Step 1: Write failing test — contract types exist and serialize**

```csharp
// AgentRegistryTests.cs
using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Tests;

public sealed class AgentRegistryContractTests
{
    [Fact]
    public void ProviderInfo_RoundTrips()
    {
        var info = new ProviderInfo
        {
            Adapter = "copilot",
            Type = "subscription",
            Plan = "github-copilot-business"
        };
        info.Adapter.Should().Be("copilot");
        info.Type.Should().Be("subscription");
        info.Plan.Should().Be("github-copilot-business");
    }

    [Fact]
    public void BudgetEnvelope_Defaults()
    {
        var budget = new BudgetEnvelope();
        budget.Type.Should().Be(BudgetType.Unlimited);
        budget.TotalTokens.Should().Be(0);
        budget.UsedTokens.Should().Be(0);
        budget.WarningThreshold.Should().Be(0.8);
        budget.HardLimit.Should().Be(1.0);
    }

    [Fact]
    public void BudgetEnvelope_RemainingFraction()
    {
        var budget = new BudgetEnvelope
        {
            Type = BudgetType.TokenLimited,
            TotalTokens = 500_000,
            UsedTokens = 400_000
        };
        budget.RemainingFraction.Should().BeApproximately(0.2, 0.001);
    }

    [Theory]
    [InlineData(BudgetType.TokenLimited)]
    [InlineData(BudgetType.RateLimited)]
    [InlineData(BudgetType.Unlimited)]
    [InlineData(BudgetType.PayPerUse)]
    public void BudgetType_AllValuesExist(BudgetType type)
    {
        Enum.IsDefined(type).Should().BeTrue();
    }

    [Theory]
    [InlineData(CircuitBreakerState.Closed)]
    [InlineData(CircuitBreakerState.Open)]
    [InlineData(CircuitBreakerState.HalfOpen)]
    public void CircuitBreakerState_AllValuesExist(CircuitBreakerState state)
    {
        Enum.IsDefined(state).Should().BeTrue();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~AgentRegistryContractTests" --no-restore`
Expected: FAIL — types do not exist

**Step 3: Implement the contract types**

```csharp
// ProviderInfo.cs
namespace SwarmAssistant.Contracts.Messaging;

public sealed record ProviderInfo
{
    public string Adapter { get; init; } = "unknown";
    public string Type { get; init; } = "subscription";
    public string? Plan { get; init; }
}
```

```csharp
// BudgetEnvelope.cs
namespace SwarmAssistant.Contracts.Messaging;

public enum BudgetType { Unlimited, TokenLimited, RateLimited, PayPerUse }

public sealed record BudgetEnvelope
{
    public BudgetType Type { get; init; } = BudgetType.Unlimited;
    public long TotalTokens { get; init; }
    public long UsedTokens { get; init; }
    public double WarningThreshold { get; init; } = 0.8;
    public double HardLimit { get; init; } = 1.0;

    public double RemainingFraction => TotalTokens > 0
        ? 1.0 - ((double)UsedTokens / TotalTokens)
        : 1.0;
}
```

```csharp
// CircuitBreakerState.cs
namespace SwarmAssistant.Contracts.Messaging;

public enum CircuitBreakerState { Closed, Open, HalfOpen }
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~AgentRegistryContractTests" --no-restore`
Expected: PASS

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Contracts/Messaging/ProviderInfo.cs \
        project/dotnet/src/SwarmAssistant.Contracts/Messaging/BudgetEnvelope.cs \
        project/dotnet/src/SwarmAssistant.Contracts/Messaging/CircuitBreakerState.cs \
        project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryTests.cs
git commit -m "feat(rfc-004): add ProviderInfo, BudgetEnvelope, CircuitBreakerState contracts"
```

---

## Task 2: Enrich AgentCapabilityAdvertisement

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Contracts/Messaging/AgentCapability.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryTests.cs` (append)

**Step 1: Write failing test — new fields on advertisement**

```csharp
// Append to AgentRegistryTests.cs (new class)
public sealed class AgentCapabilityAdvertisementTests
{
    [Fact]
    public void Advertisement_SupportsProviderInfo()
    {
        var ad = new AgentCapabilityAdvertisement(
            "akka://test/user/agent-01",
            new[] { SwarmRole.Builder },
            0,
            agentId: "builder-01",
            endpointUrl: "http://localhost:8001")
        {
            Provider = new ProviderInfo { Adapter = "copilot", Type = "subscription" },
            SandboxLevel = SandboxLevel.Container,
            Budget = new BudgetEnvelope { Type = BudgetType.Unlimited }
        };

        ad.Provider!.Adapter.Should().Be("copilot");
        ad.SandboxLevel.Should().Be(SandboxLevel.Container);
        ad.Budget!.Type.Should().Be(BudgetType.Unlimited);
    }

    [Fact]
    public void Advertisement_NewFieldsDefaultToNull()
    {
        var ad = new AgentCapabilityAdvertisement(
            "akka://test/user/agent-02",
            new[] { SwarmRole.Planner },
            0);

        ad.Provider.Should().BeNull();
        ad.SandboxLevel.Should().Be(SandboxLevel.BareCli);
        ad.Budget.Should().BeNull();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~AgentCapabilityAdvertisementTests" --no-restore`
Expected: FAIL — properties do not exist

**Step 3: Add new fields to AgentCapabilityAdvertisement**

Add init-only properties to the existing record in `AgentCapability.cs`:

```csharp
public sealed record AgentCapabilityAdvertisement(
    string ActorPath,
    IReadOnlyList<SwarmRole> Capabilities,
    int CurrentLoad,
    string? AgentId = null,
    string? EndpointUrl = null
)
{
    public ProviderInfo? Provider { get; init; }
    public SandboxLevel SandboxLevel { get; init; } = SandboxLevel.BareCli;
    public BudgetEnvelope? Budget { get; init; }

    public AgentCapabilityAdvertisement(
        string actorPath,
        SwarmRole[] capabilities,
        int currentLoad,
        string? agentId = null,
        string? endpointUrl = null)
        : this(
            actorPath,
            new ReadOnlyCollection<SwarmRole>((SwarmRole[])capabilities.Clone()),
            currentLoad,
            agentId,
            endpointUrl)
    {
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~AgentCapabilityAdvertisementTests" --no-restore`
Expected: PASS

**Step 5: Run all existing tests to check for regressions**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --no-restore`
Expected: All existing tests PASS (new fields have defaults so no breakage)

**Step 6: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Contracts/Messaging/AgentCapability.cs \
        project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryTests.cs
git commit -m "feat(rfc-004): enrich AgentCapabilityAdvertisement with provider, sandbox, budget"
```

---

## Task 3: Add Registry Messages and Lifecycle Blackboard Keys

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Contracts/Messaging/AgentRegistryMessages.cs`
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/GlobalBlackboardKeys.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryTests.cs` (append)

**Step 1: Write failing test — new messages compile and blackboard keys exist**

```csharp
// Append to AgentRegistryTests.cs
public sealed class AgentRegistryMessageTests
{
    [Fact]
    public void AgentHeartbeat_CarriesAgentId()
    {
        var hb = new AgentHeartbeat("builder-01");
        hb.AgentId.Should().Be("builder-01");
    }

    [Fact]
    public void DeregisterAgent_CarriesAgentId()
    {
        var msg = new DeregisterAgent("builder-01");
        msg.AgentId.Should().Be("builder-01");
    }

    [Fact]
    public void QueryAgents_SupportsCapabilityFilter()
    {
        var q = new QueryAgents(
            new[] { SwarmRole.Builder },
            Prefer: "cheapest");
        q.Capabilities.Should().Contain(SwarmRole.Builder);
        q.Prefer.Should().Be("cheapest");
    }

    [Fact]
    public void QueryAgents_AllowsNullFilter()
    {
        var q = new QueryAgents(null, null);
        q.Capabilities.Should().BeNull();
        q.Prefer.Should().BeNull();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~AgentRegistryMessageTests" --no-restore`
Expected: FAIL

**Step 3: Create message types and add blackboard keys**

```csharp
// AgentRegistryMessages.cs
namespace SwarmAssistant.Contracts.Messaging;

public sealed record AgentHeartbeat(string AgentId);

public sealed record DeregisterAgent(string AgentId);

public sealed record QueryAgents(
    IReadOnlyList<SwarmRole>? Capabilities,
    string? Prefer);

public sealed record QueryAgentsResult(
    IReadOnlyList<AgentRegistryEntry> Agents);

public sealed record AgentRegistryEntry(
    string AgentId,
    string ActorPath,
    IReadOnlyList<SwarmRole> Capabilities,
    int CurrentLoad,
    string? EndpointUrl,
    ProviderInfo? Provider,
    SandboxLevel SandboxLevel,
    BudgetEnvelope? Budget,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastHeartbeat,
    int ConsecutiveFailures,
    CircuitBreakerState CircuitBreakerState);
```

Add to `GlobalBlackboardKeys.cs`:

```csharp
internal const string AgentJoinedPrefix     = "agent_joined:";
internal const string AgentLeftPrefix       = "agent_left:";

internal static string AgentJoined(string agentId) => $"{AgentJoinedPrefix}{agentId}";
internal static string AgentLeft(string agentId)   => $"{AgentLeftPrefix}{agentId}";
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~AgentRegistryMessageTests" --no-restore`
Expected: PASS

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Contracts/Messaging/AgentRegistryMessages.cs \
        project/dotnet/src/SwarmAssistant.Runtime/Actors/GlobalBlackboardKeys.cs \
        project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryTests.cs
git commit -m "feat(rfc-004): add registry messages and agent lifecycle blackboard keys"
```

---

## Task 4: Evolve CapabilityRegistryActor into AgentRegistryActor

This is the core task. Rename the actor, add heartbeat tracking, eviction, lifecycle signals, and query support. Preserve all existing Contract Net behavior.

**Files:**
- Rename: `project/dotnet/src/SwarmAssistant.Runtime/Actors/CapabilityRegistryActor.cs` → `AgentRegistryActor.cs`
- Modify: all files that reference `CapabilityRegistryActor` (update class name)
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryTests.cs` (append)

**Step 1: Write failing tests — heartbeat, eviction, lifecycle signals, query**

```csharp
// Append to AgentRegistryTests.cs
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;

public sealed class AgentRegistryActorTests : TestKit
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => b.AddDebug());

    [Fact]
    public void Register_PublishesAgentJoinedToEventStream()
    {
        var blackboard = Sys.ActorOf(Props.Create(() =>
            new BlackboardActor(_loggerFactory)));
        var registry = Sys.ActorOf(Props.Create(() =>
            new AgentRegistryActor(_loggerFactory, blackboard)));

        // Subscribe to global blackboard changes
        var probe = CreateTestProbe();
        Sys.EventStream.Subscribe(probe, typeof(GlobalBlackboardChanged));

        var ad = new AgentCapabilityAdvertisement(
            "akka://test/user/agent-01",
            new[] { SwarmRole.Builder },
            0,
            agentId: "builder-01");
        registry.Tell(ad, TestActor);

        // Should receive agent.joined signal
        var changed = probe.ExpectMsg<GlobalBlackboardChanged>(TimeSpan.FromSeconds(3));
        changed.Key.Should().StartWith("agent_joined:");
    }

    [Fact]
    public void Heartbeat_UpdatesLastHeartbeatTime()
    {
        var blackboard = Sys.ActorOf(Props.Create(() =>
            new BlackboardActor(_loggerFactory)));
        var registry = Sys.ActorOf(Props.Create(() =>
            new AgentRegistryActor(_loggerFactory, blackboard)));

        // Register agent first
        var ad = new AgentCapabilityAdvertisement(
            "akka://test/user/agent-01",
            new[] { SwarmRole.Builder },
            0,
            agentId: "builder-01");
        registry.Tell(ad, TestActor);

        // Send heartbeat
        registry.Tell(new AgentHeartbeat("builder-01"));

        // Query should return agent with recent heartbeat
        registry.Tell(new QueryAgents(null, null));
        var result = ExpectMsg<QueryAgentsResult>();
        result.Agents.Should().ContainSingle(a => a.AgentId == "builder-01");
        result.Agents[0].ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public void Deregister_PublishesAgentLeftToEventStream()
    {
        var blackboard = Sys.ActorOf(Props.Create(() =>
            new BlackboardActor(_loggerFactory)));
        var registry = Sys.ActorOf(Props.Create(() =>
            new AgentRegistryActor(_loggerFactory, blackboard)));

        var probe = CreateTestProbe();
        Sys.EventStream.Subscribe(probe, typeof(GlobalBlackboardChanged));

        // Register
        var ad = new AgentCapabilityAdvertisement(
            "akka://test/user/agent-01",
            new[] { SwarmRole.Builder },
            0,
            agentId: "builder-01");
        registry.Tell(ad, TestActor);
        probe.ExpectMsg<GlobalBlackboardChanged>(); // consume joined

        // Deregister
        registry.Tell(new DeregisterAgent("builder-01"));

        var changed = probe.ExpectMsg<GlobalBlackboardChanged>(TimeSpan.FromSeconds(3));
        changed.Key.Should().StartWith("agent_left:");
    }

    [Fact]
    public void Query_FiltersByCapability()
    {
        var blackboard = Sys.ActorOf(Props.Create(() =>
            new BlackboardActor(_loggerFactory)));
        var registry = Sys.ActorOf(Props.Create(() =>
            new AgentRegistryActor(_loggerFactory, blackboard)));

        // Register two agents with different capabilities
        var probe1 = CreateTestProbe();
        var probe2 = CreateTestProbe();

        registry.Tell(new AgentCapabilityAdvertisement(
            probe1.Ref.Path.ToString(),
            new[] { SwarmRole.Builder },
            0, agentId: "builder-01"), probe1);

        registry.Tell(new AgentCapabilityAdvertisement(
            probe2.Ref.Path.ToString(),
            new[] { SwarmRole.Planner },
            0, agentId: "planner-01"), probe2);

        // Query for builders only
        registry.Tell(new QueryAgents(new[] { SwarmRole.Builder }, null));
        var result = ExpectMsg<QueryAgentsResult>();
        result.Agents.Should().ContainSingle(a => a.AgentId == "builder-01");
    }

    [Fact]
    public void Query_PreferCheapest_OrdersByProviderType()
    {
        var blackboard = Sys.ActorOf(Props.Create(() =>
            new BlackboardActor(_loggerFactory)));
        var registry = Sys.ActorOf(Props.Create(() =>
            new AgentRegistryActor(_loggerFactory, blackboard)));

        var probe1 = CreateTestProbe();
        var probe2 = CreateTestProbe();

        registry.Tell(new AgentCapabilityAdvertisement(
            probe1.Ref.Path.ToString(),
            new[] { SwarmRole.Builder },
            0, agentId: "builder-api")
        {
            Provider = new ProviderInfo { Adapter = "api", Type = "api" }
        }, probe1);

        registry.Tell(new AgentCapabilityAdvertisement(
            probe2.Ref.Path.ToString(),
            new[] { SwarmRole.Builder },
            0, agentId: "builder-sub")
        {
            Provider = new ProviderInfo { Adapter = "copilot", Type = "subscription" }
        }, probe2);

        registry.Tell(new QueryAgents(new[] { SwarmRole.Builder }, "cheapest"));
        var result = ExpectMsg<QueryAgentsResult>();
        result.Agents.Should().HaveCount(2);
        result.Agents[0].AgentId.Should().Be("builder-sub"); // subscription first
    }

    [Fact]
    public void ExistingContractNet_StillWorks()
    {
        var blackboard = Sys.ActorOf(Props.Create(() =>
            new BlackboardActor(_loggerFactory)));
        var registry = Sys.ActorOf(Props.Create(() =>
            new AgentRegistryActor(_loggerFactory, blackboard)));

        // Register a bidding agent
        var bidder = CreateTestProbe();
        registry.Tell(new AgentCapabilityAdvertisement(
            bidder.Ref.Path.ToString(),
            new[] { SwarmRole.Builder },
            0, agentId: "builder-01"), bidder);

        // Call for proposals
        registry.Tell(new ContractNetCallForProposals(
            "task-01", SwarmRole.Builder, "Build something",
            TimeSpan.FromSeconds(2)));

        // Bidder receives request
        var request = bidder.ExpectMsg<ContractNetBidRequest>();
        request.Role.Should().Be(SwarmRole.Builder);

        // Bidder submits bid
        registry.Tell(new ContractNetBid(
            request.AuctionId, "task-01", SwarmRole.Builder,
            bidder.Ref.Path.Name, 1, 1000), bidder);

        // Award should arrive
        var award = ExpectMsg<ContractNetAward>();
        award.TaskId.Should().Be("task-01");
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~AgentRegistryActorTests" --no-restore`
Expected: FAIL — `AgentRegistryActor` does not exist

**Step 3: Rename and evolve the actor**

Rename `CapabilityRegistryActor.cs` → `AgentRegistryActor.cs`. Keep the class as `AgentRegistryActor` with a constructor that now takes `IActorRef blackboard`. Add:

- Internal `AgentRegistryState` record per registered agent (replaces raw `AgentCapabilityAdvertisement` values)
- `HandleHeartbeat(AgentHeartbeat)` — resets consecutive failures, updates timestamp
- `HandleDeregister(DeregisterAgent)` — removes agent, publishes `agent_left` to blackboard
- `HandleQuery(QueryAgents)` — filters by capabilities, orders by `Prefer` strategy
- On `HandleCapabilityAdvertisement`: if new agent, publish `agent_joined` to blackboard
- On `HandleTerminated`: publish `agent_left` to blackboard (ungraceful departure)
- Heartbeat eviction timer: periodic tick checks agents with `ConsecutiveFailures >= MaxMissedHeartbeats`, evicts them

Key: preserve ALL existing `SelectAgent`, `HandleExecuteRoleTask`, `HandleContractNetCallForProposals`, `HandleContractNetBid`, `HandleContractNetFinalize` logic unchanged.

**Step 4: Update all references from CapabilityRegistryActor → AgentRegistryActor**

Search and replace across the codebase. Key files:
- `SwarmAgentActor.cs` (constructor parameter name, likely just the type)
- `DispatcherActor.cs` (creates the registry)
- `Program.cs` (may wire it up)
- All test files that reference `CapabilityRegistryActor`

**Step 5: Run ALL tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --no-restore`
Expected: ALL tests PASS (both new and existing)

**Step 6: Commit**

```bash
git add -A
git commit -m "feat(rfc-004): evolve CapabilityRegistryActor into AgentRegistryActor

Add heartbeat tracking, eviction, lifecycle signals (agent.joined/agent.left
via blackboard), and capability+cost-aware query support. All existing
Contract Net behavior preserved."
```

---

## Task 5: Update SwarmAgentActor to Report Rich Metadata and Heartbeat

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/SwarmAgentActor.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryTests.cs` (append)

**Step 1: Write failing test — agent advertises with provider + sandbox + budget**

```csharp
// Append to AgentRegistryTests.cs
public sealed class SwarmAgentRegistrationTests : TestKit
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => b.AddDebug());

    [Fact]
    public void Agent_AdvertisesWithProviderInfo()
    {
        var options = new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "subscription-cli-fallback",
            CliAdapterOrder = "copilot,cline",
            SandboxMode = "host"
        };

        var blackboard = Sys.ActorOf(Props.Create(() =>
            new BlackboardActor(_loggerFactory)));
        var registry = Sys.ActorOf(Props.Create(() =>
            new AgentRegistryActor(_loggerFactory, blackboard)));
        var telemetry = new RuntimeTelemetry(options);
        var engine = new AgentFrameworkRoleEngine(
            Microsoft.Extensions.Options.Options.Create(options),
            _loggerFactory);

        var agent = Sys.ActorOf(Props.Create(() =>
            new SwarmAgentActor(
                options, _loggerFactory, engine, telemetry,
                registry, new[] { SwarmRole.Builder },
                TimeSpan.Zero, "builder-01", null)));

        // Wait for registration, then query
        AwaitAssert(() =>
        {
            registry.Tell(new QueryAgents(null, null));
            var result = ExpectMsg<QueryAgentsResult>(TimeSpan.FromSeconds(1));
            result.Agents.Should().ContainSingle(a => a.AgentId == "builder-01");
            var entry = result.Agents.First(a => a.AgentId == "builder-01");
            entry.Provider.Should().NotBeNull();
            entry.SandboxLevel.Should().Be(SandboxLevel.BareCli); // "host" maps to BareCli
        }, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(250));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~SwarmAgentRegistrationTests" --no-restore`
Expected: FAIL — provider not populated in advertisement

**Step 3: Update SwarmAgentActor.AdvertiseCapability()**

In `SwarmAgentActor.cs`, update `AdvertiseCapability()` to include:
- `Provider` from `RuntimeOptions` (derive adapter name from `CliAdapterOrder`, type from execution mode)
- `SandboxLevel` from `RuntimeOptions.SandboxLevel`
- `Budget` — default `BudgetEnvelope` with `BudgetType.Unlimited` (RFC-007 will refine)

Add a heartbeat timer in `PreStart()`:
- Use `Context.System.Scheduler.ScheduleTellRepeatedly` to send `AgentHeartbeat(_agentId)` to the registry on interval `RuntimeOptions.AgentHeartbeatIntervalSeconds`

**Step 4: Run all tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --no-restore`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Actors/SwarmAgentActor.cs \
        project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryTests.cs
git commit -m "feat(rfc-004): SwarmAgentActor reports provider, sandbox, budget and heartbeats"
```

---

## Task 6: HTTP Registry Query Endpoint

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Program.cs`
- Create: `project/dotnet/src/SwarmAssistant.Runtime/Dto/AgentRegistryDto.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryTests.cs` (append)

**Step 1: Write failing test — DTO exists and serializes**

```csharp
// Append to AgentRegistryTests.cs
public sealed class AgentRegistryDtoTests
{
    [Fact]
    public void AgentRegistryEntryDto_SerializesToCamelCase()
    {
        var dto = new AgentRegistryEntryDto
        {
            AgentId = "builder-01",
            Capabilities = new[] { "builder" },
            Status = "active",
            Provider = new ProviderInfoDto { Adapter = "copilot", Type = "subscription" },
            SandboxLevel = 2,
            EndpointUrl = "http://localhost:8001"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(dto,
            new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web));
        json.Should().Contain("\"agentId\"");
        json.Should().Contain("\"builder-01\"");
    }
}
```

**Step 2: Run test to verify it fails**

Expected: FAIL — DTO types don't exist

**Step 3: Create DTOs and add HTTP endpoint**

```csharp
// AgentRegistryDto.cs
namespace SwarmAssistant.Runtime.Dto;

public sealed record AgentRegistryEntryDto
{
    public string AgentId { get; init; } = "";
    public string[] Capabilities { get; init; } = [];
    public string Status { get; init; } = "active";
    public ProviderInfoDto? Provider { get; init; }
    public int SandboxLevel { get; init; }
    public BudgetInfoDto? Budget { get; init; }
    public string? EndpointUrl { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset LastHeartbeat { get; init; }
    public int ConsecutiveFailures { get; init; }
    public string CircuitBreakerState { get; init; } = "closed";
}

public sealed record ProviderInfoDto
{
    public string Adapter { get; init; } = "";
    public string Type { get; init; } = "";
    public string? Plan { get; init; }
}

public sealed record BudgetInfoDto
{
    public string Type { get; init; } = "unlimited";
    public long TotalTokens { get; init; }
    public long UsedTokens { get; init; }
    public double RemainingFraction { get; init; } = 1.0;
}
```

Add to `Program.cs` (next to existing A2A endpoints):

```csharp
// GET /a2a/registry/agents — query agent registry
app.MapGet("/a2a/registry/agents", async (
    HttpContext ctx,
    [FromQuery] string? capability,
    [FromQuery] string? prefer) =>
{
    // Send QueryAgents to registry actor, map result to DTOs
});
```

**Step 4: Run all tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --no-restore`
Expected: ALL PASS

**Step 5: Dogfood verification**

Boot the runtime and curl the endpoint:
```bash
dotnet run --project project/dotnet/src/SwarmAssistant.Runtime
curl http://127.0.0.1:5080/a2a/registry/agents | jq .
curl "http://127.0.0.1:5080/a2a/registry/agents?capability=builder&prefer=cheapest" | jq .
```

Expected: JSON array of registered agents with provider, sandbox, budget info.

**Step 6: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Dto/AgentRegistryDto.cs \
        project/dotnet/src/SwarmAssistant.Runtime/Program.cs \
        project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryTests.cs
git commit -m "feat(rfc-004): add GET /a2a/registry/agents HTTP discovery endpoint"
```

---

## Task 7: AG-UI Dashboard Surface — Agent List (RFC-008 Layer 1)

Emit an AG-UI event with the agent list whenever the registry changes, so the Godot UI can render it.

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/AgentRegistryActor.cs` (emit AG-UI events)
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Ui/UiEventPayloads.cs` (add payload record)
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryTests.cs` (append)

**Step 1: Write failing test — registry publishes AG-UI event on agent join**

```csharp
// Append to AgentRegistryTests.cs
public sealed class AgentRegistryDashboardTests : TestKit
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => b.AddDebug());

    [Fact]
    public void Register_EmitsAgUiDashboardEvent()
    {
        var blackboard = Sys.ActorOf(Props.Create(() =>
            new BlackboardActor(_loggerFactory)));
        var uiEvents = new UiEventStream();

        var registry = Sys.ActorOf(Props.Create(() =>
            new AgentRegistryActor(_loggerFactory, blackboard, uiEvents)));

        var ad = new AgentCapabilityAdvertisement(
            "akka://test/user/agent-01",
            new[] { SwarmRole.Builder },
            0, agentId: "builder-01")
        {
            Provider = new ProviderInfo { Adapter = "copilot", Type = "subscription" }
        };
        registry.Tell(ad, TestActor);

        // AG-UI event should appear in the stream
        AwaitAssert(() =>
        {
            var recent = uiEvents.GetRecent(10);
            recent.Should().Contain(e => e.Type == "agui.dashboard.agents");
        }, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100));
    }
}
```

**Step 2: Run test to verify it fails**

Expected: FAIL — constructor doesn't accept `UiEventStream`, no dashboard event emitted

**Step 3: Implement**

Add `UiEventStream` as an optional constructor parameter to `AgentRegistryActor`. After any state change (register, deregister, evict, heartbeat with status change), call:

```csharp
_uiEvents?.Publish("agui.dashboard.agents", null, BuildAgentListPayload());
```

Add payload record:

```csharp
// In UiEventPayloads.cs
public sealed record DashboardAgentListPayload(
    IReadOnlyList<DashboardAgentEntry> Agents,
    int ActiveCount,
    int TotalCount);

public sealed record DashboardAgentEntry(
    string AgentId,
    string Role,
    string Status,
    string? Provider,
    string? BudgetDisplay,
    string? CurrentTaskId);
```

The A2UI surface payload wraps this in the standard protocol envelope:

```csharp
new {
    protocol = "a2ui/v0.8",
    operation = "updateDataModel",
    surfaceType = "dashboard",
    layer = "agent-list",
    data = agentListPayload
}
```

**Step 4: Run all tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --no-restore`
Expected: ALL PASS

**Step 5: Dogfood verification**

Boot the runtime. Connect via SSE or check `/ag-ui/recent`:
```bash
curl http://127.0.0.1:5080/ag-ui/recent?count=10 | jq '.[] | select(.type == "agui.dashboard.agents")'
```

Expected: Dashboard agent list event in the AG-UI stream with agent entries.

**Step 6: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Actors/AgentRegistryActor.cs \
        project/dotnet/src/SwarmAssistant.Runtime/Ui/UiEventPayloads.cs \
        project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryTests.cs
git commit -m "feat(rfc-004): emit agui.dashboard.agents events for RFC-008 Layer 1"
```

---

## Task 8: Update OpenAPI Spec and Regenerate

**Files:**
- Modify: `project/docs/openapi/runtime.v1.yaml`
- Create: `project/docs/openapi/schemas/AgentRegistryEntry.schema.json`

**Step 1: Add `/a2a/registry/agents` endpoint to OpenAPI spec**

Add the path definition with query parameters (`capability`, `prefer`) and response schema referencing the new `AgentRegistryEntry` schema.

**Step 2: Create the JSON schema file**

`AgentRegistryEntry.schema.json` with all fields from `AgentRegistryEntryDto`.

**Step 3: Verify generated models match**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --no-restore`
Expected: ALL PASS (including 'Verify generated models' check if applicable)

**Step 4: Commit**

```bash
git add project/docs/openapi/runtime.v1.yaml \
        project/docs/openapi/schemas/AgentRegistryEntry.schema.json
git commit -m "docs(rfc-004): add agent registry endpoint to OpenAPI spec"
```

---

## Task 9: End-to-End Dogfood Integration Test

**Files:**
- Create: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryIntegrationTests.cs`

**Step 1: Write E2E test — full lifecycle**

```csharp
public sealed class AgentRegistryIntegrationTests : TestKit
{
    // Test: boot dispatcher with registry → spawn agents → verify they register
    //   → query registry → verify capabilities → send heartbeats
    //   → deregister → verify agent.left signal → verify AG-UI dashboard event

    [Fact]
    public void FullLifecycle_RegisterHeartbeatDeregister()
    {
        // 1. Create registry + blackboard + dispatcher
        // 2. Spawn two agents with different roles via DispatcherActor
        // 3. Await registration in registry
        // 4. Query registry, verify both agents present
        // 5. Deregister one agent
        // 6. Query registry, verify only one remains
        // 7. Verify blackboard received agent.joined and agent.left signals
    }

    [Fact]
    public void ContractNet_StillWorksWithEvolvedRegistry()
    {
        // 1. Create registry with blackboard
        // 2. Register two builder agents with different cost profiles
        // 3. Run ContractNetCallForProposals
        // 4. Verify cheapest agent wins (subscription over API)
    }
}
```

**Step 2: Implement and run**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~AgentRegistryIntegrationTests" --no-restore`
Expected: ALL PASS

**Step 3: Run full test suite**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --no-restore`
Expected: ALL tests PASS (no regressions)

**Step 4: Commit**

```bash
git add project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryIntegrationTests.cs
git commit -m "test(rfc-004): add end-to-end agent registry integration tests"
```

---

## Task 10: Final Dogfood Verification & Cleanup

**Step 1: Boot the full system**

```bash
task run:aspire
```

**Step 2: Verify via HTTP**

```bash
# Registry endpoint
curl http://127.0.0.1:5080/a2a/registry/agents | jq .

# AG-UI dashboard events
curl http://127.0.0.1:5080/ag-ui/recent?count=5 | jq '.[] | select(.type == "agui.dashboard.agents")'

# Health check
curl http://127.0.0.1:5080/healthz
```

**Step 3: Run full test suite one final time**

```bash
dotnet test project/dotnet/SwarmAssistant.sln
```

**Step 4: Commit any cleanup**

```bash
git commit -m "refactor(rfc-004): final cleanup and polish"
```

---

## Summary

| Task | Description | Dogfood Checkpoint |
|------|-------------|-------------------|
| 1 | Contract types (ProviderInfo, BudgetEnvelope) | Tests pass |
| 2 | Enrich AgentCapabilityAdvertisement | Tests pass, no regressions |
| 3 | Registry messages + blackboard keys | Tests pass |
| 4 | Evolve CapabilityRegistryActor → AgentRegistryActor | Tests pass, Contract Net preserved |
| 5 | SwarmAgentActor reports rich metadata + heartbeats | Tests pass |
| 6 | HTTP GET /a2a/registry/agents | `curl` the endpoint, see agents |
| 7 | AG-UI dashboard surface (RFC-008 L1) | See events in `/ag-ui/recent` |
| 8 | OpenAPI spec update | Schema matches implementation |
| 9 | E2E integration tests | Full lifecycle test passes |
| 10 | Final dogfood boot | System runs, endpoints respond |
