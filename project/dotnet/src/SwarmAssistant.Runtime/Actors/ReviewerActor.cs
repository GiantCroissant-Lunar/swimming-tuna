using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime.Actors;

public sealed class ReviewerActor : ReceiveActor
{
    private readonly RuntimeOptions _options;
    private readonly AgentFrameworkRoleEngine _agentFrameworkRoleEngine;
    private readonly ILogger _logger;

    public ReviewerActor(RuntimeOptions options, ILoggerFactory loggerFactory, AgentFrameworkRoleEngine agentFrameworkRoleEngine)
    {
        _options = options;
        _agentFrameworkRoleEngine = agentFrameworkRoleEngine;
        _logger = loggerFactory.CreateLogger<ReviewerActor>();

        ReceiveAsync<ExecuteRoleTask>(HandleAsync);
    }

    private async Task HandleAsync(ExecuteRoleTask command)
    {
        var replyTo = Sender;

        if (command.Role != SwarmRole.Reviewer)
        {
            replyTo.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                $"ReviewerActor does not process role {command.Role}",
                DateTimeOffset.UtcNow));
            return;
        }

        if (_options.SimulateReviewerFailure)
        {
            replyTo.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                "Simulated reviewer failure for escalation path testing.",
                DateTimeOffset.UtcNow));
            return;
        }

        try
        {
            var output = await _agentFrameworkRoleEngine.ExecuteAsync(command);

            _logger.LogInformation("Reviewer completed taskId={TaskId} viaAgentFramework=true", command.TaskId);
            replyTo.Tell(new RoleTaskSucceeded(command.TaskId, command.Role, output, DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Reviewer failed taskId={TaskId} viaAgentFramework=true",
                command.TaskId);

            replyTo.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                $"Agent Framework execution failed: {exception.Message}",
                DateTimeOffset.UtcNow));
        }
    }
}
