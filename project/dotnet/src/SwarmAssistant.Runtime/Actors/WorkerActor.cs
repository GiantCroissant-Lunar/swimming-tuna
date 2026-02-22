using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime.Actors;

public sealed class WorkerActor : ReceiveActor
{
    private readonly RuntimeOptions _options;
    private readonly AgentFrameworkRoleEngine _agentFrameworkRoleEngine;
    private readonly ILogger _logger;

    public WorkerActor(RuntimeOptions options, ILoggerFactory loggerFactory, AgentFrameworkRoleEngine agentFrameworkRoleEngine)
    {
        _options = options;
        _agentFrameworkRoleEngine = agentFrameworkRoleEngine;
        _logger = loggerFactory.CreateLogger<WorkerActor>();

        ReceiveAsync<ExecuteRoleTask>(HandleAsync);
    }

    private async Task HandleAsync(ExecuteRoleTask command)
    {
        var replyTo = Sender;

        if (command.Role is not SwarmRole.Planner and not SwarmRole.Builder)
        {
            replyTo.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                $"WorkerActor does not process role {command.Role}",
                DateTimeOffset.UtcNow));
            return;
        }

        if (_options.SimulateBuilderFailure && command.Role == SwarmRole.Builder)
        {
            replyTo.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                "Simulated builder failure for phase testing.",
                DateTimeOffset.UtcNow));
            return;
        }

        try
        {
            var output = await _agentFrameworkRoleEngine.ExecuteAsync(command);

            _logger.LogInformation(
                "Worker role={Role} completed taskId={TaskId} viaAgentFramework=true",
                command.Role,
                command.TaskId);

            replyTo.Tell(new RoleTaskSucceeded(command.TaskId, command.Role, output, DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Worker role={Role} failed taskId={TaskId} viaAgentFramework=true",
                command.Role,
                command.TaskId);

            replyTo.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                $"Agent Framework execution failed: {exception.Message}",
                DateTimeOffset.UtcNow));
        }
    }
}
