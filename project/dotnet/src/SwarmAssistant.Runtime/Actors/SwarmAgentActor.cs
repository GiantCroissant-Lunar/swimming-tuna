using System.Diagnostics;
using Akka.Actor;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Agents;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Execution;
using SwarmAssistant.Runtime.Telemetry;

using System.Diagnostics.CodeAnalysis;

namespace SwarmAssistant.Runtime.Actors;

[SuppressMessage("Reliability", "CA1001",
    Justification = "Akka actors clean up disposable fields in PostStop(), not via IDisposable")]
public sealed class SwarmAgentActor : ReceiveActor
{
    private const int MaxConcurrentAgentTasks = 1;
    private const int EstimatedTimePerCostMs = 1_000;

    private readonly RuntimeOptions _options;
    private readonly AgentFrameworkRoleEngine _agentFrameworkRoleEngine;
    private readonly RuntimeTelemetry _telemetry;
    private readonly IActorRef _capabilityRegistry;
    private readonly IReadOnlyList<SwarmRole> _capabilities;
    private readonly ILogger _logger;
    private readonly string _agentId;
    private readonly int? _httpPort;
    private readonly Dictionary<string, int> _reservedContractCounts = new(StringComparer.Ordinal);
    private BudgetEnvelope _budget;
    private string _currentProviderAdapter;
    private string? _currentModelId;
    private string? _currentReasoning;
    private bool _budgetExhaustedAnnounced;

    private int _currentLoad;
    private readonly TimeSpan _idleTtl;
    private AgentEndpointHost? _endpointHost;
    private ICancelable? _heartbeatSchedule;

    public SwarmAgentActor(
        RuntimeOptions options,
        ILoggerFactory loggerFactory,
        AgentFrameworkRoleEngine agentFrameworkRoleEngine,
        RuntimeTelemetry telemetry,
        IActorRef capabilityRegistry,
        SwarmRole[] capabilities,
        TimeSpan idleTtl = default,
        string? agentId = null,
        int? httpPort = null)
    {
        _options = options;
        _agentFrameworkRoleEngine = agentFrameworkRoleEngine;
        _telemetry = telemetry;
        _capabilityRegistry = capabilityRegistry;
        _capabilities = Array.AsReadOnly((SwarmRole[])capabilities.Clone());
        _logger = loggerFactory.CreateLogger<SwarmAgentActor>();
        _agentId = agentId ?? $"agent-{Guid.NewGuid():N}"[..16];
        _idleTtl = idleTtl;
        _httpPort = httpPort;
        _currentProviderAdapter = ResolveConfiguredProviderAdapter();
        _budget = CreateInitialBudget();

        ReceiveAsync<ExecuteRoleTask>(HandleAsync);
        Receive<HealthCheckRequest>(HandleHealthCheck);
        Receive<NegotiationOffer>(HandleNegotiationOffer);
        Receive<HelpRequest>(HandleHelpRequest);
        Receive<NegotiationAccept>(_ => { });
        Receive<NegotiationReject>(_ => { });
        Receive<HelpResponse>(_ => { });
        Receive<ContractNetBidRequest>(HandleContractNetBidRequest);
        Receive<ContractNetAward>(HandleContractNetAward);
        Receive<PeerMessage>(HandlePeerMessage);
        if (idleTtl > TimeSpan.Zero)
        {
            Receive<ReceiveTimeout>(_ => OnIdleTimeout());
        }
    }

    protected override void PreStart()
    {
        if (_idleTtl > TimeSpan.Zero)
        {
            Context.SetReceiveTimeout(_idleTtl);
        }
        AdvertiseCapability();

        if (_options.AgentEndpointEnabled && _httpPort.HasValue)
        {
            try
            {
                var card = new AgentCard
                {
                    AgentId = _agentId,
                    Name = "swarm-assistant",
                    Version = "phase-12",
                    Protocol = "a2a",
                    Capabilities = _capabilities.ToArray(),
                    Provider = _currentProviderAdapter,
                    SandboxLevel = 0,
                    EndpointUrl = $"http://127.0.0.1:{_httpPort.Value}"
                };
                _endpointHost = new AgentEndpointHost(card, _httpPort.Value);
                _endpointHost.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

                // Re-advertise with endpoint URL now that the host is started
                AdvertiseCapability();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start agent endpoint on port {Port}", _httpPort.Value);
                _endpointHost = null;
            }
        }

        // Schedule heartbeat (store handle to cancel on stop)
        var heartbeatInterval = TimeSpan.FromSeconds(_options.AgentHeartbeatIntervalSeconds);
        _heartbeatSchedule = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(
            heartbeatInterval,
            heartbeatInterval,
            _capabilityRegistry,
            new AgentHeartbeat(_agentId),
            Self);

        base.PreStart();
    }

