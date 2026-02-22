using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Contracts.Planning;
using SwarmAssistant.Runtime.Planning;
using SwarmAssistant.Runtime.Tasks;
using SwarmAssistant.Runtime.Telemetry;
using SwarmAssistant.Runtime.Ui;

namespace SwarmAssistant.Runtime.Actors;

/// <summary>
/// Routes incoming tasks to per-task <see cref="TaskCoordinatorActor"/> instances.
/// Each task gets its own coordinator actor that manages the full GOAP lifecycle.
/// Tier 0 supervision: automatically restarts crashed coordinator children.
/// </summary>
public sealed class DispatcherActor : ReceiveActor
{
    private static readonly SupervisorStrategy Strategy = new OneForOneStrategy(
        maxNrOfRetries: 3,
        withinTimeRange: TimeSpan.FromMinutes(1),
        localOnlyDecider: ex => Directive.Restart);

    protected override SupervisorStrategy SupervisorStrategy() => Strategy;

    private readonly IActorRef _workerActor;
    private readonly IActorRef _reviewerActor;
    private readonly IActorRef _supervisorActor;
    private readonly IActorRef _blackboardActor;
    private readonly AgentFrameworkRoleEngine _roleEngine;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RuntimeTelemetry _telemetry;
    private readonly UiEventStream _uiEvents;
    private readonly TaskRegistry _taskRegistry;
    private readonly ILogger _logger;

    private readonly Dictionary<string, IActorRef> _coordinators = new(StringComparer.Ordinal);
    private readonly Dictionary<IActorRef, string> _coordinatorTaskIds = new();

    private const int DefaultMaxRetries = 2;

    public DispatcherActor(
        IActorRef workerActor,
        IActorRef reviewerActor,
        IActorRef supervisorActor,
        IActorRef blackboardActor,
        AgentFrameworkRoleEngine roleEngine,
        ILoggerFactory loggerFactory,
        RuntimeTelemetry telemetry,
        UiEventStream uiEvents,
        TaskRegistry taskRegistry)
    {
        _workerActor = workerActor;
        _reviewerActor = reviewerActor;
        _supervisorActor = supervisorActor;
        _blackboardActor = blackboardActor;
        _roleEngine = roleEngine;
        _loggerFactory = loggerFactory;
        _telemetry = telemetry;
        _uiEvents = uiEvents;
        _taskRegistry = taskRegistry;
        _logger = loggerFactory.CreateLogger<DispatcherActor>();

        Receive<TaskAssigned>(HandleTaskAssigned);
        Receive<Terminated>(OnCoordinatorTerminated);
    }

    private void HandleTaskAssigned(TaskAssigned message)
    {
        using var activity = _telemetry.StartActivity(
            "dispatcher.task.assigned",
            taskId: message.TaskId,
            tags: new Dictionary<string, object?>
            {
                ["task.title"] = message.Title,
                ["actor.name"] = Self.Path.Name,
            });

        if (_coordinators.ContainsKey(message.TaskId))
        {
            _logger.LogWarning(
                "Duplicate task assignment ignored taskId={TaskId}", message.TaskId);
            return;
        }

        _taskRegistry.Register(message);

        var goapPlanner = new GoapPlanner(SwarmActions.All);

        var coordinator = Context.ActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                message.TaskId,
                message.Title,
                message.Description,
                _workerActor,
                _reviewerActor,
                _supervisorActor,
                _blackboardActor,
                _roleEngine,
                goapPlanner,
                _loggerFactory,
                _telemetry,
                _uiEvents,
                _taskRegistry,
                DefaultMaxRetries)),
            $"task-{message.TaskId}");

        _coordinators[message.TaskId] = coordinator;
        _coordinatorTaskIds[coordinator] = message.TaskId;
        Context.Watch(coordinator);

        _logger.LogInformation(
            "Dispatched task taskId={TaskId} title={Title}",
            message.TaskId, message.Title);

        _uiEvents.Publish(
            type: "agui.task.submitted",
            taskId: message.TaskId,
            payload: new
            {
                message.TaskId,
                message.Title,
                message.Description
            });

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
    }

    private void OnCoordinatorTerminated(Terminated message)
    {
        if (_coordinatorTaskIds.TryGetValue(message.ActorRef, out var taskId))
        {
            _coordinators.Remove(taskId);
            _coordinatorTaskIds.Remove(message.ActorRef);
            _logger.LogDebug("Coordinator removed taskId={TaskId}", taskId);
        }
    }
}
