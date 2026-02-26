using Akka.Actor;
using Akka.TestKit.Xunit2;
using FluentAssertions;
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
        var registry = Sys.ActorOf(Props.Create(() => new AgentRegistryActor(_loggerFactory, blackboard, uiEvents, 30)));
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
    public void ExecuteRoleTask_SkipsExhaustedBudgetAgents()
    {
        var (registry, _, _) = CreateRegistry();

        var exhausted = CreateTestProbe();
        var active = CreateTestProbe();

        registry.Tell(new AgentCapabilityAdvertisement(
            exhausted.Ref.Path.ToString(),
            new[] { SwarmRole.Builder },
            0,
            agentId: "builder-exhausted")
        {
            Budget = new BudgetEnvelope
            {
                Type = BudgetType.TokenLimited,
                TotalTokens = 100,
                UsedTokens = 100,
                WarningThreshold = 0.8,
                HardLimit = 1.0
            }
        }, exhausted);

        registry.Tell(new AgentCapabilityAdvertisement(
            active.Ref.Path.ToString(),
            new[] { SwarmRole.Builder },
            0,
            agentId: "builder-active")
        {
            Budget = new BudgetEnvelope
            {
                Type = BudgetType.TokenLimited,
                TotalTokens = 100,
                UsedTokens = 20,
                WarningThreshold = 0.8,
                HardLimit = 1.0
            }
        }, active);

        registry.Tell(new ExecuteRoleTask("task-budget-1", SwarmRole.Builder, "t", "d", null, null), TestActor);

        active.ExpectMsg<ExecuteRoleTask>(TimeSpan.FromSeconds(2));
        exhausted.ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void ExecuteRoleTask_PrefersHealthyBudgetOverLowBudget()
    {
        var (registry, _, _) = CreateRegistry();

        var lowBudget = CreateTestProbe();
        var healthyBudget = CreateTestProbe();

        registry.Tell(new AgentCapabilityAdvertisement(
            lowBudget.Ref.Path.ToString(),
            new[] { SwarmRole.Builder },
            0,
            agentId: "builder-low")
        {
            Budget = new BudgetEnvelope
            {
                Type = BudgetType.TokenLimited,
                TotalTokens = 100,
                UsedTokens = 90,
                WarningThreshold = 0.8,
                HardLimit = 1.0
            }
        }, lowBudget);

        registry.Tell(new AgentCapabilityAdvertisement(
            healthyBudget.Ref.Path.ToString(),
            new[] { SwarmRole.Builder },
            0,
            agentId: "builder-healthy")
        {
            Budget = new BudgetEnvelope
            {
                Type = BudgetType.TokenLimited,
                TotalTokens = 100,
                UsedTokens = 10,
                WarningThreshold = 0.8,
                HardLimit = 1.0
            }
        }, healthyBudget);

        registry.Tell(new ExecuteRoleTask("task-budget-2", SwarmRole.Builder, "t", "d", null, null), TestActor);

        healthyBudget.ExpectMsg<ExecuteRoleTask>(TimeSpan.FromSeconds(2));
        lowBudget.ExpectNoMsg(TimeSpan.FromMilliseconds(200));

        registry.Tell(new QueryAgents(null, null));
        var allAgents = ExpectMsg<QueryAgentsResult>();
        var lowBudgetAgent = allAgents.Agents.FirstOrDefault(a => a.AgentId == "builder-low");
        Assert.NotNull(lowBudgetAgent);
        Assert.True(lowBudgetAgent.Budget?.IsLowBudget ?? false);
    }

    [Fact]
    public void ExecuteRoleTask_WhenAllCandidatesExhausted_ReturnsBudgetFailure()
    {
        var (registry, _, _) = CreateRegistry();
        var exhausted = CreateTestProbe();

        registry.Tell(new AgentCapabilityAdvertisement(
            exhausted.Ref.Path.ToString(),
            new[] { SwarmRole.Builder },
            0,
            agentId: "builder-exhausted")
        {
            Budget = new BudgetEnvelope
            {
                Type = BudgetType.TokenLimited,
                TotalTokens = 100,
                UsedTokens = 100,
                WarningThreshold = 0.8,
                HardLimit = 1.0
            }
        }, exhausted);

        registry.Tell(new ExecuteRoleTask("task-budget-3", SwarmRole.Builder, "t", "d", null, null), TestActor);

        var failed = ExpectMsg<RoleTaskFailed>(TimeSpan.FromSeconds(2));
        Assert.Contains("budget exhausted", failed.Error, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void AgentAdvertisement_IncludesProviderAndSandboxLevel()
    {
        var (registry, _, _) = CreateRegistry();

        registry.Tell(new AgentCapabilityAdvertisement(
            "akka://test/user/agent-01",
            new[] { SwarmRole.Builder },
            0,
            agentId: "rich-agent-01")
        {
            Provider = new ProviderInfo { Adapter = "openai", Type = "api" },
            SandboxLevel = SandboxLevel.Container,
            Budget = new BudgetEnvelope { Type = BudgetType.Unlimited }
        }, TestActor);

        registry.Tell(new QueryAgents(null, null));
        var result = ExpectMsg<QueryAgentsResult>();

        var agent = result.Agents.FirstOrDefault(a => a.AgentId == "rich-agent-01");
        Assert.NotNull(agent);
        Assert.NotNull(agent.Provider);
        Assert.Equal("openai", agent.Provider.Adapter);
        Assert.Equal("api", agent.Provider.Type);
        Assert.Equal(SandboxLevel.Container, agent.SandboxLevel);
    }

    [Fact]
    public void ResolvePeerAgent_ReturnsRefForRegisteredAgent()
    {
        var (registry, _, _) = CreateRegistry();
        var agentProbe = CreateTestProbe();

        registry.Tell(new AgentCapabilityAdvertisement(
            agentProbe.Ref.Path.ToString(),
            new[] { SwarmRole.Builder },
            0,
            agentId: "agent-1",
            endpointUrl: "http://localhost:5001"
        ), agentProbe);

        registry.Tell(new ResolvePeerAgent("agent-1"), TestActor);

        var response = ExpectMsg<PeerAgentResolved>();
        response.AgentId.Should().Be("agent-1");
        response.Found.Should().BeTrue();
        response.AgentRef.Should().Be(agentProbe);
        response.EndpointUrl.Should().Be("http://localhost:5001");
    }

    [Fact]
    public void ResolvePeerAgent_ReturnsNotFoundForUnknownAgent()
    {
        var (registry, _, _) = CreateRegistry();

        registry.Tell(new ResolvePeerAgent("unknown-agent"), TestActor);

        var response = ExpectMsg<PeerAgentResolved>();
        response.AgentId.Should().Be("unknown-agent");
        response.Found.Should().BeFalse();
        response.AgentRef.Should().BeNull();
        response.EndpointUrl.Should().BeNull();
    }

    [Fact]
    public void ForwardPeerMessage_DeliversToTargetAgent()
    {
        var (registry, _, _) = CreateRegistry();
        var targetProbe = CreateTestProbe();

        registry.Tell(new AgentCapabilityAdvertisement(
            targetProbe.Ref.Path.ToString(),
            new[] { SwarmRole.Builder },
            0,
            agentId: "target-agent",
            endpointUrl: "http://localhost:5001"
        ), targetProbe);

        var peerMessage = new PeerMessage(
            "msg-123",
            "source-agent",
            "target-agent",
            PeerMessageType.Broadcast,
            "test-payload"
        );

        registry.Tell(new ForwardPeerMessage(peerMessage), TestActor);

        // Registry forwards to target; ack is sent by the target actor, not the registry
        var receivedMessage = targetProbe.ExpectMsg<PeerMessage>();
        receivedMessage.MessageId.Should().Be("msg-123");
        receivedMessage.FromAgentId.Should().Be("source-agent");
        receivedMessage.ToAgentId.Should().Be("target-agent");
        receivedMessage.Payload.Should().Be("test-payload");
    }

    [Fact]
    public void ForwardPeerMessage_ReturnsNotFoundForUnknownAgent()
    {
        var (registry, _, _) = CreateRegistry();

        var peerMessage = new PeerMessage(
            "msg-456",
            "source-agent",
            "unknown-agent",
            PeerMessageType.Broadcast,
            "test-payload"
        );

        registry.Tell(new ForwardPeerMessage(peerMessage), TestActor);

        var ack = ExpectMsg<PeerMessageAck>();
        ack.MessageId.Should().Be("msg-456");
        ack.Accepted.Should().BeFalse();
        ack.Reason.Should().Be("agent_not_found");
    }

    [Fact]
    public void ForwardPeerMessage_EmitsAgUiDashboardEvent()
    {
        var (registry, _, uiEvents) = CreateRegistry();

        var targetProbe = CreateTestProbe();
        registry.Tell(new AgentCapabilityAdvertisement(
            targetProbe.Ref.Path.ToStringWithAddress(),
            new[] { SwarmRole.Builder },
            0,
            agentId: "target-agent")
        );

        var peerMessage = new SwarmAssistant.Contracts.Messaging.PeerMessage(
            MessageId: "msg-789",
            FromAgentId: "sender-agent",
            ToAgentId: "target-agent",
            Type: PeerMessageType.TaskRequest,
            Payload: "{\"message\":\"Hello\"}",
            ReplyTo: null,
            Timestamp: DateTimeOffset.UtcNow
        );

        registry.Tell(new ForwardPeerMessage(peerMessage));

        AwaitAssert(() =>
        {
            var recent = uiEvents.GetRecent(10);
            Assert.Contains(recent, e => e.Type == "agui.dashboard.messages");
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
