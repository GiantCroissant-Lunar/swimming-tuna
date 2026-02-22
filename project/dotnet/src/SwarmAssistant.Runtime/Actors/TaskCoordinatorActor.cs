using System.Diagnostics;
using System.Text.RegularExpressions;
using Akka.Actor;
using Akka.Pattern;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Contracts.Planning;
using SwarmAssistant.Runtime.Execution;
using SwarmAssistant.Runtime.Planning;
using SwarmAssistant.Runtime.Tasks;
using SwarmAssistant.Runtime.Telemetry;
using SwarmAssistant.Runtime.Ui;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Actors;

/// <summary>
/// Per-task coordinator that uses GOAP planning as a skill for the CLI orchestrator agent.
/// At each decision point: update world state → run GOAP → build prompt → call CLI → parse → execute.
/// Falls back to GOAP recommendation directly if CLI orchestrator fails.
/// </summary>
public sealed class TaskCoordinatorActor : ReceiveActor
{
    private static readonly Regex ActionRegex = new(
        @"ACTION:\s*(\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RejectionRegex = new(
        @"\b(reject(ed)?|fail(ed|ure)?|blocked?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IActorRef _workerActor;
    private readonly IActorRef _reviewerActor;
    private readonly IActorRef _supervisorActor;
    private readonly IActorRef _blackboardActor;
    private readonly AgentFrameworkRoleEngine _roleEngine;
    private readonly IGoapPlanner _goapPlanner;
    private readonly RuntimeTelemetry _telemetry;
    private readonly UiEventStream _uiEvents;
    private readonly TaskRegistry _taskRegistry;
    private readonly ILogger _logger;

    private readonly string _taskId;
    private readonly string _title;
    private readonly string _description;

    private WorldState _worldState;
    private string? _planningOutput;
    private string? _buildOutput;
    private string? _reviewOutput;
    private int _retryCount;
    private readonly int _maxRetries;
    private GoapPlanResult? _lastGoapPlan;
    private readonly Dictionary<string, string> _blackboardEntries = new(StringComparer.Ordinal);

    public TaskCoordinatorActor(
        string taskId,
        string title,
        string description,
        IActorRef workerActor,
        IActorRef reviewerActor,
        IActorRef supervisorActor,
        IActorRef blackboardActor,
        AgentFrameworkRoleEngine roleEngine,
        IGoapPlanner goapPlanner,
        ILoggerFactory loggerFactory,
        RuntimeTelemetry telemetry,
        UiEventStream uiEvents,
        TaskRegistry taskRegistry,
        int maxRetries = 2)
    {
        _workerActor = workerActor;
        _reviewerActor = reviewerActor;
        _supervisorActor = supervisorActor;
        _blackboardActor = blackboardActor;
        _roleEngine = roleEngine;
        _goapPlanner = goapPlanner;
        _telemetry = telemetry;
        _uiEvents = uiEvents;
        _taskRegistry = taskRegistry;
        _logger = loggerFactory.CreateLogger<TaskCoordinatorActor>();

        _taskId = taskId;
        _title = title;
        _description = description;
        _maxRetries = maxRetries;

        _worldState = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.AdapterAvailable, true);

        Receive<StartCoordination>(_ => OnStart());
        Receive<RoleTaskSucceeded>(OnRoleSucceeded);
        Receive<RoleTaskFailed>(OnRoleFailed);
        Receive<RetryRole>(OnRetryRole);
    }

    protected override void PostStop()
    {
        _blackboardActor.Tell(new RemoveBlackboard(_taskId));
        base.PostStop();
    }

    private void OnStart()
    {
        _uiEvents.Publish(
            type: "agui.ui.surface",
            taskId: _taskId,
            payload: new
            {
                source = Self.Path.Name,
                a2ui = A2UiPayloadFactory.CreateSurface(
                    _taskId, _title, _description, TaskState.Queued)
            });

        DecideAndExecute();
    }

