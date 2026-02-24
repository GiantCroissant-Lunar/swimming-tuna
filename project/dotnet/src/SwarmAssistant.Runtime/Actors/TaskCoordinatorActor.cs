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
using SwarmAssistant.Runtime.Configuration;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Actors;

/// <summary>
/// Per-task coordinator that uses GOAP planning as a skill for the CLI orchestrator agent.
/// At each decision point: update world state → run GOAP → build prompt → call CLI → parse → execute.
/// Falls back to GOAP recommendation directly if CLI orchestrator fails.
/// Integrates learning and adaptation via StrategyAdvisorActor and OutcomeTracker.
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

    internal const int MaxAllowedSubTaskDepth = 10;
    private const int EscalationLevelFatal = 2;

    private readonly IActorRef _workerActor;
    private readonly IActorRef _reviewerActor;
    private readonly IActorRef _supervisorActor;
    private readonly IActorRef _blackboardActor;
    private readonly IActorRef _consensusActor;
    private readonly IActorRef? _strategyAdvisorActor;
    private readonly IActorRef? _codeIndexActor;
    private readonly AgentFrameworkRoleEngine _roleEngine;
    private readonly IGoapPlanner _goapPlanner;
    private readonly RuntimeTelemetry _telemetry;
    private readonly UiEventStream _uiEvents;
    private readonly TaskRegistry _taskRegistry;
    private readonly RuntimeOptions _options;
    private readonly OutcomeTracker? _outcomeTracker;
    private readonly RuntimeEventRecorder? _eventRecorder;
    private readonly ILogger _logger;

    private readonly string _taskId;
    private readonly string _title;
    private readonly string _description;
    private readonly string? _runId;
    private readonly int _subTaskDepth;

    private WorldState _worldState;
    private string? _planningOutput;
    private string? _buildOutput;
    private string? _reviewOutput;

    private int _retryCount;
    private readonly int _maxRetries;
    private bool _isPaused;
    private GoapPlanResult? _lastGoapPlan;
    private string? _deferredActionName;
    private int? _maxSubTaskDepthOverride;
    private readonly HashSet<string> _childTaskIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingChildTaskIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _blackboardEntries = new(StringComparer.Ordinal);

    // Stigmergy: local cache of global blackboard signals, kept in sync via EventStream subscription
    private readonly Dictionary<string, string> _globalBlackboardCache = new(StringComparer.Ordinal);

    // Learning: cached strategy advice for this task
    private StrategyAdvice? _strategyAdvice;

    public TaskCoordinatorActor(
        string taskId,
        string title,
        string description,
        IActorRef workerActor,
        IActorRef reviewerActor,
        IActorRef supervisorActor,
        IActorRef blackboardActor,
        IActorRef consensusActor,
        AgentFrameworkRoleEngine roleEngine,
        IGoapPlanner goapPlanner,
        ILoggerFactory loggerFactory,
        RuntimeTelemetry telemetry,
        UiEventStream uiEvents,
        TaskRegistry taskRegistry,
        RuntimeOptions options,
        OutcomeTracker? outcomeTracker = null,
        IActorRef? strategyAdvisorActor = null,
        IActorRef? codeIndexActor = null,
        int maxRetries = 2,
        int subTaskDepth = 0,
        RuntimeEventRecorder? eventRecorder = null)
    {
        _workerActor = workerActor;
        _reviewerActor = reviewerActor;
        _supervisorActor = supervisorActor;
        _blackboardActor = blackboardActor;
        _consensusActor = consensusActor;
        _strategyAdvisorActor = strategyAdvisorActor;
        _codeIndexActor = codeIndexActor;
        _roleEngine = roleEngine;
        _goapPlanner = goapPlanner;
        _telemetry = telemetry;
        _uiEvents = uiEvents;
        _taskRegistry = taskRegistry;
        _options = options;
        _outcomeTracker = outcomeTracker;
        _eventRecorder = eventRecorder;
        _logger = loggerFactory.CreateLogger<TaskCoordinatorActor>();

        _taskId = taskId;
        _title = title;
        _description = description;
        _runId = taskRegistry.GetTask(taskId)?.RunId;
        _maxRetries = maxRetries;
        _subTaskDepth = subTaskDepth;

        _worldState = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.AdapterAvailable, true);

        Receive<StartCoordination>(_ => OnStart());
        Receive<StrategyAdvice>(OnStrategyAdviceReceived);
        Receive<RoleTaskSucceeded>(OnRoleSucceeded);
        Receive<RoleTaskFailed>(OnRoleFailed);
        Receive<SubTaskCompleted>(OnSubTaskCompleted);
        Receive<SubTaskFailed>(OnSubTaskFailed);
        Receive<RetryRole>(OnRetryRole);
        Receive<GlobalBlackboardChanged>(OnGlobalBlackboardChanged);
        Receive<ConsensusResult>(OnConsensusResult);
        Receive<QualityConcern>(OnQualityConcern);
        Receive<TaskInterventionCommand>(OnTaskInterventionCommand);
        Receive<RoleLifecycleEvent>(OnRoleLifecycleEvent);
        Receive<DispatchWithCodeContext>(OnDispatchWithCodeContext);
    }

    protected override void PreStart()
    {
        // Subscribe to global blackboard changes for stigmergy signals
        Context.System.EventStream.Subscribe(Self, typeof(GlobalBlackboardChanged));
        // Subscribe to quality concerns from agent actors
        Context.System.EventStream.Subscribe(Self, typeof(QualityConcern));
        // Subscribe to role lifecycle events from worker/reviewer actors
        Context.System.EventStream.Subscribe(Self, typeof(RoleLifecycleEvent));
        base.PreStart();
    }

    protected override void PostStop()
    {
        Context.System.EventStream.Unsubscribe(Self, typeof(GlobalBlackboardChanged));
        Context.System.EventStream.Unsubscribe(Self, typeof(QualityConcern));
        Context.System.EventStream.Unsubscribe(Self, typeof(RoleLifecycleEvent));
        _blackboardActor.Tell(new RemoveBlackboard(_taskId));
        base.PostStop();
    }

    private void OnConsensusResult(ConsensusResult message)
    {
        _logger.LogInformation("Received consensus result for task {TaskId}: Approved={Approved}", message.TaskId, message.Approved);

        if (message.Approved)
        {
            _worldState = (WorldState)_worldState
                .With(WorldKey.ReviewPassed, true)
                .With(WorldKey.ReviewRejected, false)
                .With(WorldKey.ConsensusReached, true)
                .With(WorldKey.ConsensusDisputed, false);
        }
        else
        {
            _worldState = (WorldState)_worldState
                .With(WorldKey.ReviewPassed, false)
                .With(WorldKey.ReviewRejected, true)
                .With(WorldKey.ConsensusReached, false)
                .With(WorldKey.ConsensusDisputed, true);

            _retryCount++;
            if (_retryCount >= _maxRetries)
            {
                _worldState = (WorldState)_worldState
                    .With(WorldKey.RetryLimitReached, true);
            }
        }

        var combinedFeedback = string.Join("\n\n---\n\n", message.Votes.Select(v => $"Vote from {v.VoterId} (Approved: {v.Approved}):\n{v.Feedback}"));
        _reviewOutput = combinedFeedback;

        _taskRegistry.SetRoleOutput(_taskId, SwarmRole.Reviewer, combinedFeedback);
        StoreBlackboard("reviewer_output", combinedFeedback);
        StoreBlackboard("review_passed", message.Approved.ToString());

        _uiEvents.Publish(
            type: "agui.task.transition",
            taskId: _taskId,
            payload: new
            {
                source = Self.Path.Name,
                action = message.Approved ? "ConsensusReached" : "ConsensusDisputed",
                decidedBy = "consensus",
            });

        _uiEvents.Publish(
            type: "agui.telemetry.consensus",
            taskId: _taskId,
            payload: new TelemetryConsensusPayload(message.Approved, message.Votes.Count, _retryCount));

        // Report a single consolidated TaskResult to the supervisor for the reviewing phase
        _supervisorActor.Tell(new TaskResult(
            _taskId,
            TaskState.Reviewing,
            combinedFeedback,
            DateTimeOffset.UtcNow,
            Self.Path.Name));

        DecideAndExecute();
    }

    private void OnGlobalBlackboardChanged(GlobalBlackboardChanged message)
    {
        _globalBlackboardCache[message.Key] = message.Value;

        // React to stigmergy signals by updating GOAP world state
        if (message.Key.StartsWith(GlobalBlackboardKeys.AdapterCircuitPrefix, StringComparison.Ordinal))
        {
            // HighFailureRateDetected is true only while at least one adapter circuit is open
            _worldState = (WorldState)_worldState.With(WorldKey.HighFailureRateDetected, HasOpenCircuits());

            _uiEvents.Publish(
                type: "agui.telemetry.circuit",
                taskId: _taskId,
                payload: new TelemetryCircuitPayload(message.Key, message.Value, HasOpenCircuits()));
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

        // Persist coordination.started lifecycle event (best-effort, fire-and-forget)
        _ = _eventRecorder?.RecordCoordinationStartedAsync(_taskId, _runId);

        _uiEvents.Publish(
            type: "agui.ui.surface",
            taskId: _taskId,
            payload: new
            {
                source = Self.Path.Name,
                a2ui = A2UiPayloadFactory.CreateSurface(
                    _taskId, _title, _description, TaskState.Queued)
            });

        // Request strategy advice from learning system if available
        if (_strategyAdvisorActor is not null)
        {
            _logger.LogInformation(
                "Requesting strategy advice taskId={TaskId}",
                _taskId);

            _strategyAdvisorActor.Tell(new StrategyAdviceRequest
            {
                TaskId = _taskId,
                Title = _title,
                Description = _description
            });

            // DecideAndExecute will be called when StrategyAdvice is received
        }
        else
        {
            DecideAndExecute();
        }
    }

    /// <summary>
    /// Handles strategy advice received from the StrategyAdvisorActor.
    /// Integrates learning insights into the task planning process.
    /// </summary>
    private void OnStrategyAdviceReceived(StrategyAdvice advice)
    {
        _strategyAdvice = advice;

        _logger.LogInformation(
            "Strategy advice received taskId={TaskId} similarTasks={Count} successRate={Rate:P0}",
            _taskId, advice.SimilarTaskCount, advice.SimilarTaskSuccessRate);

        // Store advice in blackboard for context
        if (advice.Insights is { Count: > 0 })
        {
            StoreBlackboard("strategy_insights", string.Join("; ", advice.Insights));
        }

        if (advice.SimilarTaskCount > 0)
        {
            StoreBlackboard("historical_success_rate", advice.SimilarTaskSuccessRate.ToString("P0"));
        }

        // Proceed with planning, potentially using cost adjustments
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

        _uiEvents.Publish(
            type: "agui.telemetry.quality",
            taskId: _taskId,
            payload: new TelemetryQualityPayload(
                message.Role.ToString().ToLowerInvariant(),
                message.Confidence,
                _retryCount));

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

                // Single reviewer backward compatibility
                if (_options.ReviewConsensusCount <= 1)
                {
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
                    }

                    _uiEvents.Publish(
                        type: "agui.task.decision",
                        taskId: _taskId,
                        payload: new
                        {
                            source = Self.Path.Name,
                            action = passed ? "ReviewPassed" : "ReviewRejected",
                            decidedBy = "reviewer",
                        });

                    _taskRegistry.SetRoleOutput(_taskId, message.Role, message.Output);
                    StoreBlackboard("reviewer_output", message.Output);
                    StoreBlackboard("review_passed", passed.ToString());
                    StoreBlackboard("reviewer_confidence", message.Confidence.ToString("F2"));

                    DecideAndExecute();
                }
                else
                {
                    // Forward vote to consensus actor; blackboard/registry are updated in OnConsensusResult
                    _consensusActor.Tell(new ConsensusVote(
                        TaskId: _taskId,
                        VoterId: message.ActorName,
                        Approved: passed,
                        Confidence: message.Confidence,
                        Feedback: message.Output
                    ));
                    // Don't call DecideAndExecute() yet, wait for consensus
                }
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

        // Emit explicit role.succeeded event for all non-orchestrator roles
        if (message.Role != SwarmRole.Orchestrator)
        {
            _uiEvents.Publish(
                type: "agui.role.succeeded",
                taskId: _taskId,
                payload: new RoleSucceededPayload(
                    message.Role.ToString().ToLowerInvariant(),
                    _taskId,
                    message.Confidence,
                    message.AdapterId));

            // Persist role.completed lifecycle event (best-effort, fire-and-forget)
            _ = _eventRecorder?.RecordRoleCompletedAsync(
                _taskId, _runId,
                message.Role.ToString().ToLowerInvariant(),
                message.Confidence);
        }

        // For multi-reviewer consensus, don't send per-vote TaskResult; OnConsensusResult handles it
        if (message.Role != SwarmRole.Reviewer || _options.ReviewConsensusCount <= 1)
        {
            _supervisorActor.Tell(new TaskResult(
                _taskId,
                MapRoleToState(message.Role),
                message.Output,
                message.CompletedAt,
                Self.Path.Name));
        }

        // Don't advance to the next GOAP step while sub-tasks are still running
        if (_pendingChildTaskIds.Count > 0)
        {
            return;
        }

        // For single reviewer, DecideAndExecute is called within the case. For others, call it here.
        if (message.Role != SwarmRole.Reviewer || _options.ReviewConsensusCount == 1)
        {
            DecideAndExecute();
        }
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

        // Emit explicit role.failed event
        _uiEvents.Publish(
            type: "agui.role.failed",
            taskId: _taskId,
            payload: new RoleFailedPayload(
                message.Role.ToString().ToLowerInvariant(),
                _taskId,
                message.Error));

        // Persist role.failed lifecycle event (best-effort, fire-and-forget)
        _ = _eventRecorder?.RecordRoleFailedAsync(
            _taskId, _runId,
            message.Role.ToString().ToLowerInvariant(),
            message.Error);

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

        // Persist task.failed lifecycle event (best-effort, fire-and-forget)
        _ = _eventRecorder?.RecordTaskFailedAsync(_taskId, _runId, message.Error);

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

        _uiEvents.Publish(
            type: "agui.telemetry.retry",
            taskId: _taskId,
            payload: new TelemetryRetryPayload(
                message.Role.ToString().ToLowerInvariant(),
                _retryCount,
                message.Reason));

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

    private void OnTaskInterventionCommand(TaskInterventionCommand message)
    {
        if (!string.Equals(message.TaskId, _taskId, StringComparison.Ordinal))
        {
            Sender.Tell(new TaskInterventionResult(
                message.TaskId,
                message.ActionId,
                Accepted: false,
                ReasonCode: "task_mismatch",
                Message: "Intervention taskId does not match coordinator task."));
            return;
        }

        var actionId = message.ActionId.Trim().ToLowerInvariant();
        switch (actionId)
        {
            case "approve_review":
                if (CurrentStatus != TaskState.Reviewing)
                {
                    Sender.Tell(RejectedIntervention(message, "invalid_state", "Task must be in reviewing state."));
                    return;
                }

                if (!string.IsNullOrWhiteSpace(message.Comment))
                {
                    StoreBlackboard("human_review_approval_comment", message.Comment);
                }

                _worldState = (WorldState)_worldState
                    .With(WorldKey.ReviewPassed, true)
                    .With(WorldKey.ReviewRejected, false);
                StoreBlackboard("review_passed", true.ToString());

                Sender.Tell(new TaskInterventionResult(message.TaskId, actionId, true, "approved"));
                _uiEvents.Publish(
                    type: "agui.task.intervention",
                    taskId: _taskId,
                    payload: new TaskInterventionPayload(_taskId, "approve_review", "human"));
                FinishTask();
                return;

            case "reject_review":
                if (CurrentStatus != TaskState.Reviewing)
                {
                    Sender.Tell(RejectedIntervention(message, "invalid_state", "Task must be in reviewing state."));
                    return;
                }

                if (string.IsNullOrWhiteSpace(message.Reason))
                {
                    Sender.Tell(RejectedIntervention(message, "payload_invalid", "A rejection reason is required."));
                    return;
                }

                Sender.Tell(new TaskInterventionResult(message.TaskId, actionId, true, "rejected"));
                _uiEvents.Publish(
                    type: "agui.task.intervention",
                    taskId: _taskId,
                    payload: new TaskInterventionPayload(_taskId, "reject_review", "human"));
                BlockFromIntervention($"Review rejected by human: {message.Reason.Trim()}");
                return;

            case "request_rework":
                if (CurrentStatus != TaskState.Building && CurrentStatus != TaskState.Reviewing)
                {
                    Sender.Tell(RejectedIntervention(message, "invalid_state", "Task must be building or reviewing."));
                    return;
                }

                if (string.IsNullOrWhiteSpace(message.Feedback))
                {
                    Sender.Tell(RejectedIntervention(message, "payload_invalid", "Rework feedback is required."));
                    return;
                }

                StoreBlackboard("human_rework_feedback", message.Feedback.Trim());
                Sender.Tell(new TaskInterventionResult(message.TaskId, actionId, true, "rework_requested"));
                _uiEvents.Publish(
                    type: "agui.task.intervention",
                    taskId: _taskId,
                    payload: new TaskInterventionPayload(_taskId, "request_rework", "human"));
                DispatchAction("Rework");
                return;

            case "pause_task":
                if (_isPaused)
                {
                    Sender.Tell(RejectedIntervention(message, "invalid_state", "Task is already paused."));
                    return;
                }

                if (CurrentStatus is TaskState.Done or TaskState.Blocked)
                {
                    Sender.Tell(RejectedIntervention(message, "invalid_state", "Task is already in a terminal state."));
                    return;
                }

                _isPaused = true;
                _uiEvents.Publish(
                    type: "agui.task.transition",
                    taskId: _taskId,
                    payload: new
                    {
                        source = Self.Path.Name,
                        action = "Paused",
                        decidedBy = "human"
                    });
                _uiEvents.Publish(
                    type: "agui.task.intervention",
                    taskId: _taskId,
                    payload: new TaskInterventionPayload(_taskId, "pause_task", "human"));

                Sender.Tell(new TaskInterventionResult(message.TaskId, actionId, true, "paused"));
                return;

            case "resume_task":
                if (!_isPaused)
                {
                    Sender.Tell(RejectedIntervention(message, "invalid_state", "Task is not paused."));
                    return;
                }

                _isPaused = false;
                _uiEvents.Publish(
                    type: "agui.task.transition",
                    taskId: _taskId,
                    payload: new
                    {
                        source = Self.Path.Name,
                        action = "Resumed",
                        decidedBy = "human"
                    });
                _uiEvents.Publish(
                    type: "agui.task.intervention",
                    taskId: _taskId,
                    payload: new TaskInterventionPayload(_taskId, "resume_task", "human"));

                Sender.Tell(new TaskInterventionResult(message.TaskId, actionId, true, "resumed"));
                if (!string.IsNullOrWhiteSpace(_deferredActionName))
                {
                    var actionToDispatch = _deferredActionName;
                    _deferredActionName = null;
                    DispatchAction(actionToDispatch);
                }
                else
                {
                    DecideAndExecute();
                }

                return;

            case "set_subtask_depth":
                if (_planningOutput is not null)
                {
                    Sender.Tell(RejectedIntervention(message, "invalid_state", "Sub-task depth can only be set before planning output exists."));
                    return;
                }

                if (message.MaxSubTaskDepth is null || message.MaxSubTaskDepth < 0 || message.MaxSubTaskDepth > MaxAllowedSubTaskDepth)
                {
                    Sender.Tell(RejectedIntervention(
                        message,
                        "payload_invalid",
                        $"depth must be between 0 and {MaxAllowedSubTaskDepth}."));
                    return;
                }

                _maxSubTaskDepthOverride = message.MaxSubTaskDepth.Value;
                StoreBlackboard("subtask_depth_override", _maxSubTaskDepthOverride.Value.ToString());
                Sender.Tell(new TaskInterventionResult(message.TaskId, actionId, true, "subtask_depth_updated"));
                return;

            default:
                Sender.Tell(RejectedIntervention(message, "unsupported_action", "Unsupported intervention action."));
                return;
        }
    }

    private TaskInterventionResult RejectedIntervention(TaskInterventionCommand command, string reasonCode, string message) =>
        new(command.TaskId, command.ActionId, Accepted: false, ReasonCode: reasonCode, Message: message);

    private TaskState CurrentStatus => _taskRegistry.GetTask(_taskId)?.Status ?? TaskState.Queued;

    private void DecideAndExecute()
    {
        if (_isPaused)
        {
            _logger.LogInformation("Task execution paused taskId={TaskId}; skipping GOAP decision loop", _taskId);
            return;
        }

        var planResult = _goapPlanner.Plan(_worldState, SwarmActions.CompleteTask, _strategyAdvice?.RecommendedCostAdjustments);
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
            orchestratorPrompt,
            RunId: _runId));
    }

    private void DispatchAction(string actionName)
    {
        if (_isPaused)
        {
            _deferredActionName = actionName;
            _logger.LogInformation(
                "Task is paused; deferring action={Action} taskId={TaskId}",
                actionName,
                _taskId);
            return;
        }

        // For roles that benefit from code context, query the code index first
        if (_codeIndexActor != null && ShouldQueryCodeIndex(actionName))
        {
            QueryCodeIndexAndDispatch(actionName);
            return;
        }

        DoDispatchAction(actionName, null);
    }

    /// <summary>
    /// Determines if a given action should query the code index for context.
    /// </summary>
    private bool ShouldQueryCodeIndex(string actionName) => actionName switch
    {
        "Plan" => _options.CodeIndexForPlanner,
        "Build" => _options.CodeIndexForBuilder,
        "Review" => _options.CodeIndexForReviewer,
        "Rework" => _options.CodeIndexForBuilder,
        "SecondOpinion" => _options.CodeIndexForReviewer,
        _ => false
    };

    /// <summary>
    /// Queries the code index and then dispatches the action with the results.
    /// </summary>
    private void QueryCodeIndexAndDispatch(string actionName)
    {
        if (_codeIndexActor == null)
        {
            DoDispatchAction(actionName, null);
            return;
        }

        // Build a query from task title and description
        var query = $"{_title} {_description}".Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            DoDispatchAction(actionName, null);
            return;
        }

        _logger.LogInformation(
            "Querying code index for action={Action} taskId={TaskId}",
            actionName,
            _taskId);

        // Query code index asynchronously
        var self = Self;
        var languages = _options.CodeIndexLanguages is { Length: > 0 }
            ? _options.CodeIndexLanguages
            : null;
        _codeIndexActor.Ask<CodeIndexResult>(
            new CodeIndexQuery(
                Query: query,
                TopK: _options.CodeIndexMaxChunks,
                Languages: languages
            ),
            TimeSpan.FromSeconds(10)
        ).ContinueWith(task =>
        {
            var result = task.Status == TaskStatus.RanToCompletion && task.Result != null
                ? task.Result
                : null;
            return new DispatchWithCodeContext(actionName, result);
        }).PipeTo(self);
    }

    /// <summary>
    /// Handles the result of code index query and dispatches the action.
    /// </summary>
    private void OnDispatchWithCodeContext(DispatchWithCodeContext message)
    {
        DoDispatchAction(message.ActionName, message.CodeContext);
    }

    /// <summary>
    /// Performs the actual dispatch with optional code context.
    /// </summary>
    private void DoDispatchAction(string actionName, CodeIndexResult? codeContext)
    {
        switch (actionName)
        {
            case "Plan":
                TransitionTo(TaskState.Planning);
                _uiEvents.Publish(
                    type: "agui.role.dispatched",
                    taskId: _taskId,
                    payload: new RoleDispatchedPayload("planner", _taskId));
                var planPrompt = RolePromptFactory.BuildPrompt(
                    new ExecuteRoleTask(_taskId, SwarmRole.Planner, _title, _description, null, null, RunId: _runId),
                    _strategyAdvice,
                    codeContext);
                _workerActor.Tell(new ExecuteRoleTask(
                    _taskId, SwarmRole.Planner, _title, _description, null, null, Prompt: planPrompt, RunId: _runId));
                break;

            case "Build":
                TransitionTo(TaskState.Building);
                _uiEvents.Publish(
                    type: "agui.role.dispatched",
                    taskId: _taskId,
                    payload: new RoleDispatchedPayload("builder", _taskId));
                var buildPrompt = RolePromptFactory.BuildPrompt(
                    new ExecuteRoleTask(_taskId, SwarmRole.Builder, _title, _description, _planningOutput, null, RunId: _runId),
                    _strategyAdvice,
                    codeContext);
                _workerActor.Tell(new ExecuteRoleTask(
                    _taskId, SwarmRole.Builder, _title, _description, _planningOutput, null, Prompt: buildPrompt, RunId: _runId));
                break;

            case "Review":
                TransitionTo(TaskState.Reviewing);
                _uiEvents.Publish(
                    type: "agui.role.dispatched",
                    taskId: _taskId,
                    payload: new RoleDispatchedPayload("reviewer", _taskId));
                var reviewCount = _options.ReviewConsensusCount;
                var reviewPrompt = RolePromptFactory.BuildPrompt(
                    new ExecuteRoleTask(_taskId, SwarmRole.Reviewer, _title, _description, _planningOutput, _buildOutput, RunId: _runId),
                    _strategyAdvice,
                    codeContext);
                var reviewTask = new ExecuteRoleTask(
                    _taskId, SwarmRole.Reviewer, _title, _description, _planningOutput, _buildOutput, Prompt: reviewPrompt, RunId: _runId);
                if (reviewCount == 1)
                {
                    _reviewerActor.Tell(reviewTask);
                }
                else
                {
                    _consensusActor.Tell(new ConsensusRequest(_taskId, _buildOutput ?? string.Empty, reviewCount));
                    for (int i = 0; i < reviewCount; i++)
                    {
                        _reviewerActor.Tell(reviewTask);
                    }
                }
                break;

            case "SecondOpinion":
                TransitionTo(TaskState.Reviewing);
                _uiEvents.Publish(
                    type: "agui.role.dispatched",
                    taskId: _taskId,
                    payload: new RoleDispatchedPayload("reviewer", _taskId));
                _logger.LogInformation("Requesting SecondOpinion for task {TaskId}", _taskId);

                _consensusActor.Tell(new CancelConsensusSession(_taskId));

                var additionalReviewCount = 1;
                var currentRequiredVotes = _options.ReviewConsensusCount;
                _consensusActor.Tell(new ConsensusRequest(_taskId, _buildOutput ?? string.Empty, currentRequiredVotes + additionalReviewCount));

                var secondOpinionPrompt = RolePromptFactory.BuildPrompt(
                    new ExecuteRoleTask(_taskId, SwarmRole.Reviewer, _title, _description, _planningOutput, _buildOutput, RunId: _runId),
                    _strategyAdvice,
                    codeContext);
                var secondOpinionTask = new ExecuteRoleTask(
                    _taskId, SwarmRole.Reviewer, _title, _description, _planningOutput, _buildOutput, Prompt: secondOpinionPrompt, RunId: _runId);
                for (int i = 0; i < currentRequiredVotes + additionalReviewCount; i++)
                {
                    _reviewerActor.Tell(secondOpinionTask);
                }
                break;

            case "Rework":
                StoreBlackboard($"rework_attempt_{_retryCount}", "Reworking after review rejection");
                TransitionTo(TaskState.Building);
                _uiEvents.Publish(
                    type: "agui.role.dispatched",
                    taskId: _taskId,
                    payload: new RoleDispatchedPayload("builder", _taskId));
                var reworkPrompt = RolePromptFactory.BuildPrompt(
                    new ExecuteRoleTask(_taskId, SwarmRole.Builder, _title, _description, _planningOutput, _buildOutput, RunId: _runId),
                    _strategyAdvice,
                    codeContext);
                _workerActor.Tell(new ExecuteRoleTask(
                    _taskId, SwarmRole.Builder, _title, _description, _planningOutput, _buildOutput, Prompt: reworkPrompt, RunId: _runId));
                break;

            case "Finalize":
                FinishTask();
                break;

            case "Escalate":
                HandleEscalation();
                break;

            case "WaitForSubTasks":
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

        // Learning: record successful outcome
        RecordOutcome(TaskState.Done, summary: summary);

        // Persist task.done lifecycle event (best-effort, fire-and-forget)
        _ = _eventRecorder?.RecordTaskDoneAsync(_taskId, _runId);

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
            _taskId, error, EscalationLevelFatal, DateTimeOffset.UtcNow, Self.Path.Name));
        _taskRegistry.MarkFailed(_taskId, error);

        // Stigmergy: write task failure signal to global blackboard
        ReportFailureToGlobalBlackboard(error);

        // Persist task.failed lifecycle event (best-effort, fire-and-forget)
        _ = _eventRecorder?.RecordTaskFailedAsync(_taskId, _runId, error);

        _uiEvents.Publish(
            type: "agui.task.escalated",
            taskId: _taskId,
            payload: new TaskEscalatedPayload(_taskId, error, EscalationLevelFatal));

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
            _taskId, reason, EscalationLevelFatal, DateTimeOffset.UtcNow, Self.Path.Name));
        _taskRegistry.MarkFailed(_taskId, reason);

        // Stigmergy: write escalation signal to global blackboard
        ReportFailureToGlobalBlackboard(reason);

        // Persist task.failed lifecycle event (best-effort, fire-and-forget)
        _ = _eventRecorder?.RecordTaskFailedAsync(_taskId, _runId, reason);

        _uiEvents.Publish(
            type: "agui.task.escalated",
            taskId: _taskId,
            payload: new TaskEscalatedPayload(_taskId, reason, EscalationLevelFatal));

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

    private void BlockFromIntervention(string reason)
    {
        _logger.LogWarning("Human intervention blocked task taskId={TaskId} reason={Reason}", _taskId, reason);

        _worldState = (WorldState)_worldState.With(WorldKey.TaskBlocked, true);
        _taskRegistry.MarkFailed(_taskId, reason);

        _supervisorActor.Tell(new TaskFailed(
            _taskId, TaskState.Blocked, reason, DateTimeOffset.UtcNow, Self.Path.Name));
        _supervisorActor.Tell(new EscalationRaised(
            _taskId, reason, 1, DateTimeOffset.UtcNow, Self.Path.Name));

        // Persist task.failed lifecycle event (best-effort, fire-and-forget)
        _ = _eventRecorder?.RecordTaskFailedAsync(_taskId, _runId, reason);

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
        var effectiveMaxSubTaskDepth = _maxSubTaskDepthOverride ?? _options.DefaultMaxSubTaskDepth;
        if (_subTaskDepth >= effectiveMaxSubTaskDepth)
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

        // Persist task.failed lifecycle event (best-effort, fire-and-forget)
        _ = _eventRecorder?.RecordTaskFailedAsync(_taskId, _runId, error);

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

    /// <summary>
    /// Records the task outcome for learning and adaptation.
    /// Called when task reaches a terminal state (Done or Blocked).
    /// </summary>
    private void RecordOutcome(TaskState finalStatus, string? failureReason = null, string? summary = null)
    {
        if (_outcomeTracker is null)
        {
            return;
        }

        // Record role completions for outcome tracking
        _outcomeTracker.RecordRoleCompletion(_taskId, SwarmRole.Planner, !string.IsNullOrEmpty(_planningOutput));
        _outcomeTracker.RecordRoleCompletion(_taskId, SwarmRole.Builder, !string.IsNullOrEmpty(_buildOutput));
        _outcomeTracker.RecordRoleCompletion(_taskId, SwarmRole.Reviewer,
            !string.IsNullOrEmpty(_reviewOutput) && !ContainsRejection(_reviewOutput));

        // Finalize and persist the outcome
        _ = _outcomeTracker.FinalizeOutcomeAsync(_taskId, finalStatus, failureReason, summary);
    }

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

        _uiEvents.Publish(
            type: "agui.telemetry.quality",
            taskId: _taskId,
            payload: new TelemetryQualityPayload(
                message.Role.ToString().ToLowerInvariant(),
                message.Confidence,
                _retryCount,
                message.Concern));

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

    private void OnRoleLifecycleEvent(RoleLifecycleEvent message)
    {
        // Only process lifecycle events for this task
        if (!message.TaskId.Equals(_taskId, StringComparison.Ordinal))
        {
            return;
        }

        if (message.Phase == "started")
        {
            _uiEvents.Publish(
                type: "agui.role.started",
                taskId: _taskId,
                payload: new RoleStartedPayload(
                    message.Role.ToString().ToLowerInvariant(),
                    _taskId,
                    message.ActorName));

            // Persist role.started lifecycle event (best-effort, fire-and-forget)
            _ = _eventRecorder?.RecordRoleStartedAsync(
                _taskId, _runId,
                message.Role.ToString().ToLowerInvariant());
        }
    }
}
