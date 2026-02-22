using System.Diagnostics;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Contracts.Tasks;
using SwarmAssistant.Runtime.Telemetry;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Actors;

public sealed class CoordinatorActor : ReceiveActor
{
    private readonly IActorRef _workerActor;
    private readonly IActorRef _reviewerActor;
    private readonly IActorRef _supervisorActor;
    private readonly RuntimeTelemetry _telemetry;
    private readonly ILogger _logger;

    private readonly Dictionary<string, TaskContext> _tasks = new();

    public CoordinatorActor(
        IActorRef workerActor,
        IActorRef reviewerActor,
        IActorRef supervisorActor,
        ILoggerFactory loggerFactory,
        RuntimeTelemetry telemetry)
    {
        _workerActor = workerActor;
        _reviewerActor = reviewerActor;
        _supervisorActor = supervisorActor;
        _telemetry = telemetry;
        _logger = loggerFactory.CreateLogger<CoordinatorActor>();

        Receive<TaskAssigned>(HandleTaskAssigned);
        Receive<RoleTaskSucceeded>(HandleRoleTaskSucceeded);
        Receive<RoleTaskFailed>(HandleRoleTaskFailed);
    }

    private void HandleTaskAssigned(TaskAssigned message)
    {
        using var activity = _telemetry.StartActivity(
            "coordinator.task.assigned",
            taskId: message.TaskId,
            tags: new Dictionary<string, object?>
            {
                ["task.title"] = message.Title,
                ["actor.name"] = Self.Path.Name,
            });

        if (_tasks.ContainsKey(message.TaskId))
        {
            activity?.SetStatus(ActivityStatusCode.Error, "duplicate task assignment");
            _logger.LogWarning("Duplicate task assignment ignored taskId={TaskId}", message.TaskId);
            return;
        }

        var task = new SwarmTask(
            message.TaskId,
            message.Title,
            message.Description,
            TaskState.Queued,
            message.AssignedAt,
            message.AssignedAt);

        var context = new TaskContext(task);
        _tasks[message.TaskId] = context;

        TransitionTo(context, TaskState.Planning);
        _workerActor.Tell(new ExecuteRoleTask(
            context.Task.TaskId,
            SwarmRole.Planner,
            context.Task.Title,
            context.Task.Description,
            null,
            null));
    }

    private void HandleRoleTaskSucceeded(RoleTaskSucceeded message)
    {
        using var activity = _telemetry.StartActivity(
            "coordinator.role.succeeded",
            taskId: message.TaskId,
            role: message.Role.ToString().ToLowerInvariant(),
            tags: new Dictionary<string, object?>
            {
                ["actor.name"] = Self.Path.Name,
                ["output.length"] = message.Output.Length,
            });

        if (!_tasks.TryGetValue(message.TaskId, out var context))
        {
            activity?.SetStatus(ActivityStatusCode.Error, "unknown task on success");
            _logger.LogWarning("Received completion for unknown task taskId={TaskId}", message.TaskId);
            return;
        }

        switch (message.Role)
        {
            case SwarmRole.Planner:
                context.PlanningOutput = message.Output;
                _supervisorActor.Tell(new TaskResult(
                    context.Task.TaskId,
                    TaskState.Planning,
                    message.Output,
                    message.CompletedAt,
                    Self.Path.Name));

                TransitionTo(context, TaskState.Building);
                _workerActor.Tell(new ExecuteRoleTask(
                    context.Task.TaskId,
                    SwarmRole.Builder,
                    context.Task.Title,
                    context.Task.Description,
                    context.PlanningOutput,
                    null));
                break;

            case SwarmRole.Builder:
                context.BuildOutput = message.Output;
                _supervisorActor.Tell(new TaskResult(
                    context.Task.TaskId,
                    TaskState.Building,
                    message.Output,
                    message.CompletedAt,
                    Self.Path.Name));

                TransitionTo(context, TaskState.Reviewing);
                _reviewerActor.Tell(new ExecuteRoleTask(
                    context.Task.TaskId,
                    SwarmRole.Reviewer,
                    context.Task.Title,
                    context.Task.Description,
                    context.PlanningOutput,
                    context.BuildOutput));
                break;

            case SwarmRole.Reviewer:
                context.ReviewOutput = message.Output;
                _supervisorActor.Tell(new TaskResult(
                    context.Task.TaskId,
                    TaskState.Reviewing,
                    message.Output,
                    message.CompletedAt,
                    Self.Path.Name));

                TransitionTo(context, TaskState.Done);
                _supervisorActor.Tell(new TaskResult(
                    context.Task.TaskId,
                    TaskState.Done,
                    BuildSummary(context),
                    DateTimeOffset.UtcNow,
                    Self.Path.Name));
                break;

            default:
                HandleRoleTaskFailed(new RoleTaskFailed(
                    message.TaskId,
                    message.Role,
                    $"Unsupported role completion {message.Role}",
                    DateTimeOffset.UtcNow));
                break;
        }
    }

