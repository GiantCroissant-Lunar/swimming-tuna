using Akka.Actor;
using Akka.TestKit.Xunit2;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime.Tests;

public sealed class SwarmAgentActorTests : TestKit
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder => { });

    private RuntimeOptions CreateRuntimeOptions()
    {
        return new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "subscription-cli-fallback",
            CliAdapterOrder = ["local-echo"],
            SandboxMode = "host"
        };
    }

    [Fact]
    public void RegistersCapabilitiesOnStart()
    {
        var registry = Sys.ActorOf(Props.Create(() => new CapabilityRegistryActor(_loggerFactory)));
        var telemetry = new RuntimeTelemetry(CreateRuntimeOptions(), _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(CreateRuntimeOptions(), _loggerFactory, telemetry);

        _ = Sys.ActorOf(Props.Create(() => new SwarmAgentActor(
            CreateRuntimeOptions(),
            _loggerFactory,
            engine,
            telemetry,
            registry,
            new[] { SwarmRole.Planner, SwarmRole.Reviewer },
            default(TimeSpan),
            null)));

        AwaitAssert(() =>
        {
            registry.Tell(new GetCapabilitySnapshot());
            var snapshot = ExpectMsg<CapabilitySnapshot>();
            Assert.Contains(snapshot.Agents, a => a.Capabilities.Contains(SwarmRole.Planner));
            Assert.Contains(snapshot.Agents, a => a.Capabilities.Contains(SwarmRole.Reviewer));
        }, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void RoutesRolesDynamicallyThroughRegistry()
    {
        var options = CreateRuntimeOptions();
        var registry = Sys.ActorOf(Props.Create(() => new CapabilityRegistryActor(_loggerFactory)));
        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);

        _ = Sys.ActorOf(Props.Create(() => new SwarmAgentActor(
            options,
            _loggerFactory,
            engine,
            telemetry,
            registry,
            new[] { SwarmRole.Planner, SwarmRole.Researcher },
            default(TimeSpan),
            null)));
        AwaitAgentRegistration(registry);

        registry.Tell(new ExecuteRoleTask("task-plan", SwarmRole.Planner, "Plan", "desc", null, null));
        var planResult = ExpectMsg<RoleTaskSucceeded>();
        Assert.Equal(SwarmRole.Planner, planResult.Role);

        registry.Tell(new ExecuteRoleTask("task-research", SwarmRole.Researcher, "Research", "desc", null, null));
        var researchResult = ExpectMsg<RoleTaskSucceeded>();
        Assert.Equal(SwarmRole.Researcher, researchResult.Role);
    }

    [Theory]
    [InlineData(SwarmRole.Planner)]
    [InlineData(SwarmRole.Builder)]
    [InlineData(SwarmRole.Reviewer)]
    [InlineData(SwarmRole.Orchestrator)]
    public void ExistingCoreRolesRemainSupported(SwarmRole role)
    {
        var options = CreateRuntimeOptions();
        var registry = Sys.ActorOf(Props.Create(() => new CapabilityRegistryActor(_loggerFactory)));
        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);

        _ = Sys.ActorOf(Props.Create(() => new SwarmAgentActor(
            options,
            _loggerFactory,
            engine,
            telemetry,
            registry,
            new[] { SwarmRole.Planner, SwarmRole.Builder, SwarmRole.Reviewer, SwarmRole.Orchestrator },
            default(TimeSpan),
            null)));
        AwaitAgentRegistration(registry);

        registry.Tell(new ExecuteRoleTask(
            $"task-{role}",
            role,
            "Task",
            "Description",
            "plan",
            "build",
            "ACTION: Plan\nREASON: start"));

        var result = ExpectMsg<RoleTaskSucceeded>();
        Assert.Equal(role, result.Role);
    }

    [Fact]
    public void AcceptsNegotiationOffer_WhenRoleIsSupported()
    {
        var options = CreateRuntimeOptions();
        var registry = Sys.ActorOf(Props.Create(() => new CapabilityRegistryActor(_loggerFactory)));
        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);

        var agent = Sys.ActorOf(Props.Create(() => new SwarmAgentActor(
            options,
            _loggerFactory,
            engine,
            telemetry,
            registry,
            new[] { SwarmRole.Builder },
            default(TimeSpan),
            null)));
        AwaitAgentRegistration(registry);

        agent.Tell(new NegotiationOffer("task-negotiation", SwarmRole.Builder, "peer-a"));
        var accepted = ExpectMsg<NegotiationAccept>();
        Assert.Equal("task-negotiation", accepted.TaskId);
    }

    [Fact]
    public void RespondsToHelpRequest()
    {
        var options = CreateRuntimeOptions();
        var registry = Sys.ActorOf(Props.Create(() => new CapabilityRegistryActor(_loggerFactory)));
        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);

        var agent = Sys.ActorOf(Props.Create(() => new SwarmAgentActor(
            options,
            _loggerFactory,
            engine,
            telemetry,
            registry,
            new[] { SwarmRole.Builder },
            default(TimeSpan),
            null)));
        AwaitAgentRegistration(registry);

        agent.Tell(new HelpRequest("task-help", "Need additional implementation support", "peer-a"));
        var response = ExpectMsg<HelpResponse>();
        Assert.Equal("task-help", response.TaskId);
        Assert.Contains("Need additional implementation support", response.Output);
        Assert.False(string.IsNullOrWhiteSpace(response.FromAgent));
    }

    [Fact]
    public void ContractNet_AwardsBestBidder()
    {
        var registry = Sys.ActorOf(Props.Create(() => new CapabilityRegistryActor(_loggerFactory)));
        var fastBidder = CreateTestProbe("fast-bidder");
        var slowBidder = CreateTestProbe("slow-bidder");

        registry.Tell(new AgentCapabilityAdvertisement(
            fastBidder.Ref.Path.ToStringWithoutAddress(),
            new[] { SwarmRole.Builder },
            CurrentLoad: 0), fastBidder.Ref);
        registry.Tell(new AgentCapabilityAdvertisement(
            slowBidder.Ref.Path.ToStringWithoutAddress(),
            new[] { SwarmRole.Builder },
            CurrentLoad: 0), slowBidder.Ref);

        registry.Tell(new ContractNetCallForProposals(
            "task-cnp",
            SwarmRole.Builder,
            "Implement feature",
            TimeSpan.FromSeconds(1)));

        var fastRequest = fastBidder.ExpectMsg<ContractNetBidRequest>();
        var slowRequest = slowBidder.ExpectMsg<ContractNetBidRequest>();

        fastBidder.Reply(new ContractNetBid(
            fastRequest.AuctionId,
            fastRequest.TaskId,
            fastRequest.Role,
            fastBidder.Ref.Path.ToStringWithoutAddress(),
            EstimatedCost: 1,
            EstimatedTimeMs: 100));
        slowBidder.Reply(new ContractNetBid(
            slowRequest.AuctionId,
            slowRequest.TaskId,
            slowRequest.Role,
            slowBidder.Ref.Path.ToStringWithoutAddress(),
            EstimatedCost: 3,
            EstimatedTimeMs: 500));

        var award = ExpectMsg<ContractNetAward>(TimeSpan.FromSeconds(2));
        Assert.Equal(fastBidder.Ref.Path.ToStringWithoutAddress(), award.AwardedAgent);
    }

    [Fact]
    public void ContractNet_FinalizesEarlyWhenAllBidsArrive()
    {
        var registry = Sys.ActorOf(Props.Create(() => new CapabilityRegistryActor(_loggerFactory)));
        var firstBidder = CreateTestProbe("first-bidder");
        var secondBidder = CreateTestProbe("second-bidder");

        registry.Tell(new AgentCapabilityAdvertisement(
            firstBidder.Ref.Path.ToStringWithoutAddress(),
            new[] { SwarmRole.Builder },
            CurrentLoad: 0), firstBidder.Ref);
        registry.Tell(new AgentCapabilityAdvertisement(
            secondBidder.Ref.Path.ToStringWithoutAddress(),
            new[] { SwarmRole.Builder },
            CurrentLoad: 0), secondBidder.Ref);

        registry.Tell(new ContractNetCallForProposals(
            "task-cnp-early",
            SwarmRole.Builder,
            "Implement feature",
            TimeSpan.FromSeconds(10)));

        var firstRequest = firstBidder.ExpectMsg<ContractNetBidRequest>();
        var secondRequest = secondBidder.ExpectMsg<ContractNetBidRequest>();

        firstBidder.Reply(new ContractNetBid(
            firstRequest.AuctionId,
            firstRequest.TaskId,
            firstRequest.Role,
            firstBidder.Ref.Path.ToStringWithoutAddress(),
            EstimatedCost: 1,
            EstimatedTimeMs: 100));
        secondBidder.Reply(new ContractNetBid(
            secondRequest.AuctionId,
            secondRequest.TaskId,
            secondRequest.Role,
            secondBidder.Ref.Path.ToStringWithoutAddress(),
            EstimatedCost: 2,
            EstimatedTimeMs: 200));

        var award = ExpectMsg<ContractNetAward>(TimeSpan.FromSeconds(1));
        Assert.Equal(firstBidder.Ref.Path.ToStringWithoutAddress(), award.AwardedAgent);
    }

    [Fact]
    public void Agent_HasUniqueIdentity()
    {
        var options = CreateRuntimeOptions();
        var registry = Sys.ActorOf(Props.Create(() => new CapabilityRegistryActor(_loggerFactory)));
        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);

        const string explicitId = "test-agent-42";
        _ = Sys.ActorOf(Props.Create(() => new SwarmAgentActor(
            options,
            _loggerFactory,
            engine,
            telemetry,
            registry,
            new[] { SwarmRole.Planner },
            default,
            explicitId)));

        AwaitAssert(() =>
        {
            registry.Tell(new GetCapabilitySnapshot());
            var snapshot = ExpectMsg<CapabilitySnapshot>();
            Assert.Contains(snapshot.Agents, a => a.AgentId == explicitId);
        }, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void Agent_GeneratesUniqueIdentityWhenNotProvided()
    {
        var options = CreateRuntimeOptions();
        var registry = Sys.ActorOf(Props.Create(() => new CapabilityRegistryActor(_loggerFactory)));
        var telemetry = new RuntimeTelemetry(options, _loggerFactory);
        var engine = new AgentFrameworkRoleEngine(options, _loggerFactory, telemetry);

        _ = Sys.ActorOf(Props.Create(() => new SwarmAgentActor(
            options,
            _loggerFactory,
            engine,
            telemetry,
            registry,
            new[] { SwarmRole.Builder },
            default(TimeSpan),
            null)));

        AwaitAssert(() =>
        {
            registry.Tell(new GetCapabilitySnapshot());
            var snapshot = ExpectMsg<CapabilitySnapshot>();
            Assert.Single(snapshot.Agents);
            Assert.NotNull(snapshot.Agents[0].AgentId);
            Assert.StartsWith("agent-", snapshot.Agents[0].AgentId!);
            Assert.Equal(16, snapshot.Agents[0].AgentId!.Length);
        }, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100));
    }

    private void AwaitAgentRegistration(IActorRef registry)
    {
        AwaitAssert(() =>
        {
            registry.Tell(new GetCapabilitySnapshot());
            var snapshot = ExpectMsg<CapabilitySnapshot>();
            Assert.NotEmpty(snapshot.Agents);
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
