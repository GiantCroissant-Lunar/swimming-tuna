using System.Diagnostics;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime.Actors;

public sealed class SupervisorActor : ReceiveActor
{
    private readonly RuntimeTelemetry _telemetry;
    private readonly ILogger _logger;

    private int _started;
    private int _completed;
    private int _failed;
    private int _escalations;

    public SupervisorActor(ILoggerFactory loggerFactory, RuntimeTelemetry telemetry)
    {
        _telemetry = telemetry;
        _logger = loggerFactory.CreateLogger<SupervisorActor>();

        Receive<TaskStarted>(message =>
        {
            using var activity = _telemetry.StartActivity(
                "supervisor.task.started",
                taskId: message.TaskId,
                tags: new Dictionary<string, object?>
                {
                    ["task.status"] = message.Status.ToString(),
                    ["task.actor"] = message.ActorName,
                });

            _started += 1;
            _logger.LogInformation(
                "Task started taskId={TaskId} status={Status} actor={ActorName}",
                message.TaskId,
                message.Status,
                message.ActorName);
        });

        Receive<TaskResult>(message =>
        {
            using var activity = _telemetry.StartActivity(
                "supervisor.task.result",
                taskId: message.TaskId,
                tags: new Dictionary<string, object?>
                {
                    ["task.status"] = message.Status.ToString(),
                    ["task.actor"] = message.ActorName,
                    ["result.length"] = message.Output.Length,
                });

            if (message.Status == Contracts.Tasks.TaskStatus.Done)
            {
                _completed += 1;
            }

            _logger.LogInformation(
                "Task result taskId={TaskId} status={Status} actor={ActorName}",
                message.TaskId,
                message.Status,
                message.ActorName);
        });

        Receive<TaskFailed>(message =>
        {
            using var activity = _telemetry.StartActivity(
                "supervisor.task.failed",
                taskId: message.TaskId,
                tags: new Dictionary<string, object?>
                {
                    ["task.status"] = message.Status.ToString(),
                    ["task.actor"] = message.ActorName,
                    ["error.message"] = message.Error,
                });
            activity?.SetStatus(ActivityStatusCode.Error, message.Error);

            _failed += 1;
            _logger.LogWarning(
                "Task failed taskId={TaskId} status={Status} actor={ActorName} error={Error}",
                message.TaskId,
                message.Status,
                message.ActorName,
                message.Error);
        });

        Receive<EscalationRaised>(message =>
        {
            using var activity = _telemetry.StartActivity(
                "supervisor.escalation.raised",
                taskId: message.TaskId,
                tags: new Dictionary<string, object?>
                {
                    ["escalation.level"] = message.Level,
                    ["escalation.from"] = message.FromActor,
                    ["error.message"] = message.Reason,
                });
            activity?.SetStatus(ActivityStatusCode.Error, message.Reason);

            _escalations += 1;
            _logger.LogError(
                "Escalation raised taskId={TaskId} level={Level} from={FromActor} reason={Reason}",
                message.TaskId,
                message.Level,
                message.FromActor,
                message.Reason);
        });

        Receive<GetSupervisorSnapshot>(_ =>
            Sender.Tell(new SupervisorSnapshot(_started, _completed, _failed, _escalations)));
    }
}
