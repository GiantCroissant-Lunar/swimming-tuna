using Akka.Actor;
using Akka.Pattern;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Execution;
using SwarmAssistant.Contracts.Planning;
using SwarmAssistant.Runtime.Planning;
using SwarmAssistant.Runtime.Tasks;
using SwarmAssistant.Runtime.Telemetry;
using SwarmAssistant.Runtime.Ui;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Actors;

/// <summary>
/// Routes incoming tasks to per-task <see cref="TaskCoordinatorActor"/> instances.
/// Maintains task registry state before coordination begins.
/// </summary>
public sealed class DispatcherActor : ReceiveActor
{
    private readonly IActorRef _workerActor;
    private readonly IActorRef _reviewerActor;
    private readonly IActorRef _supervisorActor;
    private readonly IActorRef _blackboardActor;
    private readonly IActorRef _consensusActor;
    private readonly AgentFrameworkRoleEngine _roleEngine;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RuntimeTelemetry _telemetry;
    private readonly UiEventStream _uiEvents;
    private readonly TaskRegistry _taskRegistry;
    private readonly RuntimeOptions _options;
    private readonly OutcomeTracker? _outcomeTracker;
    private readonly IActorRef? _strategyAdvisorActor;
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
        IActorRef consensusActor,
        AgentFrameworkRoleEngine roleEngine,
        ILoggerFactory loggerFactory,
        RuntimeTelemetry telemetry,
        UiEventStream uiEvents,
        TaskRegistry taskRegistry,
        IOptions<RuntimeOptions> options,
        OutcomeTracker? outcomeTracker = null,
        IActorRef? strategyAdvisorActor = null)
    {
        _workerActor = workerActor;
        _reviewerActor = reviewerActor;
        _supervisorActor = supervisorActor;
        _blackboardActor = blackboardActor;
        _consensusActor = consensusActor;
        _roleEngine = roleEngine;
        _loggerFactory = loggerFactory;
        _telemetry = telemetry;
        _uiEvents = uiEvents;
        _taskRegistry = taskRegistry;
        _options = options.Value;
        _outcomeTracker = outcomeTracker;
        _strategyAdvisorActor = strategyAdvisorActor;
        _logger = loggerFactory.CreateLogger<DispatcherActor>();

        Receive<TaskAssigned>(HandleTaskAssigned);
        Receive<SpawnSubTask>(HandleSpawnSubTask);
        Receive<TaskInterventionCommand>(HandleTaskInterventionCommand);
        Receive<SpawnAgent>(HandleSpawnAgent);
        Receive<Terminated>(OnCoordinatorTerminated);
    }

    protected override SupervisorStrategy SupervisorStrategy()
    {
        return new OneForOneStrategy(ex => Directive.Stop);
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
                _consensusActor,
                _roleEngine,
                goapPlanner,
                _loggerFactory,
                _telemetry,
                _uiEvents,
                _taskRegistry,
                _options,
                _outcomeTracker,
                _strategyAdvisorActor,
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
            payload: new TaskSubmittedPayload(message.TaskId, message.Title, message.Description));

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
                _consensusActor,
                _roleEngine,
                goapPlanner,
                _loggerFactory,
                _telemetry,
                _uiEvents,
                _taskRegistry,
                _options,
                _outcomeTracker,
                _strategyAdvisorActor,
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

        _uiEvents.Publish(
            type: "agui.graph.link_created",
            taskId: message.ParentTaskId,
            payload: new GraphLinkCreatedPayload(
                message.ParentTaskId,
                message.ChildTaskId,
                message.Depth,
                message.Title));

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
    }

    private void HandleTaskInterventionCommand(TaskInterventionCommand message)
    {
        if (!_coordinators.TryGetValue(message.TaskId, out var coordinator))
        {
            Sender.Tell(new TaskInterventionResult(
                message.TaskId,
                message.ActionId,
                Accepted: false,
                ReasonCode: "task_not_found",
                Message: "Task coordinator not found."));
            return;
        }

        coordinator
            .Ask<TaskInterventionResult>(message, TimeSpan.FromSeconds(5))
            .PipeTo(
                Sender,
                Self,
                success: result => result,
                failure: ex => new TaskInterventionResult(
                    message.TaskId,
                    message.ActionId,
                    Accepted: false,
                    ReasonCode: "dispatch_failed",
                    Message: ex.Message));
    }

    private void HandleSpawnAgent(SpawnAgent message)
    {
        var agentId = $"dynamic-agent-{Guid.NewGuid():N}";
        var capabilities = message.Capabilities;
        var idleTtl = message.IdleTtl;

        var agent = Context.ActorOf(
            Props.Create(() => new SwarmAgentActor(
                _options,
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
            Context.System.EventStream.Publish(new AgentRetired(retiredAgentId, "idle-timeout"));
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

                _uiEvents.Publish(
                    type: "agui.graph.child_completed",
                    taskId: resolvedParentTaskId,
                    payload: new GraphChildCompletedPayload(resolvedParentTaskId, taskId));
            }
            else
            {
                var error = snapshot?.Error ?? "Sub-task terminated without completing.";
                parentCoordinatorRef.Tell(new SubTaskFailed(
                    resolvedParentTaskId,
                    taskId,
                    error));

                _uiEvents.Publish(
                    type: "agui.graph.child_failed",
                    taskId: resolvedParentTaskId,
                    payload: new GraphChildFailedPayload(resolvedParentTaskId, taskId, error));
            }
        }

        _logger.LogDebug("Coordinator removed taskId={TaskId}", taskId);
    }
}
