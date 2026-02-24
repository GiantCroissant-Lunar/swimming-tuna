using Akka.Actor;
using Akka.TestKit.Xunit2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Contracts.Planning;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Planning;
using SwarmAssistant.Runtime.Tasks;
using SwarmAssistant.Runtime.Telemetry;
using SwarmAssistant.Runtime.Ui;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tests;

/// <summary>
/// Reliability tests for HITL intervention actions, coordinator transitions under
/// interventions, and negative/edge-case scenarios (Phase 4 Issue).
/// Covers: approve_review, reject_review, request_rework, pause/resume, set_subtask_depth,
/// invalid states, missing payloads, stale task IDs, unknown actions, and
/// DispatcherActor routing.
/// </summary>
public sealed class InterventionReliabilityTests : TestKit
{
    private readonly RuntimeOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RuntimeTelemetry _telemetry;

    public InterventionReliabilityTests()
    {
        _options = new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "subscription-cli-fallback",
            CliAdapterOrder = ["local-echo"],
            WorkerPoolSize = 1,
            ReviewerPoolSize = 1,
            MaxCliConcurrency = 4,
            SandboxMode = "none",
        };

        _loggerFactory = NullLoggerFactory.Instance;
        _telemetry = new RuntimeTelemetry(_options, _loggerFactory);
    }

    // ── approve_review ────────────────────────────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_ApproveReview_WhenReviewing_Accepted()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "approve-ok", TaskState.Reviewing);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "approve_review", Comment: "LGTM"), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.True(result.Accepted);
        Assert.Equal("approved", result.ReasonCode);
    }

    [Fact]
    public void TaskCoordinatorActor_ApproveReview_WhenReviewing_EmitsInterventionEvent()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "approve-evt", TaskState.Reviewing);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "approve_review"), parentProbe);
        parentProbe.ExpectMsg<TaskInterventionResult>(r => r.Accepted, TimeSpan.FromSeconds(5));

        var evt = PollForEvent(uiEvents,
            e => e.Type == "agui.task.intervention" && e.TaskId == taskId,
            TimeSpan.FromSeconds(5));

        Assert.NotNull(evt);
        var payload = Assert.IsType<TaskInterventionPayload>(evt!.Payload);
        Assert.Equal("approve_review", payload.ActionId);
        Assert.Equal("human", payload.DecidedBy);
    }

    [Fact]
    public void TaskCoordinatorActor_ApproveReview_WhenNotReviewing_RejectedWithInvalidState()
    {
        // Task is in Queued state (initial), not Reviewing
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "approve-bad-state", TaskState.Queued);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "approve_review"), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.False(result.Accepted);
        Assert.Equal("invalid_state", result.ReasonCode);
    }

    // ── reject_review ─────────────────────────────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_RejectReview_WhenReviewing_WithReason_Accepted()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "reject-ok", TaskState.Reviewing);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "reject_review", Reason: "Output is incorrect"), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.True(result.Accepted);
        Assert.Equal("rejected", result.ReasonCode);
    }

    [Fact]
    public void TaskCoordinatorActor_RejectReview_WhenReviewing_EmitsInterventionEvent()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "reject-evt", TaskState.Reviewing);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "reject_review", Reason: "Does not meet criteria"), parentProbe);
        parentProbe.ExpectMsg<TaskInterventionResult>(r => r.Accepted, TimeSpan.FromSeconds(5));

        var evt = PollForEvent(uiEvents,
            e => e.Type == "agui.task.intervention" && e.TaskId == taskId,
            TimeSpan.FromSeconds(5));

        Assert.NotNull(evt);
        var payload = Assert.IsType<TaskInterventionPayload>(evt!.Payload);
        Assert.Equal("reject_review", payload.ActionId);
        Assert.Equal("human", payload.DecidedBy);
    }

    [Fact]
    public void TaskCoordinatorActor_RejectReview_WhenNotReviewing_RejectedWithInvalidState()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "reject-bad-state", TaskState.Building);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "reject_review", Reason: "reason"), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.False(result.Accepted);
        Assert.Equal("invalid_state", result.ReasonCode);
    }

    [Fact]
    public void TaskCoordinatorActor_RejectReview_WithoutReason_RejectedWithPayloadInvalid()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "reject-no-reason", TaskState.Reviewing);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "reject_review"), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.False(result.Accepted);
        Assert.Equal("payload_invalid", result.ReasonCode);
    }

    // ── request_rework ────────────────────────────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_RequestRework_WhenBuilding_WithFeedback_Accepted()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "rework-building", TaskState.Building);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "request_rework", Feedback: "Fix the auth module"), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.True(result.Accepted);
        Assert.Equal("rework_requested", result.ReasonCode);
    }

    [Fact]
    public void TaskCoordinatorActor_RequestRework_WhenReviewing_WithFeedback_Accepted()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "rework-reviewing", TaskState.Reviewing);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "request_rework", Feedback: "Needs more tests"), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.True(result.Accepted);
        Assert.Equal("rework_requested", result.ReasonCode);
    }

    [Fact]
    public void TaskCoordinatorActor_RequestRework_WhenReviewing_EmitsInterventionEvent()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "rework-evt", TaskState.Reviewing);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "request_rework", Feedback: "Fix errors"), parentProbe);
        parentProbe.ExpectMsg<TaskInterventionResult>(r => r.Accepted, TimeSpan.FromSeconds(5));

        var evt = PollForEvent(uiEvents,
            e => e.Type == "agui.task.intervention" && e.TaskId == taskId,
            TimeSpan.FromSeconds(5));

        Assert.NotNull(evt);
        var payload = Assert.IsType<TaskInterventionPayload>(evt!.Payload);
        Assert.Equal("request_rework", payload.ActionId);
        Assert.Equal("human", payload.DecidedBy);
    }

    [Fact]
    public void TaskCoordinatorActor_RequestRework_WhenPlanning_RejectedWithInvalidState()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "rework-bad-state", TaskState.Planning);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "request_rework", Feedback: "feedback"), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.False(result.Accepted);
        Assert.Equal("invalid_state", result.ReasonCode);
    }

    [Fact]
    public void TaskCoordinatorActor_RequestRework_WithoutFeedback_RejectedWithPayloadInvalid()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "rework-no-feedback", TaskState.Building);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "request_rework"), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.False(result.Accepted);
        Assert.Equal("payload_invalid", result.ReasonCode);
    }

    // ── pause_task / resume_task edge cases ───────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_PauseTask_WhenAlreadyPaused_RejectedWithInvalidState()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "pause-dup", TaskState.Building);

        var parentProbe = CreateTestProbe();

        coordinator.Tell(new TaskInterventionCommand(taskId, "pause_task"), parentProbe);
        parentProbe.ExpectMsg<TaskInterventionResult>(r => r.Accepted, TimeSpan.FromSeconds(5));

        // Second pause should be rejected
        coordinator.Tell(new TaskInterventionCommand(taskId, "pause_task"), parentProbe);
        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.False(result.Accepted);
        Assert.Equal("invalid_state", result.ReasonCode);
    }

    [Fact]
    public void TaskCoordinatorActor_ResumeTask_WhenNotPaused_RejectedWithInvalidState()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "resume-not-paused", TaskState.Building);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "resume_task"), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.False(result.Accepted);
        Assert.Equal("invalid_state", result.ReasonCode);
    }

    [Fact]
    public void TaskCoordinatorActor_PauseTask_WhenDone_RejectedWithInvalidState()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "pause-done", TaskState.Done);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "pause_task"), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.False(result.Accepted);
        Assert.Equal("invalid_state", result.ReasonCode);
    }

    // ── set_subtask_depth negative tests ─────────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_SetSubtaskDepth_WithNegativeValue_RejectedWithPayloadInvalid()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "depth-negative", TaskState.Queued);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "set_subtask_depth", MaxSubTaskDepth: -1), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.False(result.Accepted);
        Assert.Equal("payload_invalid", result.ReasonCode);
    }

    [Fact]
    public void TaskCoordinatorActor_SetSubtaskDepth_BeyondMaxAllowed_RejectedWithPayloadInvalid()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "depth-too-large", TaskState.Queued);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "set_subtask_depth",
            MaxSubTaskDepth: TaskCoordinatorActor.MaxAllowedSubTaskDepth + 1), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.False(result.Accepted);
        Assert.Equal("payload_invalid", result.ReasonCode);
    }

    [Fact]
    public void TaskCoordinatorActor_SetSubtaskDepth_WithValidValue_BeforePlanning_Accepted()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "depth-valid", TaskState.Queued);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "set_subtask_depth", MaxSubTaskDepth: 3), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.True(result.Accepted);
        Assert.Equal("subtask_depth_updated", result.ReasonCode);
    }

    // ── stale / wrong task ID ─────────────────────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_WrongTaskId_RejectedWithTaskMismatch()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "mismatch", TaskState.Building);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand("wrong-task-id", "pause_task"), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.False(result.Accepted);
        Assert.Equal("task_mismatch", result.ReasonCode);
    }

    // ── unknown action ────────────────────────────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_UnknownAction_RejectedWithUnsupportedAction()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "unknown-action", TaskState.Building);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "teleport_task"), parentProbe);

        var result = parentProbe.ExpectMsg<TaskInterventionResult>(TimeSpan.FromSeconds(5));
        Assert.False(result.Accepted);
        Assert.Equal("unsupported_action", result.ReasonCode);
    }

    // ── DispatcherActor routing ────────────────────────────────────────────────

    [Fact]
    public async Task DispatcherActor_InterventionForUnknownTaskId_ReturnsTaskNotFound()
    {
        var dispatcher = BuildDispatcher("routing-unknown");

        var result = await dispatcher.Ask<TaskInterventionResult>(
            new TaskInterventionCommand("task-does-not-exist", "pause_task"),
            TimeSpan.FromSeconds(5));

        Assert.False(result.Accepted);
        Assert.Equal("task_not_found", result.ReasonCode);
    }

    [Fact]
    public async Task DispatcherActor_InterventionForKnownTask_ForwardsToCoordinator()
    {
        var dispatcher = BuildDispatcher("routing-known");
        var registry = GetRegistry("routing-known");

        var taskId = $"task-routing-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Test", "desc", DateTimeOffset.UtcNow));
        dispatcher.Tell(new TaskAssigned(taskId, "Test", "desc", DateTimeOffset.UtcNow));

        // Pause intervention should be forwarded and acknowledged (task is in Queued)
        await AwaitAssertAsync(async () =>
        {
            var result = await dispatcher.Ask<TaskInterventionResult>(
                new TaskInterventionCommand(taskId, "pause_task"),
                TimeSpan.FromSeconds(5));

            Assert.True(result.Accepted);
            Assert.Equal("paused", result.ReasonCode);
        }, TimeSpan.FromSeconds(5));
    }

    // ── Approval workflow: approve_review emits agui.task.intervention exactly once ──

    [Fact]
    public void TaskCoordinatorActor_ApproveReview_EmitsExactlyOneInterventionEvent()
    {
        var (taskId, coordinator, registry, uiEvents) = BuildCoordinatorInState(
            "approve-single-evt", TaskState.Reviewing);

        var parentProbe = CreateTestProbe();
        coordinator.Tell(new TaskInterventionCommand(taskId, "approve_review"), parentProbe);
        parentProbe.ExpectMsg<TaskInterventionResult>(r => r.Accepted, TimeSpan.FromSeconds(5));

        AwaitAssert(() =>
        {
            var interventionEvents = uiEvents.GetRecent(200)
                .Where(e => e.Type == "agui.task.intervention" && e.TaskId == taskId)
                .ToList();

            Assert.Single(interventionEvents);
        }, TimeSpan.FromSeconds(5));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private readonly Dictionary<string, TaskRegistry> _registries = new(StringComparer.Ordinal);

    private (string taskId, IActorRef coordinator, TaskRegistry registry, UiEventStream uiEvents)
        BuildCoordinatorInState(string suffix, TaskState state)
    {
        var registry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
        var uiEvents = new UiEventStream();
        var goapPlanner = new GoapPlanner(SwarmActions.All);
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);

        var parentProbe = CreateTestProbe($"parent-{suffix}");
        var workerProbe = CreateTestProbe($"worker-{suffix}");
        var reviewerProbe = CreateTestProbe($"reviewer-{suffix}");
        var supervisorProbe = CreateTestProbe($"supervisor-{suffix}");
        var blackboardProbe = CreateTestProbe($"blackboard-{suffix}");

        var taskId = $"task-{suffix}-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Task", "desc", DateTimeOffset.UtcNow));

        // Advance registry to the requested state so CurrentStatus checks pass
        if (state != TaskState.Queued)
        {
            registry.Transition(taskId, state);
        }

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId, "Task", "desc",
                workerProbe, reviewerProbe, supervisorProbe, blackboardProbe,
                ActorRefs.Nobody, roleEngine, goapPlanner, _loggerFactory, _telemetry, uiEvents, registry, _options,
                null, null, null, 2, 0, null)));

        _registries[suffix] = registry;
        return (taskId, coordinator, registry, uiEvents);
    }

    private TaskRegistry GetRegistry(string suffix) =>
        _registries.TryGetValue(suffix, out var r) ? r : throw new InvalidOperationException($"No registry for '{suffix}'");

    private IActorRef BuildDispatcher(string suffix)
    {
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);
        var uiEvents = new UiEventStream();
        var registry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
        _registries[suffix] = registry;

        var workerProbe = CreateTestProbe($"w-{suffix}");
        var reviewerProbe = CreateTestProbe($"rv-{suffix}");
        var supervisorProbe = CreateTestProbe($"sup-{suffix}");
        var blackboardProbe = CreateTestProbe($"bb-{suffix}");
        var consensusProbe = CreateTestProbe($"cs-{suffix}");

        return Sys.ActorOf(
            Props.Create(() => new DispatcherActor(
                workerProbe,
                reviewerProbe,
                supervisorProbe,
                blackboardProbe,
                consensusProbe,
                roleEngine,
                _loggerFactory,
                _telemetry,
                uiEvents,
                registry,
                Options.Create(_options),
                null,
                null,
                null,
                null)),
            $"disp-{suffix}");
    }

    private static UiEventEnvelope? PollForEvent(
        UiEventStream eventStream,
        Func<UiEventEnvelope, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var evt = eventStream.GetRecent(200).FirstOrDefault(predicate);
            if (evt != null) return evt;
            Thread.Sleep(20);
        }

        return null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var registry in _registries.Values)
            {
                // Safe in TestKit teardown: no synchronization context is captured here.
                registry.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            _loggerFactory.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class NoOpTaskMemoryWriter : ITaskMemoryWriter
    {
        public Task WriteAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
