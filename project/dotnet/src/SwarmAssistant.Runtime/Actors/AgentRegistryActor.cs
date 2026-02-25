using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Ui;

namespace SwarmAssistant.Runtime.Actors;

public sealed class AgentRegistryActor : ReceiveActor, IWithTimers
{
    private const int DefaultContractNetTimeoutMs = 500;
    private const string ContractNetTimerPrefix = "contract-net-";
    private const string HeartbeatCheckTimerKey = "heartbeat-check";
    private const int MaxMissedHeartbeats = 3;

    private readonly ILogger _logger;
    private readonly IActorRef? _blackboard;
    private readonly UiEventStream? _uiEvents;
    private readonly int _heartbeatIntervalSeconds;
    private readonly Dictionary<IActorRef, AgentCapabilityAdvertisement> _agents = new();
    private readonly Dictionary<string, AgentHealthState> _health = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IActorRef> _agentIdToRef = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ContractNetAuctionState> _auctions = new();
    public ITimerScheduler Timers { get; set; } = null!;

    // Constructor with all dependencies (new)
    public AgentRegistryActor(ILoggerFactory loggerFactory, IActorRef? blackboard = null, UiEventStream? uiEvents = null, int heartbeatIntervalSeconds = 30)
    {
        _logger = loggerFactory.CreateLogger<AgentRegistryActor>();
        _blackboard = blackboard;
        _uiEvents = uiEvents;
        _heartbeatIntervalSeconds = heartbeatIntervalSeconds;

        // Existing message handlers (preserved)
        Receive<AgentCapabilityAdvertisement>(HandleCapabilityAdvertisement);
        Receive<GetBestAgentForRole>(HandleGetBestAgentForRole);
        Receive<GetCapabilitySnapshot>(_ => Sender.Tell(new CapabilitySnapshot(_agents.Values.ToArray())));
        Receive<ExecuteRoleTask>(HandleExecuteRoleTask);
        Receive<ContractNetCallForProposals>(HandleContractNetCallForProposals);
        Receive<ContractNetBid>(HandleContractNetBid);
        Receive<ContractNetFinalize>(HandleContractNetFinalize);
        Receive<Terminated>(HandleTerminated);

        // New message handlers (RFC-004)
        Receive<AgentHeartbeat>(HandleHeartbeat);
        Receive<DeregisterAgent>(HandleDeregister);
        Receive<QueryAgents>(HandleQuery);
        Receive<HeartbeatCheck>(_ => CheckHeartbeats());
    }

    protected override void PreStart()
    {
        base.PreStart();
        var checkInterval = TimeSpan.FromSeconds(_heartbeatIntervalSeconds);
        Timers.StartPeriodicTimer(
            HeartbeatCheckTimerKey,
            new HeartbeatCheck(),
            checkInterval,
            checkInterval);
    }

    // ── Existing handlers (preserved exactly) ──

