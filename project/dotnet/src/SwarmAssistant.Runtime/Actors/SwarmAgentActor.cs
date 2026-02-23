using System.Diagnostics;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime.Actors;

public sealed class SwarmAgentActor : ReceiveActor
{
    private readonly RuntimeOptions _options;
    private readonly AgentFrameworkRoleEngine _agentFrameworkRoleEngine;
    private readonly RuntimeTelemetry _telemetry;
    private readonly IActorRef _capabilityRegistry;
    private readonly IReadOnlyList<SwarmRole> _capabilities;
    private readonly ILogger _logger;

    private int _currentLoad;

    private readonly TimeSpan _idleTtl;

    public SwarmAgentActor(
        RuntimeOptions options,
        ILoggerFactory loggerFactory,
        AgentFrameworkRoleEngine agentFrameworkRoleEngine,
        RuntimeTelemetry telemetry,
        IActorRef capabilityRegistry,
        SwarmRole[] capabilities,
        TimeSpan idleTtl = default)
    {
        _options = options;
        _agentFrameworkRoleEngine = agentFrameworkRoleEngine;
        _telemetry = telemetry;
        _capabilityRegistry = capabilityRegistry;
        _capabilities = Array.AsReadOnly((SwarmRole[])capabilities.Clone());
        _logger = loggerFactory.CreateLogger<SwarmAgentActor>();
        _idleTtl = idleTtl;

        ReceiveAsync<ExecuteRoleTask>(HandleAsync);
        Receive<HealthCheckRequest>(HandleHealthCheck);
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
        base.PreStart();
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

        _currentLoad++;
        AdvertiseCapability();
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
                    DateTimeOffset.UtcNow));
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
                    DateTimeOffset.UtcNow));
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
                    DateTimeOffset.UtcNow));
                return;
            }

            var result = await _agentFrameworkRoleEngine.ExecuteAsync(command);
            activity?.SetTag("output.length", result.Output.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);

            _logger.LogInformation(
                "Swarm agent role={Role} completed taskId={TaskId} executionMode={ExecutionMode}",
                command.Role,
                command.TaskId,
                _options.AgentFrameworkExecutionMode);

            replyTo.Tell(new RoleTaskSucceeded(command.TaskId, command.Role, result.Output, DateTimeOffset.UtcNow, AdapterId: result.AdapterId));
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
                DateTimeOffset.UtcNow));
        }
        finally
        {
            _currentLoad = Math.Max(0, _currentLoad - 1);
            AdvertiseCapability();
        }
    }

    private void OnIdleTimeout()
    {
        _logger.LogInformation(
            "SwarmAgentActor idle TTL expired, retiring agentId={AgentId}",
            Self.Path.Name);
        Context.Stop(Self);
    }

    private void AdvertiseCapability()
    {
        _capabilityRegistry.Tell(new AgentCapabilityAdvertisement(
            Self.Path.ToStringWithoutAddress(),
            _capabilities,
            _currentLoad), Self);
    }
}
