using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Contracts.Planning;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Planning;
using SwarmAssistant.Runtime.Tasks;
using SwarmAssistant.Runtime.Telemetry;
using SwarmAssistant.Runtime.Ui;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

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
        localOnlyDecider: ex => Directive.Stop);

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
    private readonly RuntimeOptions _runtimeOptions;
    private readonly ILogger _logger;

    private readonly Dictionary<string, IActorRef> _coordinators = new(StringComparer.Ordinal);
    private readonly Dictionary<IActorRef, string> _coordinatorTaskIds = new();
    private readonly Dictionary<string, IActorRef> _parentCoordinators = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _childParentTaskIds = new(StringComparer.Ordinal);
    private readonly Dictionary<IActorRef, string> _spawnedAgentIds = new();

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
        TaskRegistry taskRegistry,
        RuntimeOptions runtimeOptions)
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
        _runtimeOptions = runtimeOptions;
        _logger = loggerFactory.CreateLogger<DispatcherActor>();

        Receive<TaskAssigned>(HandleTaskAssigned);
        Receive<SpawnSubTask>(HandleSpawnSubTask);
        Receive<SpawnAgent>(HandleSpawnAgent);
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
                DefaultMaxRetries,
                0)),
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

    private void HandleSpawnSubTask(SpawnSubTask message)
    {
        if (_coordinators.ContainsKey(message.ChildTaskId))
        {
            _logger.LogWarning(
                "Duplicate sub-task spawn ignored childTaskId={ChildTaskId}", message.ChildTaskId);
            return;
        }

        _taskRegistry.RegisterSubTask(
            message.ChildTaskId, message.Title, message.Description, message.ParentTaskId);

        var goapPlanner = new GoapPlanner(SwarmActions.All);

        var coordinator = Context.ActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                message.ChildTaskId,
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
                DefaultMaxRetries,
                message.Depth)),
            $"task-{message.ChildTaskId}");

        _coordinators[message.ChildTaskId] = coordinator;
        _coordinatorTaskIds[coordinator] = message.ChildTaskId;
        _parentCoordinators[message.ChildTaskId] = Sender;
        _childParentTaskIds[message.ChildTaskId] = message.ParentTaskId;
        Context.Watch(coordinator);

        _logger.LogInformation(
            "Spawned sub-task childTaskId={ChildTaskId} parentTaskId={ParentTaskId} depth={Depth}",
            message.ChildTaskId, message.ParentTaskId, message.Depth);

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
    }

    private void HandleSpawnAgent(SpawnAgent message)
    {
        var agentId = $"dynamic-agent-{Guid.NewGuid():N}";
        var capabilities = message.Capabilities;
        var idleTtl = message.IdleTtl;

        var agent = Context.ActorOf(
            Props.Create(() => new SwarmAgentActor(
                _runtimeOptions,
                _loggerFactory,
                _roleEngine,
                _telemetry,
                _workerActor,
                capabilities,
                idleTtl)),
            agentId);

        _spawnedAgentIds[agent] = agentId;
        Context.Watch(agent);

        _logger.LogInformation(
            "Spawned dynamic agent agentId={AgentId} capabilities=[{Capabilities}] idleTtl={IdleTtl}",
            agentId,
            string.Join(",", capabilities.Select(c => c.ToString())),
            idleTtl);

        Sender.Tell(new AgentSpawned(agentId, agent));
    }

    private void OnCoordinatorTerminated(Terminated message)
    {
        // Handle dynamic agent retirement
        if (_spawnedAgentIds.TryGetValue(message.ActorRef, out var retiredAgentId))
        {
            _spawnedAgentIds.Remove(message.ActorRef);
            _logger.LogInformation(
                "Dynamic agent retired agentId={AgentId}", retiredAgentId);
            return;
        }

        if (!_coordinatorTaskIds.TryGetValue(message.ActorRef, out var taskId))
        {
            return;
        }

        _coordinators.Remove(taskId);
        _coordinatorTaskIds.Remove(message.ActorRef);

        if (_parentCoordinators.TryGetValue(taskId, out var parentCoordinatorRef))
        {
            _parentCoordinators.Remove(taskId);
            _childParentTaskIds.TryGetValue(taskId, out var parentTaskId);
            _childParentTaskIds.Remove(taskId);

            var snapshot = _taskRegistry.GetTask(taskId);
            var resolvedParentTaskId = parentTaskId ?? snapshot?.ParentTaskId ?? string.Empty;

            if (snapshot?.Status == TaskState.Done)
            {
                parentCoordinatorRef.Tell(new SubTaskCompleted(
                    resolvedParentTaskId,
                    taskId,
                    snapshot.Summary ?? string.Empty));
            }
            else
            {
                var error = snapshot?.Error ?? "Sub-task terminated without completing.";
                parentCoordinatorRef.Tell(new SubTaskFailed(
                    resolvedParentTaskId,
                    taskId,
                    error));
            }
        }

        _logger.LogDebug("Coordinator removed taskId={TaskId}", taskId);
    }
}