    private void OnRoleSucceeded(RoleTaskSucceeded message)
    {
        using var activity = _telemetry.StartActivity(
            "task-coordinator.role.succeeded",
            taskId: _taskId,
            role: message.Role.ToString().ToLowerInvariant());

        switch (message.Role)
        {
            case SwarmRole.Orchestrator:
                HandleOrchestratorResponse(message.Output);
                return;

            case SwarmRole.Planner:
                _planningOutput = message.Output;
                _worldState = (WorldState)_worldState.With(WorldKey.PlanExists, true);
                _taskRegistry.SetRoleOutput(_taskId, message.Role, message.Output);
                StoreBlackboard("planner_output", message.Output);
                break;

            case SwarmRole.Builder:
                _buildOutput = message.Output;
                _worldState = (WorldState)_worldState.With(WorldKey.BuildExists, true);
                _taskRegistry.SetRoleOutput(_taskId, message.Role, message.Output);
                StoreBlackboard("builder_output", message.Output);
                break;

            case SwarmRole.Reviewer:
                _reviewOutput = message.Output;
                var passed = !ContainsRejection(message.Output);
                if (passed)
                {
                    _worldState = (WorldState)_worldState
                        .With(WorldKey.ReviewPassed, true)
                        .With(WorldKey.ReviewRejected, false);
                }
                else
                {
                    _worldState = (WorldState)_worldState
                        .With(WorldKey.ReviewPassed, false)
                        .With(WorldKey.ReviewRejected, true);
                    _retryCount++;
                    if (_retryCount >= _maxRetries)
                    {
                        _worldState = (WorldState)_worldState
                            .With(WorldKey.RetryLimitReached, true);
                    }
                }

                _taskRegistry.SetRoleOutput(_taskId, message.Role, message.Output);
                StoreBlackboard("reviewer_output", message.Output);
                StoreBlackboard("review_passed", passed.ToString());
                break;
        }

        _uiEvents.Publish(
            type: "agui.ui.patch",
            taskId: _taskId,
            payload: new
            {
                source = Self.Path.Name,
                a2ui = A2UiPayloadFactory.AppendMessage(
                    _taskId,
                    message.Role.ToString().ToLowerInvariant(),
                    message.Output)
            });

        _supervisorActor.Tell(new TaskResult(
            _taskId,
            MapRoleToState(message.Role),
            message.Output,
            message.CompletedAt,
            Self.Path.Name));

        DecideAndExecute();
    }

    private void OnRoleFailed(RoleTaskFailed message)
    {
        using var activity = _telemetry.StartActivity(
            "task-coordinator.role.failed",
            taskId: _taskId,
            role: message.Role.ToString().ToLowerInvariant());
        activity?.SetStatus(ActivityStatusCode.Error, message.Error);

        StoreBlackboard($"failure_{message.Role.ToString().ToLowerInvariant()}", message.Error);

        _logger.LogWarning(
            "Role failed taskId={TaskId} role={Role} error={Error}",
            _taskId, message.Role, message.Error);

        // Orchestrator failures are non-fatal: fall back to GOAP deterministic execution
        if (message.Role == SwarmRole.Orchestrator)
        {
            _logger.LogWarning(
                "Orchestrator failed, falling back to GOAP taskId={TaskId}",
                _taskId);
            FallbackToGoap();
            return;
        }

        // Send detailed failure report to supervisor for active retry decisions
        _supervisorActor.Tell(new RoleFailureReport(
            _taskId,
            message.Role,
            message.Error,
            _retryCount,
            message.FailedAt));

        // Mark the task as blocked
        _worldState = (WorldState)_worldState.With(WorldKey.TaskBlocked, true);

        _supervisorActor.Tell(new TaskFailed(
            _taskId,
            TaskState.Blocked,
            message.Error,
            message.FailedAt,
            Self.Path.Name));

        _supervisorActor.Tell(new EscalationRaised(
            _taskId,
            message.Error,
            1,
            DateTimeOffset.UtcNow,
            Self.Path.Name));

        _taskRegistry.MarkFailed(_taskId, message.Error);

        _uiEvents.Publish(
            type: "agui.task.failed",
            taskId: _taskId,
            payload: new
            {
                source = Self.Path.Name,
                error = message.Error,
                a2ui = A2UiPayloadFactory.UpdateStatus(_taskId, TaskState.Blocked, message.Error)
            });
    }

    private void OnRetryRole(RetryRole message)
    {
        using var activity = _telemetry.StartActivity(
            "task-coordinator.retry",
            taskId: _taskId,
            role: message.Role.ToString().ToLowerInvariant(),
            tags: new Dictionary<string, object?>
            {
                ["retry.reason"] = message.Reason,
                ["retry.skip_adapter"] = message.SkipAdapter,
            });

        _logger.LogInformation(
            "Supervisor-initiated retry taskId={TaskId} role={Role} reason={Reason}",
            _taskId, message.Role, message.Reason);

        // Unblock the task so GOAP can plan again
        _worldState = (WorldState)_worldState.With(WorldKey.TaskBlocked, false);

        StoreBlackboard($"supervisor_retry_{message.Role.ToString().ToLowerInvariant()}", message.Reason);

        _uiEvents.Publish(
            type: "agui.task.retry",
            taskId: _taskId,
            payload: new
            {
                source = Self.Path.Name,
                role = message.Role.ToString().ToLowerInvariant(),
                reason = message.Reason,
            });

        // Re-dispatch the failed role
        var actionName = MapRoleToActionName(message.Role);
        DispatchAction(actionName);
    }

