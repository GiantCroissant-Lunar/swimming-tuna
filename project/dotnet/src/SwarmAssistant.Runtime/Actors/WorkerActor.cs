using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime.Actors;

public sealed class WorkerActor : ReceiveActor
{
    private readonly RuntimeOptions _options;
    private readonly ILogger _logger;

    public WorkerActor(RuntimeOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger<WorkerActor>();

        Receive<ExecuteRoleTask>(Handle);
    }

    private void Handle(ExecuteRoleTask command)
    {
        if (command.Role is not SwarmRole.Planner and not SwarmRole.Builder)
        {
            Sender.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                $"WorkerActor does not process role {command.Role}",
                DateTimeOffset.UtcNow));
            return;
        }

        if (_options.SimulateBuilderFailure && command.Role == SwarmRole.Builder)
        {
            Sender.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                "Simulated builder failure for phase testing.",
                DateTimeOffset.UtcNow));
            return;
        }

        var output = command.Role switch
        {
            SwarmRole.Planner =>
                $"Plan generated for '{command.Title}': define scope, implement minimum slice, validate, and report risks.",
            SwarmRole.Builder =>
                $"Build output for '{command.Title}': implemented from plan '{command.PlanningOutput ?? "(none)"}'.",
            _ => "Unsupported role"
        };

        _logger.LogInformation(
            "Worker role={Role} completed taskId={TaskId}",
            command.Role,
            command.TaskId);

        Sender.Tell(new RoleTaskSucceeded(command.TaskId, command.Role, output, DateTimeOffset.UtcNow));
    }
}
