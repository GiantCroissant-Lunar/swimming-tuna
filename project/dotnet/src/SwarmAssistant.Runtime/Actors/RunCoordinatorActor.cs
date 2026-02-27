using Akka.Actor;
using Microsoft.Extensions.Options;
using SwarmAssistant.Contracts.Hierarchy;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Contracts.Planning;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Execution;
using SwarmAssistant.Runtime.Langfuse;
using SwarmAssistant.Runtime.Memvid;
using SwarmAssistant.Runtime.Planning;
using SwarmAssistant.Runtime.Skills;
using SwarmAssistant.Runtime.Tasks;
using SwarmAssistant.Runtime.Telemetry;
using SwarmAssistant.Runtime.Ui;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Actors;

/// <summary>
/// Per-run coordinator that manages run-scoped task lifecycle.
/// When tasks in a run complete, receives RunTaskCompleted notifications
/// and tracks completion status for the entire run.
/// </summary>
public sealed class RunCoordinatorActor : ReceiveActor
{
    private readonly IActorRef _workerActor;
    private readonly IActorRef _reviewerActor;
    private readonly IActorRef _supervisorActor;
    private readonly IActorRef _blackboardActor;
    private readonly IActorRef _consensusActor;
    private readonly AgentFrameworkRoleEngine _roleEngine;
    private readonly IGoapPlanner _goapPlanner;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RuntimeTelemetry _telemetry;
    private readonly UiEventStream _uiEvents;
    private readonly TaskRegistry _taskRegistry;
    private readonly RuntimeOptions _options;
    private readonly OutcomeTracker? _outcomeTracker;
    private readonly IActorRef? _strategyAdvisorActor;
    private readonly RuntimeEventRecorder? _eventRecorder;
    private readonly IActorRef? _codeIndexActor;
    private readonly string? _projectContext;
    private readonly WorkspaceBranchManager? _workspaceBranchManager;
    private readonly BuildVerifier? _buildVerifier;
    private readonly SandboxLevelEnforcer? _sandboxEnforcer;
    private readonly ILangfuseScoreWriter? _langfuseScoreWriter;
    private readonly ILangfuseSimilarityQuery? _langfuseSimilarityQuery;
    private readonly SkillMatcher? _skillMatcher;
    private readonly MemvidClient? _memvidClient;
    private readonly ILogger _logger;

    private readonly string _runId;
    private readonly string? _title;
    private readonly Dictionary<string, IActorRef> _taskCoordinators = new(StringComparer.Ordinal);
    private readonly Dictionary<IActorRef, string> _coordinatorTaskIds = new();
    private readonly HashSet<string> _completedTasks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedTasks = new(StringComparer.Ordinal);
    private int _totalTaskCount;
    private int _mergedTaskCount;

    // Lifecycle state machine
    private RunSpanStatus _status = RunSpanStatus.Accepted;
    private string? _featureBranch;
    private string? _baseBranch;
    private string? _branchPrefix;
    private readonly SemaphoreSlim _mergeLock = new(1, 1);

    private const int DefaultMaxRetries = 2;