    private static string MapRoleToActionName(SwarmRole role) => role switch
    {
        SwarmRole.Planner => "Plan",
        SwarmRole.Builder => "Build",
        SwarmRole.Reviewer => "Review",
        _ => "Plan"
    };

    private void DecideAndExecute()
    {
        var planResult = _goapPlanner.Plan(_worldState, SwarmActions.CompleteTask);
        _lastGoapPlan = planResult;

        _logger.LogInformation(
            "GOAP plan taskId={TaskId} deadEnd={DeadEnd} recommended={Recommended}",
            _taskId,
            planResult.DeadEnd,
            planResult.RecommendedPlan is { Count: > 0 }
                ? string.Join(" → ", planResult.RecommendedPlan.Select(a => a.Name))
                : "(none)");

        if (planResult.RecommendedPlan is { Count: 0 })
        {
            // Goal already satisfied
            FinishTask();
            return;
        }

        if (planResult.DeadEnd)
        {
            HandleDeadEnd();
            return;
        }

        // Build orchestrator prompt with GOAP context + task history
        var goapContext = GoapContextSerializer.Serialize(_worldState, planResult);
        var orchestratorPrompt = RolePromptFactory.BuildOrchestratorPrompt(
            _taskId, _title, _description, goapContext, _blackboardEntries);

        if (planResult.RecommendedPlan is null or { Count: 0 })
        {
            _logger.LogWarning(
                "GOAP returned no recommended plan despite non-dead-end state taskId={TaskId}, falling back to GOAP",
                _taskId);
            FallbackToGoap();
            return;
        }

        _logger.LogInformation(
            "Requesting orchestrator decision taskId={TaskId} goapAction={GoapAction}",
            _taskId,
            planResult.RecommendedPlan[0].Name);

        _uiEvents.Publish(
            type: "agui.task.transition",
            taskId: _taskId,
            payload: new
            {
                source = Self.Path.Name,
                phase = "orchestrating",
                goapPlan = planResult.RecommendedPlan.Select(a => a.Name).ToArray(),
            });

        // Send orchestrator task to worker pool — response handled in OnRoleSucceeded
        _workerActor.Tell(new ExecuteRoleTask(
            _taskId,
            SwarmRole.Orchestrator,
            _title,
            _description,
            null,
            null,
            orchestratorPrompt));
    }

    private void DispatchAction(string actionName)
    {
        switch (actionName)
        {
            case "Plan":
                TransitionTo(TaskState.Planning);
                _workerActor.Tell(new ExecuteRoleTask(
                    _taskId, SwarmRole.Planner, _title, _description, null, null));
                break;

            case "Build":
                TransitionTo(TaskState.Building);
                _workerActor.Tell(new ExecuteRoleTask(
                    _taskId, SwarmRole.Builder, _title, _description, _planningOutput, null));
                break;

            case "Review":
                TransitionTo(TaskState.Reviewing);
                _reviewerActor.Tell(new ExecuteRoleTask(
                    _taskId, SwarmRole.Reviewer, _title, _description, _planningOutput, _buildOutput));
                break;

            case "Rework":
                StoreBlackboard($"rework_attempt_{_retryCount}", "Reworking after review rejection");
                TransitionTo(TaskState.Building);
                _workerActor.Tell(new ExecuteRoleTask(
                    _taskId, SwarmRole.Builder, _title, _description, _planningOutput, _buildOutput));
                break;

            case "Finalize":
                FinishTask();
                break;

            case "Escalate":
                HandleEscalation();
                break;

            default:
                _logger.LogWarning("Unknown GOAP action {Action} for taskId={TaskId}", actionName, _taskId);
                HandleDeadEnd();
                break;
        }
    }

    private void TransitionTo(TaskState target)
    {
        using var activity = _telemetry.StartActivity(
            "task-coordinator.transition",
            taskId: _taskId,
            tags: new Dictionary<string, object?> { ["transition.to"] = target.ToString() });

        _logger.LogInformation("Task transition taskId={TaskId} to={Target}", _taskId, target);

        _supervisorActor.Tell(new TaskStarted(_taskId, target, DateTimeOffset.UtcNow, Self.Path.Name));
        _taskRegistry.Transition(_taskId, target);

        _uiEvents.Publish(
            type: "agui.ui.patch",
            taskId: _taskId,
            payload: new
            {
                source = Self.Path.Name,
                a2ui = A2UiPayloadFactory.UpdateStatus(_taskId, target)
            });
    }

    private void FinishTask()
    {
        TransitionTo(TaskState.Done);

        var summary = BuildSummary();

        _supervisorActor.Tell(new TaskResult(
            _taskId, TaskState.Done, summary, DateTimeOffset.UtcNow, Self.Path.Name));

        _taskRegistry.MarkDone(_taskId, summary);

        _uiEvents.Publish(
            type: "agui.task.done",
            taskId: _taskId,
            payload: new { summary, source = Self.Path.Name });

        Context.Stop(Self);
    }

