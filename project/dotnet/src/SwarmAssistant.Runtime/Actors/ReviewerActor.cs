using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime.Actors;

public sealed class ReviewerActor : ReceiveActor
{
    private readonly RuntimeOptions _options;
    private readonly ILogger _logger;

    public ReviewerActor(RuntimeOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger<ReviewerActor>();

        Receive<ExecuteRoleTask>(Handle);
    }

    private void Handle(ExecuteRoleTask command)
    {
        if (command.Role != SwarmRole.Reviewer)
        {
            Sender.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                $"ReviewerActor does not process role {command.Role}",
                DateTimeOffset.UtcNow));
            return;
        }

        if (_options.SimulateReviewerFailure)
        {
            Sender.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                "Simulated reviewer failure for escalation path testing.",
                DateTimeOffset.UtcNow));
            return;
        }

        var output =
            $"Review output for '{command.Title}': reviewed build '{command.BuildOutput ?? "(none)"}', no blocking defects found.";

        _logger.LogInformation("Reviewer completed taskId={TaskId}", command.TaskId);
        Sender.Tell(new RoleTaskSucceeded(command.TaskId, command.Role, output, DateTimeOffset.UtcNow));
    }
}
