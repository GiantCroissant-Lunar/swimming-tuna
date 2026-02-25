using Akka.Actor;
using Akka.TestKit.Xunit2;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Ui;

namespace SwarmAssistant.Runtime.Tests;

public sealed class AgentRegistryActorTests : TestKit
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => b.AddDebug());

    private (IActorRef registry, IActorRef blackboard, UiEventStream uiEvents) CreateRegistry()
    {
        var blackboard = Sys.ActorOf(Props.Create(() => new BlackboardActor(_loggerFactory)));
        var uiEvents = new UiEventStream();
        var registry = Sys.ActorOf(Props.Create(() => new AgentRegistryActor(_loggerFactory, blackboard, uiEvents)));
        return (registry, blackboard, uiEvents);
    }

    [Fact]
    public void Register_PublishesAgentJoinedToEventStream()
    {
        var (registry, _, _) = CreateRegistry();

        var probe = CreateTestProbe();
        Sys.EventStream.Subscribe(probe, typeof(GlobalBlackboardChanged));

        registry.Tell(new AgentCapabilityAdvertisement(
            "akka://test/user/agent-01",
            new[] { SwarmRole.Builder },
            0, agentId: "builder-01"), TestActor);

        var changed = probe.ExpectMsg<GlobalBlackboardChanged>(TimeSpan.FromSeconds(3));
        Assert.StartsWith("agent_joined:", changed.Key);
    }

    [Fact]
    public void Heartbeat_UpdatesHealthState()
    {
        var (registry, _, _) = CreateRegistry();

        registry.Tell(new AgentCapabilityAdvertisement(
            "akka://test/user/agent-01",
            new[] { SwarmRole.Builder },
            0, agentId: "builder-01"), TestActor);

        registry.Tell(new AgentHeartbeat("builder-01"));

        registry.Tell(new QueryAgents(null, null));
        var result = ExpectMsg<QueryAgentsResult>();
        Assert.Single(result.Agents, a => a.AgentId == "builder-01");
        Assert.Equal(0, result.Agents[0].ConsecutiveFailures);
    }

    [Fact]
    public void Deregister_RemovesAgentAndPublishesAgentLeft()
    {
        var (registry, _, _) = CreateRegistry();

        var probe = CreateTestProbe();
        Sys.EventStream.Subscribe(probe, typeof(GlobalBlackboardChanged));

        registry.Tell(new AgentCapabilityAdvertisement(
            "akka://test/user/agent-01",
            new[] { SwarmRole.Builder },
            0, agentId: "builder-01"), TestActor);
        probe.ExpectMsg<GlobalBlackboardChanged>(); // consume joined

        registry.Tell(new DeregisterAgent("builder-01"));

        var changed = probe.ExpectMsg<GlobalBlackboardChanged>(TimeSpan.FromSeconds(3));
        Assert.StartsWith("agent_left:", changed.Key);

        // Verify agent is gone
        registry.Tell(new QueryAgents(null, null));
        var result = ExpectMsg<QueryAgentsResult>();
        Assert.Empty(result.Agents);
    }

    [Fact]
    public void Query_FiltersByCapability()
    {
        var (registry, _, _) = CreateRegistry();

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

        registry.Tell(new QueryAgents(new[] { SwarmRole.Builder }, null));
        var result = ExpectMsg<QueryAgentsResult>();
        Assert.Single(result.Agents);
        Assert.Equal("builder-01", result.Agents[0].AgentId);
    }

    [Fact]
    public void Query_PreferCheapest_OrdersSubscriptionFirst()
    {
        var (registry, _, _) = CreateRegistry();

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
        Assert.Equal(2, result.Agents.Count);
        Assert.Equal("builder-sub", result.Agents[0].AgentId);
    }

    [Fact]
    public void Register_EmitsAgUiDashboardEvent()
    {
        var (registry, _, uiEvents) = CreateRegistry();

        registry.Tell(new AgentCapabilityAdvertisement(
            "akka://test/user/agent-01",
            new[] { SwarmRole.Builder },
            0, agentId: "builder-01")
        {
            Provider = new ProviderInfo { Adapter = "copilot", Type = "subscription" }
        }, TestActor);

        AwaitAssert(() =>
        {
            var recent = uiEvents.GetRecent(10);
            Assert.Contains(recent, e => e.Type == "agui.dashboard.agents");
        }, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void ContractNet_StillWorksWithEvolvedRegistry()
    {
        var (registry, _, _) = CreateRegistry();

        var bidder = CreateTestProbe();
        registry.Tell(new AgentCapabilityAdvertisement(
            bidder.Ref.Path.ToString(),
            new[] { SwarmRole.Builder },
            0, agentId: "builder-01"), bidder);

        registry.Tell(new ContractNetCallForProposals(
            "task-01", SwarmRole.Builder, "Build something",
            TimeSpan.FromSeconds(2)));

        var request = bidder.ExpectMsg<ContractNetBidRequest>();
        Assert.Equal(SwarmRole.Builder, request.Role);

        registry.Tell(new ContractNetBid(
            request.AuctionId, "task-01", SwarmRole.Builder,
            bidder.Ref.Path.Name, 1, 1000), bidder);

        var award = ExpectMsg<ContractNetAward>();
        Assert.Equal("task-01", award.TaskId);
    }

    [Fact]
    public void Query_ReturnsAllAgentsWhenNullFilter()
    {
        var (registry, _, _) = CreateRegistry();

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

        registry.Tell(new QueryAgents(null, null));
        var result = ExpectMsg<QueryAgentsResult>();
        Assert.Equal(2, result.Agents.Count);
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