    private void HandleCapabilityAdvertisement(AgentCapabilityAdvertisement message)
    {
        var isNew = !_agents.ContainsKey(Sender);
        if (isNew)
        {
            Context.Watch(Sender);
        }

        _agents[Sender] = message;

        // Track agent ID mapping and health
        var agentId = message.AgentId ?? Sender.Path.Name;
        if (_agentIdToRef.TryGetValue(agentId, out var existingRef) && !Equals(existingRef, Sender))
        {
            _agents.Remove(existingRef);
            Context.Unwatch(existingRef);
        }
        _agentIdToRef[agentId] = Sender;

        if (isNew)
        {
            _health[agentId] = new AgentHealthState
            {
                RegisteredAt = DateTimeOffset.UtcNow,
                LastHeartbeat = DateTimeOffset.UtcNow,
                ConsecutiveFailures = 0,
                CircuitBreakerState = CircuitBreakerState.Closed
            };

            _logger.LogInformation("Agent {AgentId} registered with capabilities [{Caps}]",
                agentId, string.Join(", ", message.Capabilities));

            // Publish lifecycle signal
            _blackboard?.Tell(new UpdateGlobalBlackboard(
                GlobalBlackboardKeys.AgentJoined(agentId),
                DateTimeOffset.UtcNow.ToString("O")));

            EmitDashboardEvent();
        }
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
                DateTimeOffset.UtcNow,
                ActorName: Self.Path.Name));
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
            message.TaskId,
            message.Role,
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

        Timers.StartSingleTimer(ContractNetTimerPrefix + auctionId, new ContractNetFinalize(auctionId, message.TaskId, message.Role), timeout);
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
        if (state.Bids.Count == state.Candidates.Count)
        {
            Timers.Cancel(ContractNetTimerPrefix + bid.AuctionId);
            HandleContractNetFinalize(new ContractNetFinalize(bid.AuctionId, state.TaskId, state.Role));
        }
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
        if (_agents.Remove(message.ActorRef, out var ad))
        {
            var agentId = ad.AgentId ?? message.ActorRef.Path.Name;
            _health.Remove(agentId);
            _agentIdToRef.Remove(agentId);

            _logger.LogInformation("Agent {AgentId} departed (terminated)", agentId);

            _blackboard?.Tell(new UpdateGlobalBlackboard(
                GlobalBlackboardKeys.AgentLeft(agentId),
                DateTimeOffset.UtcNow.ToString("O")));

            EmitDashboardEvent();
        }
    }

    // ── New handlers (RFC-004) ──

    private void HandleHeartbeat(AgentHeartbeat message)
    {
        if (_health.TryGetValue(message.AgentId, out var state))
        {
            _health[message.AgentId] = state with
            {
                LastHeartbeat = DateTimeOffset.UtcNow,
                ConsecutiveFailures = 0,
                CircuitBreakerState = CircuitBreakerState.Closed
            };
        }
    }

    private void HandleDeregister(DeregisterAgent message)
    {
        RemoveAgent(message.AgentId);
        _logger.LogInformation("Agent {AgentId} deregistered (graceful)", message.AgentId);
    }

    private void RemoveAgent(string agentId)
    {
        var removed = false;
        if (_agentIdToRef.TryGetValue(agentId, out var actorRef))
        {
            removed |= _agents.Remove(actorRef);
            Context.Unwatch(actorRef);
        }
        removed |= _health.Remove(agentId);
        removed |= _agentIdToRef.Remove(agentId);
        if (!removed) return;

        _blackboard?.Tell(new UpdateGlobalBlackboard(
            GlobalBlackboardKeys.AgentLeft(agentId),
            DateTimeOffset.UtcNow.ToString("O")));

        EmitDashboardEvent();
    }

    private void HandleQuery(QueryAgents message)
    {
        var entries = _agents
            .Where(static pair => pair.Key != ActorRefs.Nobody)
            .Where(pair =>
                message.Capabilities is null ||
                message.Capabilities.Count == 0 ||
                message.Capabilities.Any(c => pair.Value.Capabilities.Contains(c)))
            .Select(pair =>
            {
                var ad = pair.Value;
                var agentId = ad.AgentId ?? pair.Key.Path.Name;
                var health = _health.GetValueOrDefault(agentId);
                return new AgentRegistryEntry(
                    agentId,
                    ad.ActorPath,
                    ad.Capabilities,
                    ad.CurrentLoad,
                    ad.EndpointUrl,
                    ad.Provider,
                    ad.SandboxLevel,
                    ad.Budget,
                    health?.RegisteredAt ?? DateTimeOffset.UtcNow,
                    health?.LastHeartbeat ?? DateTimeOffset.UtcNow,
                    health?.ConsecutiveFailures ?? 0,
                    health?.CircuitBreakerState ?? CircuitBreakerState.Closed);
            });

        // Apply preference ordering
        var ordered = message.Prefer?.ToLowerInvariant() switch
        {
            "cheapest" => entries
                .OrderBy(e => ProviderCostRank(e.Provider?.Type))
                .ThenBy(e => e.CurrentLoad),
            "least-loaded" => entries.OrderBy(e => e.CurrentLoad),
            _ => entries.OrderBy(e => e.CurrentLoad)
                .ThenBy(e => e.ActorPath, StringComparer.Ordinal)
        };

        Sender.Tell(new QueryAgentsResult(ordered.ToArray()));
    }

    private void CheckHeartbeats()
    {
        var now = DateTimeOffset.UtcNow;
        var toEvict = new List<string>();
        var toUpdate = new Dictionary<string, AgentHealthState>(StringComparer.Ordinal);

        foreach (var (agentId, state) in _health)
        {
            var elapsed = now - state.LastHeartbeat;
            var evictionThreshold = TimeSpan.FromSeconds(_heartbeatIntervalSeconds * MaxMissedHeartbeats);
            if (elapsed > evictionThreshold)
            {
                var updated = state with { ConsecutiveFailures = state.ConsecutiveFailures + 1 };
                toUpdate[agentId] = updated;

                if (updated.ConsecutiveFailures >= MaxMissedHeartbeats)
                {
                    toEvict.Add(agentId);
                }
            }
        }

        foreach (var (agentId, updated) in toUpdate)
        {
            _health[agentId] = updated;
        }

        foreach (var agentId in toEvict)
        {
            _logger.LogWarning("Agent {AgentId} evicted (missed heartbeats)", agentId);
            RemoveAgent(agentId);
        }
    }

    private void EmitDashboardEvent()
    {
        if (_uiEvents is null) return;

        var entries = _agents
            .Where(static pair => pair.Key != ActorRefs.Nobody)
            .Select(pair =>
            {
                var ad = pair.Value;
                var agentId = ad.AgentId ?? pair.Key.Path.Name;
                var health = _health.GetValueOrDefault(agentId);
                var status = health?.ConsecutiveFailures > 0 ? "unhealthy" : "active";
                var providerDisplay = ad.Provider?.Adapter;
                var budgetDisplay = ad.Budget?.Type == BudgetType.Unlimited
                    ? "unlimited"
                    : ad.Budget is not null
                        ? $"{ad.Budget.RemainingFraction:P0}"
                        : null;

                return new
                {
                    agentId,
                    role = ad.Capabilities.Count > 0
                        ? ad.Capabilities[0].ToString().ToLowerInvariant()
                        : "unknown",
                    status,
                    provider = providerDisplay,
                    budgetDisplay,
                    sandboxLevel = (int)ad.SandboxLevel,
                    endpointUrl = ad.EndpointUrl
                };
            })
            .ToArray();

        _uiEvents.Publish("agui.dashboard.agents", null, new
        {
            protocol = "a2ui/v0.8",
            operation = "updateDataModel",
            surfaceType = "dashboard",
            layer = "agent-list",
            data = new
            {
                agents = entries,
                activeCount = entries.Count(e => e.status == "active"),
                totalCount = entries.Length
            }
        });
    }

    private static int ProviderCostRank(string? providerType) => providerType switch
    {
        "subscription" => 0,
        "subscription-api" => 1,
        _ => 2
    };

    // ── Internal types ──

    private sealed record HeartbeatCheck;

    private sealed record AgentHealthState
    {
        public DateTimeOffset RegisteredAt { get; init; }
        public DateTimeOffset LastHeartbeat { get; init; }
        public int ConsecutiveFailures { get; init; }
        public CircuitBreakerState CircuitBreakerState { get; init; }
    }

    private sealed class ContractNetAuctionState(
        IActorRef requester,
        string taskId,
        SwarmRole role,
        HashSet<IActorRef> candidates)
    {
        public IActorRef Requester { get; } = requester;
        public string TaskId { get; } = taskId;
        public SwarmRole Role { get; } = role;

        public HashSet<IActorRef> Candidates { get; } = candidates;

        public Dictionary<IActorRef, ContractNetBid> Bids { get; } = new();
    }
}
