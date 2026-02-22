using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Actors;

public sealed class CapabilityRegistryActor : ReceiveActor, IWithTimers
{
    private const int DefaultContractNetTimeoutMs = 500;

    private readonly ILogger _logger;
    private readonly Dictionary<IActorRef, AgentCapabilityAdvertisement> _agents = new();
    private readonly Dictionary<Guid, ContractNetAuctionState> _auctions = new();
    public ITimerScheduler Timers { get; set; } = null!;

    public CapabilityRegistryActor(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<CapabilityRegistryActor>();

        Receive<AgentCapabilityAdvertisement>(HandleCapabilityAdvertisement);
        Receive<GetBestAgentForRole>(HandleGetBestAgentForRole);
        Receive<GetCapabilitySnapshot>(_ => Sender.Tell(new CapabilitySnapshot(_agents.Values.ToArray())));
        Receive<ExecuteRoleTask>(HandleExecuteRoleTask);
        Receive<ContractNetCallForProposals>(HandleContractNetCallForProposals);
        Receive<ContractNetBid>(HandleContractNetBid);
        Receive<ContractNetFinalize>(HandleContractNetFinalize);
        Receive<Terminated>(HandleTerminated);
    }

    private void HandleCapabilityAdvertisement(AgentCapabilityAdvertisement message)
    {
        if (!_agents.ContainsKey(Sender))
        {
            Context.Watch(Sender);
        }

        _agents[Sender] = message;
    }

    private void HandleGetBestAgentForRole(GetBestAgentForRole message)
    {
        Sender.Tell(new BestAgentForRole(message.Role, SelectAgent(message.Role)));
    }

    private void HandleExecuteRoleTask(ExecuteRoleTask message)
    {
        var candidate = SelectAgent(message.Role);
        if (candidate is null)
        {
            var error = $"No capable swarm agent available for role {message.Role}";
            _logger.LogWarning(error);
            Sender.Tell(new RoleTaskFailed(
                message.TaskId,
                message.Role,
                error,
                DateTimeOffset.UtcNow));
            return;
        }

        if (_agents.TryGetValue(candidate, out var advertised))
        {
            _agents[candidate] = advertised with { CurrentLoad = advertised.CurrentLoad + 1 };
        }

        candidate.Tell(message, Sender);
    }

    private void HandleContractNetCallForProposals(ContractNetCallForProposals message)
    {
        var candidates = _agents
            .Where(static pair => pair.Key != ActorRefs.Nobody)
            .Where(pair => pair.Value.Capabilities.Contains(message.Role))
            .Select(pair => pair.Key)
            .ToArray();

        var auctionId = Guid.NewGuid();
        if (candidates.Length == 0)
        {
            Sender.Tell(new ContractNetNoBid(auctionId, message.TaskId, message.Role, "No capable agents available"));
            return;
        }

        var timeout = message.Timeout <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(DefaultContractNetTimeoutMs)
            : message.Timeout;

        var state = new ContractNetAuctionState(
            Sender,
            candidates.ToHashSet());
        _auctions[auctionId] = state;

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        foreach (var candidate in candidates)
        {
            candidate.Tell(new ContractNetBidRequest(
                auctionId,
                message.TaskId,
                message.Role,
                message.Description,
                deadline), Self);
        }

        Timers.StartSingleTimer($"contract-net-{auctionId}", new ContractNetFinalize(auctionId, message.TaskId, message.Role), timeout);
    }

    private void HandleContractNetBid(ContractNetBid bid)
    {
        if (!_auctions.TryGetValue(bid.AuctionId, out var state))
        {
            return;
        }

        if (!state.Candidates.Contains(Sender))
        {
            return;
        }

        state.Bids[Sender] = bid;
    }

    private void HandleContractNetFinalize(ContractNetFinalize finalize)
    {
        if (!_auctions.Remove(finalize.AuctionId, out var state))
        {
            return;
        }

        // Contract Net Protocol winner selection: choose the lowest-cost bid first,
        // then shortest estimate, and finally stable lexical actor-path ordering.
        var winner = state.Bids
            .OrderBy(pair => pair.Value.EstimatedCost)
            .ThenBy(pair => pair.Value.EstimatedTimeMs)
            .ThenBy(pair => pair.Value.FromAgent, StringComparer.Ordinal)
            .FirstOrDefault();

        if (winner.Equals(default(KeyValuePair<IActorRef, ContractNetBid>)))
        {
            state.Requester.Tell(new ContractNetNoBid(finalize.AuctionId, finalize.TaskId, finalize.Role, "No bids received"));
            return;
        }

        var award = new ContractNetAward(
            finalize.AuctionId,
            finalize.TaskId,
            finalize.Role,
            winner.Value.FromAgent);

        winner.Key.Tell(award, Self);
        state.Requester.Tell(award, Self);
    }

    private IActorRef? SelectAgent(SwarmRole role)
    {
        return _agents
            .Where(static pair => pair.Key != ActorRefs.Nobody)
            .Where(pair => pair.Value.Capabilities.Contains(role))
            .OrderBy(pair => pair.Value.CurrentLoad)
            .ThenBy(pair => pair.Value.ActorPath, StringComparer.Ordinal)
            .Select(pair => pair.Key)
            .FirstOrDefault();
    }

    private void HandleTerminated(Terminated message)
    {
        _agents.Remove(message.ActorRef);
    }

    private sealed class ContractNetAuctionState(
        IActorRef requester,
        HashSet<IActorRef> candidates)
    {
        public IActorRef Requester { get; } = requester;

        public HashSet<IActorRef> Candidates { get; } = candidates;

        public Dictionary<IActorRef, ContractNetBid> Bids { get; } = new();
    }
}
