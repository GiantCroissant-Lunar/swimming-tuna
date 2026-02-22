using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Actors;

public sealed class SupervisorActor : ReceiveActor
{
    private readonly ILogger _logger;

    private int _started;
    private int _completed;
    private int _failed;
    private int _escalations;

    public SupervisorActor(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<SupervisorActor>();

        Receive<TaskStarted>(message =>
        {
            _started += 1;
            _logger.LogInformation(
                "Task started taskId={TaskId} status={Status} actor={ActorName}",
                message.TaskId,
                message.Status,
                message.ActorName);
        });

        Receive<TaskResult>(message =>
        {
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
