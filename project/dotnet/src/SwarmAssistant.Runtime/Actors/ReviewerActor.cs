using System.Diagnostics;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime.Actors;

public sealed class ReviewerActor : ReceiveActor
{
    private readonly RuntimeOptions _options;
    private readonly AgentFrameworkRoleEngine _agentFrameworkRoleEngine;
    private readonly RuntimeTelemetry _telemetry;
    private readonly ILogger _logger;

    public ReviewerActor(
        RuntimeOptions options,
        ILoggerFactory loggerFactory,
        AgentFrameworkRoleEngine agentFrameworkRoleEngine,
        RuntimeTelemetry telemetry)
    {
        _options = options;
        _agentFrameworkRoleEngine = agentFrameworkRoleEngine;
        _telemetry = telemetry;
        _logger = loggerFactory.CreateLogger<ReviewerActor>();

        ReceiveAsync<ExecuteRoleTask>(HandleAsync);
    }

    private async Task HandleAsync(ExecuteRoleTask command)
    {
        var replyTo = Sender;

        using var activity = _telemetry.StartActivity(
            "reviewer.role.execute",
            taskId: command.TaskId,
            role: command.Role.ToString().ToLowerInvariant(),
            tags: new Dictionary<string, object?>
            {
                ["actor.name"] = Self.Path.Name,
                ["engine"] = "microsoft-agent-framework",
            });

        if (command.Role != SwarmRole.Reviewer)
        {
            var error = $"ReviewerActor does not process role {command.Role}";
            activity?.SetStatus(ActivityStatusCode.Error, error);
            replyTo.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                error,
                DateTimeOffset.UtcNow));
            return;
        }

        if (_options.SimulateReviewerFailure)
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

        try
        {
            var output = await _agentFrameworkRoleEngine.ExecuteAsync(command);
            activity?.SetTag("output.length", output.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);

            _logger.LogInformation("Reviewer completed taskId={TaskId} viaAgentFramework=true", command.TaskId);
            replyTo.Tell(new RoleTaskSucceeded(command.TaskId, command.Role, output, DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Reviewer failed taskId={TaskId} viaAgentFramework=true",
                command.TaskId);

            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            replyTo.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                $"Agent Framework execution failed: {exception.Message}",
                DateTimeOffset.UtcNow));
        }
    }
}