    protected override void PostStop()
    {
        _heartbeatSchedule?.Cancel();
        _heartbeatSchedule = null;

        if (_endpointHost is not null)
        {
            _endpointHost.StopAsync().GetAwaiter().GetResult();
            _endpointHost = null;
        }
        base.PostStop();
    }

    private void HandleHealthCheck(HealthCheckRequest request)
    {
        Sender.Tell(new HealthCheckResponse(
            request.RequestId,
            Self.Path.Name,
            _currentLoad,
            DateTimeOffset.UtcNow));
    }

    private async Task HandleAsync(ExecuteRoleTask command)
    {
        var replyTo = Sender;

        using var activity = _telemetry.StartActivity(
            "swarm-agent.role.execute",
            taskId: command.TaskId,
            role: command.Role.ToString().ToLowerInvariant(),
            tags: new Dictionary<string, object?>
            {
                ["actor.name"] = Self.Path.Name,
                ["engine"] = "microsoft-agent-framework",
            });
        var traceId = activity?.TraceId.ToHexString();
        var spanId = activity?.SpanId.ToHexString();

        // Contract awards reserve capacity before the execute message arrives.
        // If this execution consumes a reservation, avoid incrementing load again.
        var reserved = TryConsumeReservedContract(command.TaskId);
        if (!reserved)
        {
            _currentLoad++;
            AdvertiseCapability();
        }
        try
        {
            if (_budget.IsExhausted)
            {
                TransitionToExhausted();
                var error = $"Agent budget exhausted for {_agentId}";
                activity?.SetStatus(ActivityStatusCode.Error, error);
                replyTo.Tell(new RoleTaskFailed(
                    command.TaskId,
                    command.Role,
                    error,
                    DateTimeOffset.UtcNow,
                    ActorName: Self.Path.Name,
                    TraceId: traceId,
                    SpanId: spanId));
                return;
            }

            if (!_capabilities.Contains(command.Role))
            {
                var error = $"SwarmAgentActor does not support role {command.Role}";
                activity?.SetStatus(ActivityStatusCode.Error, error);
                replyTo.Tell(new RoleTaskFailed(
                    command.TaskId,
                    command.Role,
                    error,
                    DateTimeOffset.UtcNow,
                    ActorName: Self.Path.Name,
                    TraceId: traceId,
                    SpanId: spanId));
                return;
            }

            if (_options.SimulateBuilderFailure && command.Role == SwarmRole.Builder)
            {
                const string error = "Simulated builder failure for phase testing.";
                activity?.SetStatus(ActivityStatusCode.Error, error);
                replyTo.Tell(new RoleTaskFailed(
                    command.TaskId,
                    command.Role,
                    error,
                    DateTimeOffset.UtcNow,
                    ActorName: Self.Path.Name,
                    TraceId: traceId,
                    SpanId: spanId));
                return;
            }

            if (_options.SimulateReviewerFailure && command.Role == SwarmRole.Reviewer)
            {
                const string error = "Simulated reviewer failure for escalation path testing.";
                activity?.SetStatus(ActivityStatusCode.Error, error);
                replyTo.Tell(new RoleTaskFailed(
                    command.TaskId,
                    command.Role,
                    error,
                    DateTimeOffset.UtcNow,
                    ActorName: Self.Path.Name,
                    TraceId: traceId,
                    SpanId: spanId));
                return;
            }

            var result = await _agentFrameworkRoleEngine.ExecuteAsync(command);
            if (!string.IsNullOrWhiteSpace(result.AdapterId))
            {
                _currentProviderAdapter = result.AdapterId;
            }
            _currentModelId = result.Model?.Id;
            _currentReasoning = result.Reasoning;
            ConsumeBudgetForExecution(command, result.Output);
            activity?.SetTag("output.length", result.Output.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);

            _logger.LogInformation(
                "Swarm agent role={Role} completed taskId={TaskId} executionMode={ExecutionMode}",
                command.Role,
                command.TaskId,
                _options.AgentFrameworkExecutionMode);

            if (_budget.IsExhausted)
            {
                TransitionToExhausted();
            }

            replyTo.Tell(new RoleTaskSucceeded(
                command.TaskId,
                command.Role,
                result.Output,
                DateTimeOffset.UtcNow,
                AdapterId: result.AdapterId,
                ActorName: Self.Path.Name,
                TraceId: traceId,
                SpanId: spanId));
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Swarm agent role={Role} failed taskId={TaskId} executionMode={ExecutionMode}",
                command.Role,
                command.TaskId,
                _options.AgentFrameworkExecutionMode);

            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            replyTo.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                $"Agent Framework execution failed: {exception.Message}",
                DateTimeOffset.UtcNow,
                ActorName: Self.Path.Name,
                TraceId: traceId,
                SpanId: spanId));
        }
        finally
        {
            _currentLoad = Math.Max(0, _currentLoad - 1);
            if (!_budgetExhaustedAnnounced)
            {
                AdvertiseCapability();
            }
        }
    }

    private void HandleNegotiationOffer(NegotiationOffer offer)
    {
        if (_budget.IsExhausted)
        {
            Sender.Tell(new NegotiationReject(
                offer.TaskId,
                "Agent budget exhausted",
                Self.Path.ToStringWithoutAddress()));
            return;
        }

        if (_currentLoad >= MaxConcurrentAgentTasks)
        {
            Sender.Tell(new NegotiationReject(
                offer.TaskId,
                "Agent overloaded",
                Self.Path.ToStringWithoutAddress()));
            return;
        }

        if (_capabilities.Contains(offer.Role))
        {
            Sender.Tell(new NegotiationAccept(offer.TaskId, Self.Path.ToStringWithoutAddress()));
            return;
        }

        Sender.Tell(new NegotiationReject(
            offer.TaskId,
            $"Role {offer.Role} not supported",
            Self.Path.ToStringWithoutAddress()));
    }

    private void HandleHelpRequest(HelpRequest request)
    {
        var output = $"Help acknowledged for '{request.TaskId}' by {Self.Path.Name}: {request.Description}";
        Sender.Tell(new HelpResponse(request.TaskId, output, Self.Path.ToStringWithoutAddress()));
    }

    private void HandleContractNetBidRequest(ContractNetBidRequest request)
    {
        if (_budget.IsExhausted ||
            !_capabilities.Contains(request.Role) ||
            DateTimeOffset.UtcNow > request.DeadlineUtc)
        {
            return;
        }

        var estimatedCost = _currentLoad + 1;
        var estimatedTimeMs = estimatedCost * EstimatedTimePerCostMs;
        Sender.Tell(new ContractNetBid(
            request.AuctionId,
            request.TaskId,
            request.Role,
            Self.Path.ToStringWithoutAddress(),
            estimatedCost,
            estimatedTimeMs));
    }

    private void HandleContractNetAward(ContractNetAward award)
    {
        _reservedContractCounts.TryGetValue(award.TaskId, out var reservedCount);
        _reservedContractCounts[award.TaskId] = reservedCount + 1;
        _currentLoad++;
        AdvertiseCapability();

        _logger.LogInformation(
            "Won contract for task {TaskId} role {Role}; awaiting execution message.",
            award.TaskId,
            award.Role);
    }

    private void HandlePeerMessage(PeerMessage message)
    {
        _logger.LogInformation(
            "Peer message received messageId={MessageId} from={From} type={Type}",
            message.MessageId,
            message.FromAgentId,
            message.Type);

        using var activity = _telemetry.StartActivity(
            "swarm-agent.peer.message",
            tags: new Dictionary<string, object?>
            {
                ["peer.messageId"] = message.MessageId,
                ["peer.fromAgentId"] = message.FromAgentId,
                ["peer.toAgentId"] = message.ToAgentId,
                ["peer.type"] = message.Type.ToString(),
                ["actor.name"] = Self.Path.Name
            });

        Sender.Tell(new PeerMessageAck(message.MessageId, Accepted: true));
    }

    private bool TryConsumeReservedContract(string taskId)
    {
        if (!_reservedContractCounts.TryGetValue(taskId, out var reservedCount) || reservedCount <= 0)
        {
            return false;
        }

        if (reservedCount == 1)
        {
            _reservedContractCounts.Remove(taskId);
        }
        else
        {
            _reservedContractCounts[taskId] = reservedCount - 1;
        }

        return true;
    }

    private void OnIdleTimeout()
    {
        _logger.LogInformation(
            "SwarmAgentActor idle TTL expired, retiring agentId={AgentId}",
            _agentId);
        Context.Stop(Self);
    }

    private void AdvertiseCapability()
    {
        var provider = new ProviderInfo
        {
            Adapter = _currentProviderAdapter,
            Type = ResolveProviderType(_currentProviderAdapter),
            Model = _currentModelId,
            Reasoning = _currentReasoning
        };

        _capabilityRegistry.Tell(new AgentCapabilityAdvertisement(
            Self.Path.ToStringWithoutAddress(),
            _capabilities,
            _currentLoad,
            AgentId: _agentId,
            EndpointUrl: _endpointHost?.BaseUrl)
        {
            Provider = provider,
            SandboxLevel = _options.SandboxLevel,
            Budget = _budget
        }, Self);
    }

    private BudgetEnvelope CreateInitialBudget()
    {
        if (!_options.BudgetEnabled || _options.BudgetType == BudgetType.Unlimited)
        {
            return new BudgetEnvelope { Type = BudgetType.Unlimited };
        }

        var warningThreshold = _options.BudgetWarningThreshold;
        var hardLimit = Math.Max(warningThreshold, _options.BudgetHardLimit);

        return new BudgetEnvelope
        {
            Type = _options.BudgetType,
            TotalTokens = _options.BudgetTotalTokens,
            UsedTokens = 0,
            WarningThreshold = warningThreshold,
            HardLimit = hardLimit
        };
    }

    private void ConsumeBudgetForExecution(ExecuteRoleTask command, string output)
    {
        if (_budget.Type == BudgetType.Unlimited || _budget.TotalTokens <= 0)
        {
            return;
        }

        var prompt = command.Prompt ?? RolePromptFactory.BuildPrompt(command);
        var chars = prompt.Length + output.Length;
        var estimatedTokens = (long)Math.Ceiling(chars / (double)Math.Max(1, _options.BudgetCharsPerToken));
        if (estimatedTokens <= 0)
        {
            return;
        }

        var newUsed = _budget.UsedTokens >= long.MaxValue - estimatedTokens
            ? long.MaxValue
            : _budget.UsedTokens + estimatedTokens;

        _budget = _budget with { UsedTokens = newUsed };
        _logger.LogInformation(
            "Budget usage updated agentId={AgentId} estimatedTokens={EstimatedTokens} used={Used}/{Total} remaining={Remaining:P1}",
            _agentId,
            estimatedTokens,
            _budget.UsedTokens,
            _budget.TotalTokens,
            _budget.RemainingFraction);
    }

    private void TransitionToExhausted()
    {
        if (_budgetExhaustedAnnounced)
        {
            return;
        }

        _budgetExhaustedAnnounced = true;
        _logger.LogWarning(
            "Agent budget exhausted; deregistering agentId={AgentId} provider={Provider} used={Used}/{Total}",
            _agentId,
            _currentProviderAdapter,
            _budget.UsedTokens,
            _budget.TotalTokens);

        _heartbeatSchedule?.Cancel();
        _heartbeatSchedule = null;
        _capabilityRegistry.Tell(new DeregisterAgent(_agentId), Self);
    }

    private string ResolveConfiguredProviderAdapter()
    {
        if (_options.AgentFrameworkExecutionMode.Equals("api-direct", StringComparison.OrdinalIgnoreCase))
        {
            var configuredApiProvider = _options.ApiProviderOrder?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            if (string.IsNullOrWhiteSpace(configuredApiProvider))
            {
                return "api-openai";
            }

            var normalizedProvider = configuredApiProvider.Trim();
            if (normalizedProvider.StartsWith("api-", StringComparison.OrdinalIgnoreCase))
            {
                normalizedProvider = normalizedProvider["api-".Length..];
            }

            return $"api-{normalizedProvider}";
        }

        return _options.CliAdapterOrder?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "local-echo";
    }

    private static string ResolveProviderType(string adapterId)
    {
        return adapterId.StartsWith("api-", StringComparison.OrdinalIgnoreCase)
            ? "api"
            : "subscription";
    }
}
