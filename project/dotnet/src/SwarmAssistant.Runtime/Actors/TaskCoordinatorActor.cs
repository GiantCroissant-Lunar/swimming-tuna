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

    // Matches SUBTASK marker lines in planner output.
    // Expected format: SUBTASK: <title>|<description>
    // Example:         SUBTASK: Implement auth|Create login and logout endpoints
    private static readonly Regex SubTaskRegex = new(
        @"SUBTASK:\s*([^|\n\r]+)\|([^\n\r]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    internal const int MaxSubTaskDepth = 3;

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
    private readonly int _subTaskDepth;

    private WorldState _worldState;
    private string? _planningOutput;
    private string? _buildOutput;
    private string? _reviewOutput;
    private int _retryCount;
    private readonly int _maxRetries;
    private GoapPlanResult? _lastGoapPlan;
    private readonly HashSet<string> _childTaskIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingChildTaskIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _blackboardEntries = new(StringComparer.Ordinal);

    // Stigmergy: local cache of global blackboard signals, kept in sync via EventStream subscription
    private readonly Dictionary<string, string> _globalBlackboardCache = new(StringComparer.Ordinal);

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
        int maxRetries = 2,
        int subTaskDepth = 0)
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
        _subTaskDepth = subTaskDepth;

        _worldState = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.AdapterAvailable, true);

        Receive<StartCoordination>(_ => OnStart());
        Receive<RoleTaskSucceeded>(OnRoleSucceeded);
        Receive<RoleTaskFailed>(OnRoleFailed);
        Receive<SubTaskCompleted>(OnSubTaskCompleted);
        Receive<SubTaskFailed>(OnSubTaskFailed);
        Receive<RetryRole>(OnRetryRole);
        Receive<GlobalBlackboardChanged>(OnGlobalBlackboardChanged);
        Receive<QualityConcern>(OnQualityConcern);
    }

    protected override void PreStart()
    {
        // Subscribe to global blackboard changes for stigmergy signals
        Context.System.EventStream.Subscribe(Self, typeof(GlobalBlackboardChanged));
        // Subscribe to quality concerns from agent actors
        Context.System.EventStream.Subscribe(Self, typeof(QualityConcern));
        base.PreStart();
    }

    protected override void PostStop()
    {
        Context.System.EventStream.Unsubscribe(Self, typeof(GlobalBlackboardChanged));
        Context.System.EventStream.Unsubscribe(Self, typeof(QualityConcern));
        _blackboardActor.Tell(new RemoveBlackboard(_taskId));
        base.PostStop();
    }

    private void OnGlobalBlackboardChanged(GlobalBlackboardChanged message)
    {
        _globalBlackboardCache[message.Key] = message.Value;

        // React to stigmergy signals by updating GOAP world state
        if (message.Key.StartsWith(GlobalBlackboardKeys.AdapterCircuitPrefix, StringComparison.Ordinal))
        {
            // HighFailureRateDetected is true only while at least one adapter circuit is open
            _worldState = (WorldState)_worldState.With(WorldKey.HighFailureRateDetected, HasOpenCircuits());
        }
        else if (message.Key.StartsWith(GlobalBlackboardKeys.TaskSucceededPrefix, StringComparison.Ordinal))
        {
            _worldState = (WorldState)_worldState.With(WorldKey.SimilarTaskSucceeded, true);
        }
    }

    private bool HasOpenCircuits() =>
        _globalBlackboardCache.Any(kv =>
            kv.Key.StartsWith(GlobalBlackboardKeys.AdapterCircuitPrefix, StringComparison.Ordinal)
            && kv.Value.StartsWith(GlobalBlackboardKeys.CircuitStateOpen, StringComparison.Ordinal));

    private void OnStart()
    {
        // Reset per-run flags so stale swarm signals don't carry over
        _worldState = (WorldState)_worldState.With(WorldKey.SimilarTaskSucceeded, false);

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

        // Track confidence in activity for telemetry
        activity?.SetTag("quality.confidence", message.Confidence);

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
                StoreBlackboard("planner_confidence", message.Confidence.ToString("F2"));
                SpawnSubTasksIfPresent(message.Output);
                break;

            case SwarmRole.Builder:
                _buildOutput = message.Output;
                _worldState = (WorldState)_worldState.With(WorldKey.BuildExists, true);
                _taskRegistry.SetRoleOutput(_taskId, message.Role, message.Output);
                StoreBlackboard("builder_output", message.Output);
                StoreBlackboard("builder_confidence", message.Confidence.ToString("F2"));
                break;

            case SwarmRole.Reviewer:
                _reviewOutput = message.Output;
                var passed = !ContainsRejection(message.Output) && message.Confidence >= QualityEvaluator.QualityConcernThreshold;
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
                StoreBlackboard("reviewer_confidence", message.Confidence.ToString("F2"));
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

        // Don't advance to the next GOAP step while sub-tasks are still running
        if (_pendingChildTaskIds.Count > 0)
        {
            return;
        }

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

        // Sync the registry: clear the previous blocked/failed status so the task
        // is no longer reported as blocked while it retries.
        _taskRegistry.Transition(_taskId, MapRoleToState(message.Role));

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

        // Build orchestrator prompt with GOAP context + task history + stigmergy signals
        var goapContext = GoapContextSerializer.Serialize(_worldState, planResult);
        var orchestratorPrompt = RolePromptFactory.BuildOrchestratorPrompt(
            _taskId, _title, _description, goapContext, _blackboardEntries,
            _globalBlackboardCache.Count > 0 ? _globalBlackboardCache : null);

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

            case "WaitForSubTasks":
                // Sub-task completions will drive the next DecideAndExecute call via OnSubTaskCompleted
                _logger.LogInformation("Waiting for sub-tasks taskId={TaskId}", _taskId);
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

        // Only notify the supervisor for non-terminal transitions; terminal states
        // (Done, Blocked) are reported via TaskResult / TaskFailed instead.
        if (target != TaskState.Done && target != TaskState.Blocked)
        {
            _supervisorActor.Tell(new TaskStarted(_taskId, target, DateTimeOffset.UtcNow, Self.Path.Name));
        }

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

        // Stigmergy: write task success signal to global blackboard for cross-task learning
        _blackboardActor.Tell(new UpdateGlobalBlackboard(
            GlobalBlackboardKeys.TaskSucceeded(_taskId),
            _title));

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

        // Stigmergy: write task failure signal to global blackboard
        ReportFailureToGlobalBlackboard(error);

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

        // Stigmergy: write escalation signal to global blackboard
        ReportFailureToGlobalBlackboard(reason);

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

    private void ReportFailureToGlobalBlackboard(string failureReason)
    {
        _blackboardActor.Tell(new UpdateGlobalBlackboard(
            GlobalBlackboardKeys.TaskBlocked(_taskId),
            $"{_title}|{failureReason}"));
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

    private void SpawnSubTasksIfPresent(string planningOutput)
    {
        if (_subTaskDepth >= MaxSubTaskDepth)
        {
            _logger.LogWarning(
                "Max sub-task depth {Depth} reached; skipping SUBTASK spawning taskId={TaskId}",
                _subTaskDepth, _taskId);
            return;
        }

        var matches = SubTaskRegex.Matches(planningOutput);
        foreach (Match match in matches)
        {
            var title = match.Groups[1].Value.Trim();
            var description = match.Groups[2].Value.Trim();
            var childTaskId = $"subtask-{Guid.NewGuid():N}";

            _childTaskIds.Add(childTaskId);
            _pendingChildTaskIds.Add(childTaskId);

            Context.Parent.Tell(new SpawnSubTask(
                _taskId,
                childTaskId,
                title,
                description,
                _subTaskDepth + 1));

            _logger.LogInformation(
                "Spawning sub-task childTaskId={ChildTaskId} title={Title} taskId={TaskId}",
                childTaskId, title, _taskId);
        }

        if (_childTaskIds.Count > 0)
        {
            _worldState = (WorldState)_worldState.With(WorldKey.SubTasksSpawned, true);
        }
    }

    private void OnSubTaskCompleted(SubTaskCompleted message)
    {
        _pendingChildTaskIds.Remove(message.ChildTaskId);

        _logger.LogInformation(
            "Sub-task completed childTaskId={ChildTaskId} taskId={TaskId} remaining={Remaining}",
            message.ChildTaskId, _taskId, _pendingChildTaskIds.Count);

        if (_pendingChildTaskIds.Count == 0)
        {
            _worldState = (WorldState)_worldState.With(WorldKey.SubTasksCompleted, true);
            DecideAndExecute();
        }
    }

    private void OnSubTaskFailed(SubTaskFailed message)
    {
        var error = $"Sub-task '{message.ChildTaskId}' failed: {message.Error}";

        _logger.LogError(
            "Sub-task failed childTaskId={ChildTaskId} taskId={TaskId} error={Error}",
            message.ChildTaskId, _taskId, message.Error);

        _worldState = (WorldState)_worldState.With(WorldKey.TaskBlocked, true);

        _supervisorActor.Tell(new TaskFailed(
            _taskId, TaskState.Blocked, error, DateTimeOffset.UtcNow, Self.Path.Name));

        _supervisorActor.Tell(new EscalationRaised(
            _taskId, error, 1, DateTimeOffset.UtcNow, Self.Path.Name));

        _taskRegistry.MarkFailed(_taskId, error);

        // Stigmergy: write sub-task failure signal to global blackboard
        ReportFailureToGlobalBlackboard(error);

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

    private void OnQualityConcern(QualityConcern message)
    {
        // Only process quality concerns for this task
        if (!message.TaskId.Equals(_taskId, StringComparison.Ordinal))
        {
            return;
        }

        using var activity = _telemetry.StartActivity(
            "task-coordinator.quality.concern",
            taskId: _taskId,
            role: message.Role.ToString().ToLowerInvariant());

        activity?.SetTag("quality.confidence", message.Confidence);
        activity?.SetTag("quality.concern", message.Concern);

        _logger.LogWarning(
            "TaskCoordinator received quality concern taskId={TaskId} role={Role} confidence={Confidence:F2}",
            _taskId,
            message.Role,
            message.Confidence);

        // Store quality concern in blackboard for context
        StoreBlackboard($"quality_concern_{message.Role.ToString().ToLowerInvariant()}",
            $"{message.Concern} (confidence: {message.Confidence:F2})");

        // Adjust world state based on confidence level
        if (message.Confidence < QualityEvaluator.SelfRetryThreshold)
        {
            _worldState = (WorldState)_worldState.With(WorldKey.HighFailureRateDetected, true);
            _logger.LogWarning(
                "Low confidence detected taskId={TaskId} role={Role} - marking high failure risk",
                _taskId,
                message.Role);
        }
        else if (_worldState.Get(WorldKey.HighFailureRateDetected) && !HasOpenCircuits())
        {
            // Clear stale high-failure flag when confidence recovers and no circuits are open
            _worldState = (WorldState)_worldState.With(WorldKey.HighFailureRateDetected, false);
            _logger.LogInformation(
                "Quality recovered taskId={TaskId} role={Role} confidence={Confidence:F2} - clearing high failure risk",
                _taskId,
                message.Role,
                message.Confidence);
        }
    }
}