    public RunCoordinatorActor(
        string runId,
        string? title,
        string? baseBranch,
        string? branchPrefix,
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
        IOptions<RuntimeOptions> options,
        OutcomeTracker? outcomeTracker = null,
        IActorRef? strategyAdvisorActor = null,
        RuntimeEventRecorder? eventRecorder = null,
        IActorRef? codeIndexActor = null,
        string? projectContext = null,
        WorkspaceBranchManager? workspaceBranchManager = null,
        BuildVerifier? buildVerifier = null,
        SandboxLevelEnforcer? sandboxEnforcer = null,
        ILangfuseScoreWriter? langfuseScoreWriter = null,
        MemvidClient? memvidClient = null,
        ILangfuseSimilarityQuery? langfuseSimilarityQuery = null,
        SkillMatcher? skillMatcher = null)
    {
        _runId = runId;
        _title = title;
        _baseBranch = baseBranch ?? "main";
        _branchPrefix = branchPrefix ?? "feat";
        _workerActor = workerActor;
        _reviewerActor = reviewerActor;
        _supervisorActor = supervisorActor;
        _blackboardActor = blackboardActor;
        _consensusActor = consensusActor;
        _roleEngine = roleEngine;
        _goapPlanner = goapPlanner;
        _loggerFactory = loggerFactory;
        _telemetry = telemetry;
        _uiEvents = uiEvents;
        _taskRegistry = taskRegistry;
        _options = options.Value;
        _outcomeTracker = outcomeTracker;
        _strategyAdvisorActor = strategyAdvisorActor;
        _eventRecorder = eventRecorder;
        _codeIndexActor = codeIndexActor;
        _projectContext = projectContext;
        _workspaceBranchManager = workspaceBranchManager;
        _buildVerifier = buildVerifier;
        _sandboxEnforcer = sandboxEnforcer;
        _langfuseScoreWriter = langfuseScoreWriter;
        _langfuseSimilarityQuery = langfuseSimilarityQuery;
        _skillMatcher = skillMatcher;
        _memvidClient = memvidClient;
        _logger = loggerFactory.CreateLogger<RunCoordinatorActor>();

        Receive<TaskAssigned>(HandleTaskAssigned);
        Receive<RunTaskCompleted>(HandleRunTaskCompleted);
        Receive<Terminated>(OnCoordinatorTerminated);
        Receive<RunConfigured>(HandleRunConfigured);
        Receive<RunMarkDone>(HandleRunMarkDone);

        EmitRunEvent("agui.run.accepted", new { runId, title });
    }

    protected override void PostStop()
    {
        _mergeLock.Dispose();
        base.PostStop();
    }

    protected override SupervisorStrategy SupervisorStrategy()
    {
        return new OneForOneStrategy(ex => Directive.Stop);
    }

