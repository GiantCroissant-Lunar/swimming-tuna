using System.Diagnostics;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Agents;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime.Actors;

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
    private string _currentProviderAdapter;

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

        ReceiveAsync<ExecuteRoleTask>(HandleAsync);
        Receive<HealthCheckRequest>(HandleHealthCheck);
        Receive<NegotiationOffer>(HandleNegotiationOffer);
        Receive<HelpRequest>(HandleHelpRequest);
        Receive<NegotiationAccept>(_ => { });
        Receive<NegotiationReject>(_ => { });
        Receive<HelpResponse>(_ => { });
        Receive<ContractNetBidRequest>(HandleContractNetBidRequest);
        Receive<ContractNetAward>(HandleContractNetAward);
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
            activity?.SetTag("output.length", result.Output.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);

            _logger.LogInformation(
                "Swarm agent role={Role} completed taskId={TaskId} executionMode={ExecutionMode}",
                command.Role,
                command.TaskId,
                _options.AgentFrameworkExecutionMode);

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
            AdvertiseCapability();
        }
    }

    private void HandleNegotiationOffer(NegotiationOffer offer)
    {
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
        if (!_capabilities.Contains(request.Role) || DateTimeOffset.UtcNow > request.DeadlineUtc)
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
            Type = _options.AgentFrameworkExecutionMode == "subscription-cli-fallback"
                ? "subscription"
                : "api"
        };

        var budget = new BudgetEnvelope { Type = BudgetType.Unlimited };

        _capabilityRegistry.Tell(new AgentCapabilityAdvertisement(
            Self.Path.ToStringWithoutAddress(),
            _capabilities,
            _currentLoad,
            AgentId: _agentId,
            EndpointUrl: _endpointHost?.BaseUrl)
        {
            Provider = provider,
            SandboxLevel = _options.SandboxLevel,
            Budget = budget
        }, Self);
    }

    private string ResolveConfiguredProviderAdapter()
    {
        return _options.CliAdapterOrder?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "local-echo";
    }
}