    private void HandleDeadEnd()
    {
        const string error = "GOAP planner detected dead end — no valid path to goal.";
        _logger.LogError("Dead end taskId={TaskId}", _taskId);

        _worldState = (WorldState)_worldState.With(WorldKey.TaskBlocked, true);

        _supervisorActor.Tell(new TaskFailed(
            _taskId, TaskState.Blocked, error, DateTimeOffset.UtcNow, Self.Path.Name));
        _supervisorActor.Tell(new EscalationRaised(
            _taskId, error, 2, DateTimeOffset.UtcNow, Self.Path.Name));
        _taskRegistry.MarkFailed(_taskId, error);

        _uiEvents.Publish(
            type: "agui.task.failed",
            taskId: _taskId,
            payload: new
            {
                source = Self.Path.Name,
                error,
                a2ui = A2UiPayloadFactory.UpdateStatus(_taskId, TaskState.Blocked, error)
            });

        Context.Stop(Self);
    }

    private void HandleEscalation()
    {
        const string reason = "Retry limit reached — escalating task.";
        _logger.LogWarning("Escalating taskId={TaskId}", _taskId);

        _worldState = (WorldState)_worldState.With(WorldKey.TaskBlocked, true);

        _supervisorActor.Tell(new TaskFailed(
            _taskId, TaskState.Blocked, reason, DateTimeOffset.UtcNow, Self.Path.Name));
        _supervisorActor.Tell(new EscalationRaised(
            _taskId, reason, 2, DateTimeOffset.UtcNow, Self.Path.Name));
        _taskRegistry.MarkFailed(_taskId, reason);

        _uiEvents.Publish(
            type: "agui.task.failed",
            taskId: _taskId,
            payload: new
            {
                source = Self.Path.Name,
                error = reason,
                a2ui = A2UiPayloadFactory.UpdateStatus(_taskId, TaskState.Blocked, reason)
            });

        Context.Stop(Self);
    }

    private void HandleOrchestratorResponse(string output)
    {
        using var activity = _telemetry.StartActivity(
            "task-coordinator.orchestrator.decision",
            taskId: _taskId);

        var match = ActionRegex.Match(output);
        if (match.Success)
        {
            var actionName = match.Groups[1].Value;
            _logger.LogInformation(
                "Orchestrator decided action={Action} taskId={TaskId}",
                actionName, _taskId);

            StoreBlackboard("orchestrator_decision", output);
            activity?.SetTag("orchestrator.action", actionName);

            _uiEvents.Publish(
                type: "agui.task.transition",
                taskId: _taskId,
                payload: new
                {
                    source = Self.Path.Name,
                    action = actionName,
                    decidedBy = "orchestrator",
                });

            DispatchAction(actionName);
        }
        else
        {
            _logger.LogWarning(
                "Could not parse orchestrator output, falling back to GOAP taskId={TaskId}",
                _taskId);

            activity?.SetTag("orchestrator.fallback", true);
            FallbackToGoap();
        }
    }

    private void FallbackToGoap()
    {
        if (_lastGoapPlan?.RecommendedPlan is { Count: > 0 } plan)
        {
            var action = plan[0];
            _logger.LogInformation(
                "GOAP fallback action={Action} taskId={TaskId}",
                action.Name, _taskId);

            _uiEvents.Publish(
                type: "agui.task.transition",
                taskId: _taskId,
                payload: new
                {
                    source = Self.Path.Name,
                    action = action.Name,
                    decidedBy = "goap-fallback",
                });

            DispatchAction(action.Name);
        }
        else
        {
            HandleDeadEnd();
        }
    }

    private void StoreBlackboard(string key, string value)
    {
        _blackboardEntries[key] = value;
        _blackboardActor.Tell(new UpdateBlackboard(_taskId, key, value));
    }

    private string BuildSummary()
    {
        return string.Join(
            Environment.NewLine,
            $"Task '{_title}' completed.",
            $"Plan: {_planningOutput ?? "(none)"}",
            $"Build: {_buildOutput ?? "(none)"}",
            $"Review: {_reviewOutput ?? "(none)"}");
    }

    private static bool ContainsRejection(string reviewOutput)
    {
        // Use word-boundary matching to avoid false positives on phrases like
        // "don't fail to add tests" or "code block".
        return RejectionRegex.IsMatch(reviewOutput);
    }

    private static TaskState MapRoleToState(SwarmRole role) => role switch
    {
        SwarmRole.Planner => TaskState.Planning,
        SwarmRole.Builder => TaskState.Building,
        SwarmRole.Reviewer => TaskState.Reviewing,
        _ => TaskState.Queued
    };

    // Trigger message to start the coordination loop after actor creation
    internal sealed record StartCoordination;
}