    private void HandleTaskAssigned(TaskAssigned message)
    {
        var taskId = message.TaskId;

        using var activity = _telemetry.StartActivity(
            "run-coordinator.task.assigned",
            taskId: taskId,
            runId: _runId,
            tags: new Dictionary<string, object?>
            {
                ["task.title"] = message.Title,
                ["actor.name"] = Self.Path.Name,
            });

        if (_taskCoordinators.ContainsKey(taskId))
        {
            _logger.LogWarning(
                "Duplicate task assignment ignored taskId={TaskId} runId={RunId}",
                taskId, _runId);
            return;
        }

        // Ensure L0 feature branch exists before dispatching any task
        EnsureFeatureBranch();

        // Transition to Executing on first task
        if (_totalTaskCount == 0 && _status is RunSpanStatus.Accepted or RunSpanStatus.Decomposing)
        {
            TransitionTo(RunSpanStatus.Executing);
            EmitRunEvent("agui.run.executing", new { runId = _runId, taskCount = 1 });
        }

        _taskRegistry.Register(message, _runId);
        RecordTaskSubmitted(taskId, message.Title);

        var coordinator = Context.ActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId,
                message.Title,
                message.Description,
                _workerActor,
                _reviewerActor,
                _supervisorActor,
                _blackboardActor,
                _consensusActor,
                _roleEngine,
                _goapPlanner,
                _loggerFactory,
                _telemetry,
                _uiEvents,
                _taskRegistry,
                _options,
                _outcomeTracker,
                _strategyAdvisorActor,
                _codeIndexActor,
                DefaultMaxRetries,
                0,
                _eventRecorder,
                _projectContext,
                _workspaceBranchManager,
                _buildVerifier,
                _sandboxEnforcer,
                _langfuseScoreWriter,
                _memvidClient,
                _langfuseSimilarityQuery,
                _skillMatcher,
                _featureBranch)),
            $"task-{taskId}");

        _taskCoordinators[taskId] = coordinator;
        _coordinatorTaskIds[coordinator] = taskId;
        Context.Watch(coordinator);
        _totalTaskCount++;

        _logger.LogInformation(
            "Dispatched run-scoped task taskId={TaskId} runId={RunId} title={Title}",
            taskId, _runId, message.Title);

        _uiEvents.Publish(
            type: "agui.task.submitted",
            taskId: taskId,
            payload: new TaskSubmittedPayload(taskId, message.Title, message.Description));

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
    }

    private void HandleRunTaskCompleted(RunTaskCompleted message)
    {
        if (!string.Equals(message.RunId, _runId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Received RunTaskCompleted for mismatched runId={ReceivedRunId} expected={ExpectedRunId}",
                message.RunId, _runId);
            return;
        }

        _logger.LogInformation(
            "Task completed in run taskId={TaskId} runId={RunId} status={Status}",
            message.TaskId, _runId, message.Status);

        switch (message.Status)
        {
            case TaskState.Done:
                _completedTasks.Add(message.TaskId);
                break;

            case TaskState.Blocked:
                _failedTasks.Add(message.TaskId);
                break;

            default:
                _logger.LogWarning(
                    "Unexpected task status in RunTaskCompleted taskId={TaskId} status={Status}",
                    message.TaskId, message.Status);
                break;
        }

        _uiEvents.Publish(
            type: "agui.run.task_completed",
            taskId: message.TaskId,
            payload: new
            {
                runId = _runId,
                taskId = message.TaskId,
                status = message.Status.ToString(),
                summary = message.Summary,
                error = message.Error
            });

        // Merge gate: merge completed task branch into feature branch
        if (message.Status == TaskState.Done && _workspaceBranchManager is not null && _featureBranch is not null)
        {
            _ = MergeTaskAsync(message.TaskId);
        }

        LogRunProgress();
        CheckRunCompletion();
    }

    private async Task MergeTaskAsync(string taskId)
    {
        await _mergeLock.WaitAsync();
        try
        {
            var result = await _workspaceBranchManager.MergeTaskBranchAsync(taskId, _featureBranch!);
            switch (result)
            {
                case MergeResult.Success:
                    _mergedTaskCount++;
                    EmitRunEvent("agui.run.task-merged", new { runId = _runId, taskId });
                    _logger.LogInformation(
                        "Task branch merged taskId={TaskId} into featureBranch={FeatureBranch} runId={RunId}",
                        taskId, _featureBranch, _runId);
                    CheckRunCompletion();
                    break;

                case MergeResult.Conflict:
                    EmitRunEvent("agui.run.merge-conflict", new { runId = _runId, taskId });
                    _logger.LogWarning(
                        "Merge conflict taskId={TaskId} featureBranch={FeatureBranch} runId={RunId}",
                        taskId, _featureBranch, _runId);
                    break;

                case MergeResult.BranchNotFound:
                    _logger.LogWarning(
                        "Task branch not found for merge taskId={TaskId} runId={RunId}",
                        taskId, _runId);
                    break;

                case MergeResult.CheckoutFailed:
                    _logger.LogWarning(
                        "Checkout failed during merge taskId={TaskId} featureBranch={FeatureBranch} runId={RunId}",
                        taskId, _featureBranch, _runId);
                    break;
            }
        }
        finally
        {
            _mergeLock.Release();
        }
    }

    private void CheckRunCompletion()
    {
        var terminalCount = _completedTasks.Count + _failedTasks.Count;
        if (terminalCount < _totalTaskCount || _totalTaskCount == 0)
        {
            return;
        }

        // Prevent double-trigger if already transitioning
        if (_status >= RunSpanStatus.ReadyForPr)
        {
            return;
        }

        // All tasks are in terminal state — claim transition synchronously to prevent race
        if (_featureBranch is not null)
        {
            TransitionTo(RunSpanStatus.ReadyForPr);
            _ = TransitionToReadyForPrAsync();
        }
        else
        {
            TransitionTo(RunSpanStatus.Done);
            EmitRunEvent("agui.run.done", new { runId = _runId });
        }
    }

    private void OnCoordinatorTerminated(Terminated message)
    {
        if (!_coordinatorTaskIds.TryGetValue(message.ActorRef, out var taskId))
        {
            return;
        }

        _taskCoordinators.Remove(taskId);
        _coordinatorTaskIds.Remove(message.ActorRef);

        _logger.LogDebug("Task coordinator removed taskId={TaskId} runId={RunId}", taskId, _runId);

        // If coordinator terminated without sending RunTaskCompleted, check final state
        if (!_completedTasks.Contains(taskId) && !_failedTasks.Contains(taskId))
        {
            var snapshot = _taskRegistry.GetTask(taskId);
            if (snapshot is not null)
            {
                switch (snapshot.Status)
                {
                    case TaskState.Done:
                        _completedTasks.Add(taskId);
                        break;

                    case TaskState.Blocked:
                        _failedTasks.Add(taskId);
                        break;
                }
                LogRunProgress();
            }
        }
    }

    private void RecordTaskSubmitted(string taskId, string title)
    {
        if (_eventRecorder is null)
        {
            return;
        }

        _ = _eventRecorder.RecordTaskSubmittedAsync(taskId, _runId, title);
    }

    private void HandleRunConfigured(RunConfigured message)
    {
        _baseBranch = message.BaseBranch;
        _branchPrefix = message.BranchPrefix;

        _logger.LogInformation(
            "Run configured runId={RunId} baseBranch={BaseBranch} branchPrefix={BranchPrefix}",
            _runId, _baseBranch, _branchPrefix);

        EnsureFeatureBranch();
    }

    /// <summary>
    /// Ensures the L0 feature branch exists before any task worktrees are created.
    /// Called synchronously — git checkout -b is fast (&lt;100ms).
    /// </summary>
    private void EnsureFeatureBranch()
    {
        if (_featureBranch is not null || _workspaceBranchManager is null || string.IsNullOrWhiteSpace(_baseBranch))
        {
            return;
        }

        var slug = ComputeBranchSlug(_title ?? _runId);
        var prefix = string.IsNullOrWhiteSpace(_branchPrefix) ? "feat" : _branchPrefix;
        _featureBranch = $"{prefix}/{slug}";

        var result = WorkspaceBranchManager.CreateBranchFromAsync(_featureBranch, _baseBranch)
            .GetAwaiter().GetResult();

        if (result)
        {
            _logger.LogInformation(
                "L0 feature branch created featureBranch={FeatureBranch} baseBranch={BaseBranch} runId={RunId}",
                _featureBranch, _baseBranch, _runId);
        }
        else
        {
            _logger.LogWarning(
                "Failed to create L0 feature branch featureBranch={FeatureBranch} baseBranch={BaseBranch} runId={RunId}",
                _featureBranch, _baseBranch, _runId);
            _featureBranch = null;
        }
    }

    private void HandleRunMarkDone(RunMarkDone message)
    {
        if (!string.Equals(message.RunId, _runId, StringComparison.Ordinal))
        {
            return;
        }

        TransitionTo(RunSpanStatus.Done);
        EmitRunEvent("agui.run.done", new { runId = _runId });
    }

    private async Task TransitionToReadyForPrAsync()
    {
        if (_featureBranch is null)
        {
            return;
        }

        // Push feature branch (status already set to ReadyForPr by caller)
        var pushed = await WorkspaceBranchManager.PushBranchAsync(_featureBranch);
        if (pushed)
        {
            _logger.LogInformation(
                "Feature branch pushed featureBranch={FeatureBranch} runId={RunId}",
                _featureBranch, _runId);
        }
        else
        {
            _logger.LogWarning(
                "Failed to push feature branch featureBranch={FeatureBranch} runId={RunId}",
                _featureBranch, _runId);
        }

        EmitRunEvent("agui.run.ready-for-pr", new { runId = _runId, featureBranch = _featureBranch });
    }

    private void TransitionTo(RunSpanStatus newStatus)
    {
        var previous = _status;
        _status = newStatus;
        _logger.LogInformation(
            "Run state transition runId={RunId} from={From} to={To}",
            _runId, previous, newStatus);
    }

    private void EmitRunEvent(string type, object payload)
    {
        _uiEvents.Publish(type: type, taskId: null, payload: payload);
    }

    /// <summary>Gets the current run lifecycle status.</summary>
    public RunSpanStatus Status => _status;

    /// <summary>Gets the feature branch name, if created.</summary>
    public string? FeatureBranch => _featureBranch;

    private static string ComputeBranchSlug(string input)
        => WorkspaceBranchManager.ComputeBranchSlug(input);

    private void LogRunProgress()
    {
        var completed = _completedTasks.Count;
        var failed = _failedTasks.Count;
        var remaining = _totalTaskCount - completed - failed;

        _logger.LogInformation(
            "Run progress runId={RunId} completed={Completed} failed={Failed} remaining={Remaining} total={Total}",
            _runId, completed, failed, remaining, _totalTaskCount);
    }
}