    private void HandleRoleTaskFailed(RoleTaskFailed message)
    {
        using var activity = _telemetry.StartActivity(
            "coordinator.role.failed",
            taskId: message.TaskId,
            role: message.Role.ToString().ToLowerInvariant(),
            tags: new Dictionary<string, object?>
            {
                ["error.message"] = message.Error,
                ["actor.name"] = Self.Path.Name,
            });
        activity?.SetStatus(ActivityStatusCode.Error, message.Error);

        if (!_tasks.TryGetValue(message.TaskId, out var context))
        {
            _logger.LogWarning("Received failure for unknown task taskId={TaskId}", message.TaskId);
            return;
        }

        context.Task = context.Task with
        {
            Status = TaskLifecycle.Next(context.Task.Status, success: false),
            UpdatedAt = DateTimeOffset.UtcNow,
            Error = message.Error
        };

        _supervisorActor.Tell(new TaskFailed(
            context.Task.TaskId,
            context.Task.Status,
            message.Error,
            message.FailedAt,
            Self.Path.Name));

        _supervisorActor.Tell(new EscalationRaised(
            context.Task.TaskId,
            message.Error,
            1,
            DateTimeOffset.UtcNow,
            Self.Path.Name));
    }

    private void TransitionTo(TaskContext context, TaskState target)
    {
        using var activity = _telemetry.StartActivity(
            "coordinator.task.transition",
            taskId: context.Task.TaskId,
            tags: new Dictionary<string, object?>
            {
                ["transition.to"] = target.ToString(),
                ["actor.name"] = Self.Path.Name,
            });

        var previous = context.Task.Status;
        context.Task = context.Task with
        {
            Status = target,
            UpdatedAt = DateTimeOffset.UtcNow,
            Error = null
        };

        activity?.SetTag("transition.from", previous.ToString());

        _logger.LogInformation(
            "Task transition taskId={TaskId} from={Previous} to={Current}",
            context.Task.TaskId,
            previous,
            target);

        _supervisorActor.Tell(new TaskStarted(
            context.Task.TaskId,
            target,
            context.Task.UpdatedAt,
            Self.Path.Name));
    }

    private static string BuildSummary(TaskContext context)
    {
        return string.Join(
            Environment.NewLine,
            $"Task '{context.Task.Title}' finished with status {context.Task.Status}.",
            $"Plan: {context.PlanningOutput}",
            $"Build: {context.BuildOutput}",
            $"Review: {context.ReviewOutput}");
    }

    private sealed class TaskContext
    {
        public TaskContext(SwarmTask task)
        {
            Task = task;
        }

        public SwarmTask Task { get; set; }

        public string? PlanningOutput { get; set; }

        public string? BuildOutput { get; set; }

        public string? ReviewOutput { get; set; }
    }
}
