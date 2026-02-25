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
/// Tests verifying that deterministic lifecycle events are emitted from
/// Coordinator/Worker/Reviewer/Dispatcher actors (Phase 3 Issue 09).
/// </summary>
public sealed class LifecycleEventsTests : TestKit
{
    private readonly RuntimeOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RuntimeTelemetry _telemetry;
    private readonly TaskRegistry _taskRegistry;

    public LifecycleEventsTests()
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
        _taskRegistry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
    }

    // ── agui.role.dispatched ─────────────────────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_PlanAction_EmitsRoleDispatchedEvent()
    {
        var uiEvents = new UiEventStream();
        var coordinator = BuildCoordinator("dispatch-plan", uiEvents);
        var taskId = coordinator.Path.Name.Replace("coord-", "");

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());

        // coordinator emits agui.role.dispatched when it dispatches to the worker
        var evt = PollForEvent(uiEvents,
            e => e.Type == "agui.role.dispatched" && e.TaskId == taskId,
            TimeSpan.FromSeconds(10));

        Assert.NotNull(evt);
        var payload = Assert.IsType<RoleDispatchedPayload>(evt!.Payload);
        Assert.Equal(taskId, payload.TaskId);
        // The first role dispatched after orchestrator decision is planner or builder
        Assert.NotEmpty(payload.Role);
    }

    // ── agui.role.started ────────────────────────────────────────────────────

    [Fact]
    public void WorkerActor_ExecuteRoleTask_EmitsRoleStartedViaEventStream()
    {
        var uiEvents = new UiEventStream();
        var coordinator = BuildCoordinator("role-started", uiEvents);
        var taskId = coordinator.Path.Name.Replace("coord-", "");

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());

        // After dispatcher sends task to worker, worker publishes RoleLifecycleEvent
        // and coordinator forwards it as agui.role.started
        var evt = PollForEvent(uiEvents,
            e => e.Type == "agui.role.started" && e.TaskId == taskId,
            TimeSpan.FromSeconds(15));

        Assert.NotNull(evt);
        var payload = Assert.IsType<RoleStartedPayload>(evt!.Payload);
        Assert.Equal(taskId, payload.TaskId);
        Assert.NotEmpty(payload.Role);
    }

    // ── agui.role.succeeded ──────────────────────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_OnRoleSucceeded_EmitsRoleSucceededEvent()
    {
        var parentProbe = CreateTestProbe();
        var supervisorProbe = CreateTestProbe();
        var blackboardProbe = CreateTestProbe();
        var workerProbe = CreateTestProbe();
        var reviewerProbe = CreateTestProbe();

        var registry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
        var goapPlanner = new GoapPlanner(SwarmActions.All);
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);
        var uiEvents = new UiEventStream();

        var taskId = $"role-succ-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Task", "desc", DateTimeOffset.UtcNow));

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId, "Task", "desc",
                workerProbe, reviewerProbe, supervisorProbe, blackboardProbe,
                ActorRefs.Nobody, roleEngine, goapPlanner, _loggerFactory, _telemetry, uiEvents, registry, _options, null, null, null, 2, 0, null, null, null, null)));

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Orchestrator, TimeSpan.FromSeconds(5));

        // Orchestrator decides Plan
        coordinator.Tell(new RoleTaskSucceeded(
            taskId, SwarmRole.Orchestrator, "ACTION: Plan", DateTimeOffset.UtcNow, 0.9, null, "worker"));
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Planner, TimeSpan.FromSeconds(5));

        // Planner succeeds
        coordinator.Tell(new RoleTaskSucceeded(
            taskId, SwarmRole.Planner, "plan output with no subtasks", DateTimeOffset.UtcNow, 0.85, null, "worker"));

        // Check for role.succeeded
        var evt = PollForEvent(uiEvents,
            e => e.Type == "agui.role.succeeded" && e.TaskId == taskId,
            TimeSpan.FromSeconds(5));

        Assert.NotNull(evt);
        var payload = Assert.IsType<RoleSucceededPayload>(evt!.Payload);
        Assert.Equal("planner", payload.Role);
        Assert.Equal(taskId, payload.TaskId);
        Assert.True(payload.Confidence > 0.0);
    }

    // ── agui.role.failed ─────────────────────────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_OnRoleFailed_EmitsRoleFailedEvent()
    {
        var parentProbe = CreateTestProbe();
        var supervisorProbe = CreateTestProbe();
        var blackboardProbe = CreateTestProbe();
        var workerProbe = CreateTestProbe();
        var reviewerProbe = CreateTestProbe();

        var registry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
        var goapPlanner = new GoapPlanner(SwarmActions.All);
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);
        var uiEvents = new UiEventStream();

        var taskId = $"role-fail-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Task", "desc", DateTimeOffset.UtcNow));

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId, "Task", "desc",
                workerProbe, reviewerProbe, supervisorProbe, blackboardProbe,
                ActorRefs.Nobody, roleEngine, goapPlanner, _loggerFactory, _telemetry, uiEvents, registry, _options, null, null, null, 2, 0, null, null, null, null)));

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Orchestrator, TimeSpan.FromSeconds(5));

        // Orchestrator decides Plan
        coordinator.Tell(new RoleTaskSucceeded(
            taskId, SwarmRole.Orchestrator, "ACTION: Plan", DateTimeOffset.UtcNow, 0.9, null, "worker"));
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Planner, TimeSpan.FromSeconds(5));

        // Planner fails
        coordinator.Tell(new RoleTaskFailed(
            taskId, SwarmRole.Planner, "planning error", DateTimeOffset.UtcNow));

        var evt = PollForEvent(uiEvents,
            e => e.Type == "agui.role.failed" && e.TaskId == taskId,
            TimeSpan.FromSeconds(5));

        Assert.NotNull(evt);
        var payload = Assert.IsType<RoleFailedPayload>(evt!.Payload);
        Assert.Equal("planner", payload.Role);
        Assert.Equal(taskId, payload.TaskId);
        Assert.Equal("planning error", payload.Error);
    }

    // ── agui.task.escalated (via HandleEscalation) ───────────────────────────

    [Fact]
    public void TaskCoordinatorActor_HandleEscalation_EmitsTaskEscalatedEvent()
    {
        var parentProbe = CreateTestProbe();
        var supervisorProbe = CreateTestProbe();
        var blackboardProbe = CreateTestProbe();
        var workerProbe = CreateTestProbe();
        var reviewerProbe = CreateTestProbe();

        var registry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
        var goapPlanner = new GoapPlanner(SwarmActions.All);
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);
        var uiEvents = new UiEventStream();

        // Trigger explicit escalation path
        var taskId = $"escalate-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Task", "desc", DateTimeOffset.UtcNow));

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId, "Task", "desc",
                workerProbe, reviewerProbe, supervisorProbe, blackboardProbe,
                ActorRefs.Nobody, roleEngine, goapPlanner, _loggerFactory, _telemetry, uiEvents, registry, _options, null, null, null, 0, 0, null, null, null, null)));

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Orchestrator, TimeSpan.FromSeconds(5));

        coordinator.Tell(new RoleTaskSucceeded(
            taskId, SwarmRole.Orchestrator, "ACTION: Escalate", DateTimeOffset.UtcNow, 0.9, null, "worker"));

        var escalatedEvt = PollForEvent(uiEvents,
            e => e.Type == "agui.task.escalated" && e.TaskId == taskId,
            TimeSpan.FromSeconds(5));
        Assert.NotNull(escalatedEvt);

        var failedEvt = PollForEvent(uiEvents,
            e => e.Type == "agui.task.failed" && e.TaskId == taskId,
            TimeSpan.FromSeconds(5));
        Assert.NotNull(failedEvt);
        Assert.True(escalatedEvt!.Sequence < failedEvt!.Sequence);
    }

    // ── agui.task.escalated (via HandleDeadEnd) ──────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_HandleDeadEnd_EmitsTaskEscalatedEvent()
    {
        var parentProbe = CreateTestProbe();
        var supervisorProbe = CreateTestProbe();
        var blackboardProbe = CreateTestProbe();
        var workerProbe = CreateTestProbe();
        var reviewerProbe = CreateTestProbe();

        var registry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
        var goapPlanner = new GoapPlanner(SwarmActions.All);
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);
        var uiEvents = new UiEventStream();

        var taskId = $"deadend-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Task", "desc", DateTimeOffset.UtcNow));

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId, "Task", "desc",
                workerProbe, reviewerProbe, supervisorProbe, blackboardProbe,
                ActorRefs.Nobody, roleEngine, goapPlanner, _loggerFactory, _telemetry, uiEvents, registry, _options, null, null, null, 2, 0, null, null, null, null)));

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Orchestrator, TimeSpan.FromSeconds(5));

        // Orchestrator decides Escalate to force the HandleEscalation path
        coordinator.Tell(new RoleTaskSucceeded(
            taskId, SwarmRole.Orchestrator, "ACTION: Escalate", DateTimeOffset.UtcNow, 0.9, null, "worker"));

        var evt = PollForEvent(uiEvents,
            e => e.Type == "agui.task.escalated" && e.TaskId == taskId,
            TimeSpan.FromSeconds(5));

        Assert.NotNull(evt);
        var payload = Assert.IsType<TaskEscalatedPayload>(evt!.Payload);
        Assert.Equal(taskId, payload.TaskId);
        Assert.Equal(2, payload.Level);
        Assert.NotEmpty(payload.Reason);
    }

    // ── agui.task.intervention ───────────────────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_PauseTask_EmitsInterventionEvent()
    {
        var parentProbe = CreateTestProbe();
        var supervisorProbe = CreateTestProbe();
        var blackboardProbe = CreateTestProbe();
        var workerProbe = CreateTestProbe();
        var reviewerProbe = CreateTestProbe();

        var registry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
        var goapPlanner = new GoapPlanner(SwarmActions.All);
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);
        var uiEvents = new UiEventStream();

        var taskId = $"intervention-pause-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Task", "desc", DateTimeOffset.UtcNow));

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId, "Task", "desc",
                workerProbe, reviewerProbe, supervisorProbe, blackboardProbe,
                ActorRefs.Nobody, roleEngine, goapPlanner, _loggerFactory, _telemetry, uiEvents, registry, _options, null, null, null, 2, 0, null, null, null, null)));

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Orchestrator, TimeSpan.FromSeconds(5));

        // Pause the task
        coordinator.Tell(new TaskInterventionCommand(taskId, "pause_task"), parentProbe);
        parentProbe.ExpectMsg<TaskInterventionResult>(r => r.Accepted, TimeSpan.FromSeconds(5));

        var evt = PollForEvent(uiEvents,
            e => e.Type == "agui.task.intervention" && e.TaskId == taskId,
            TimeSpan.FromSeconds(5));

        Assert.NotNull(evt);
        var payload = Assert.IsType<TaskInterventionPayload>(evt!.Payload);
        Assert.Equal(taskId, payload.TaskId);
        Assert.Equal("pause_task", payload.ActionId);
        Assert.Equal("human", payload.DecidedBy);
    }

    [Fact]
    public void TaskCoordinatorActor_ResumeTask_EmitsInterventionEvent()
    {
        var parentProbe = CreateTestProbe();
        var supervisorProbe = CreateTestProbe();
        var blackboardProbe = CreateTestProbe();
        var workerProbe = CreateTestProbe();
        var reviewerProbe = CreateTestProbe();

        var registry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
        var goapPlanner = new GoapPlanner(SwarmActions.All);
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);
        var uiEvents = new UiEventStream();

        var taskId = $"intervention-resume-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Task", "desc", DateTimeOffset.UtcNow));

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId, "Task", "desc",
                workerProbe, reviewerProbe, supervisorProbe, blackboardProbe,
                ActorRefs.Nobody, roleEngine, goapPlanner, _loggerFactory, _telemetry, uiEvents, registry, _options, null, null, null, 2, 0, null, null, null, null)));

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Orchestrator, TimeSpan.FromSeconds(5));

        // Pause first
        coordinator.Tell(new TaskInterventionCommand(taskId, "pause_task"), parentProbe);
        parentProbe.ExpectMsg<TaskInterventionResult>(r => r.Accepted, TimeSpan.FromSeconds(5));

        // Resume
        coordinator.Tell(new TaskInterventionCommand(taskId, "resume_task"), parentProbe);
        parentProbe.ExpectMsg<TaskInterventionResult>(r => r.Accepted, TimeSpan.FromSeconds(5));

        var events = PollForEvents(uiEvents,
            e => e.Type == "agui.task.intervention" && e.TaskId == taskId,
            count: 2,
            TimeSpan.FromSeconds(5));

        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => ((TaskInterventionPayload)e.Payload).ActionId == "pause_task");
        Assert.Contains(events, e => ((TaskInterventionPayload)e.Payload).ActionId == "resume_task");
    }

    // ── No duplicate events on normal path ───────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_NoDuplicateRoleDispatchedEvents_OnNormalPath()
    {
        var parentProbe = CreateTestProbe();
        var supervisorProbe = CreateTestProbe();
        var blackboardProbe = CreateTestProbe();
        var workerProbe = CreateTestProbe();
        var reviewerProbe = CreateTestProbe();

        var registry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
        var goapPlanner = new GoapPlanner(SwarmActions.All);
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);
        var uiEvents = new UiEventStream();

        var taskId = $"no-dup-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Task", "desc", DateTimeOffset.UtcNow));

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId, "Task", "desc",
                workerProbe, reviewerProbe, supervisorProbe, blackboardProbe,
                ActorRefs.Nobody, roleEngine, goapPlanner, _loggerFactory, _telemetry, uiEvents, registry, _options, null, null, null, 2, 0, null, null, null, null)));

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Orchestrator, TimeSpan.FromSeconds(5));

        // Orchestrator decides Plan
        coordinator.Tell(new RoleTaskSucceeded(
            taskId, SwarmRole.Orchestrator, "ACTION: Plan", DateTimeOffset.UtcNow, 0.9, null, "worker"));
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Planner, TimeSpan.FromSeconds(5));

        // Planner succeeds
        coordinator.Tell(new RoleTaskSucceeded(
            taskId, SwarmRole.Planner, "plan output with no subtasks", DateTimeOffset.UtcNow, 0.85, null, "worker"));

        // Small delay for events to settle
        Thread.Sleep(100);

        // Check there is exactly one agui.role.dispatched for "planner"
        var dispatchedEvents = uiEvents.GetRecent(200)
            .Where(e => e.Type == "agui.role.dispatched"
                && e.TaskId == taskId
                && ((RoleDispatchedPayload)e.Payload).Role == "planner")
            .ToList();

        Assert.Single(dispatchedEvents);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private (string taskId, IActorRef coordinator) CreateCoordinatorWithProbes(
        string suffix,
        UiEventStream uiEvents,
        IActorRef workerProbe,
        IActorRef reviewerProbe,
        IActorRef supervisorProbe,
        IActorRef blackboardProbe)
    {
        var registry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
        var goapPlanner = new GoapPlanner(SwarmActions.All);
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);
        var parentProbe = CreateTestProbe($"parent-{suffix}");

        var taskId = $"lc-{suffix}-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Task", "desc", DateTimeOffset.UtcNow));

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId, "Task", "desc",
                workerProbe, reviewerProbe, supervisorProbe, blackboardProbe,
                ActorRefs.Nobody, roleEngine, goapPlanner, _loggerFactory, _telemetry, uiEvents, registry, _options, null, null, null, 2, 0, null, null, null, null)));

        return (taskId, coordinator);
    }

    /// <summary>
    /// Builds a real dispatcher + worker/reviewer actors for end-to-end tests.
    /// Returns a coordinator actor ref with a predictable path for taskId extraction.
    /// </summary>
    private IActorRef BuildCoordinator(string suffix, UiEventStream uiEvents)
    {
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);
        var parentProbe = CreateTestProbe($"parent-{suffix}");
        var supervisorProbe = CreateTestProbe($"sup-{suffix}");
        var blackboardProbe = CreateTestProbe($"bb-{suffix}");

        var workerActor = Sys.ActorOf(
            Props.Create(() => new WorkerActor(_options, _loggerFactory, roleEngine, _telemetry, null)),
            $"worker-{suffix}");

        var reviewerActor = Sys.ActorOf(
            Props.Create(() => new ReviewerActor(_options, _loggerFactory, roleEngine, _telemetry)),
            $"reviewer-{suffix}");

        var registry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
        var goapPlanner = new GoapPlanner(SwarmActions.All);
        var taskId = $"lc-{suffix}";
        registry.Register(new TaskAssigned(taskId, "Task", "desc", DateTimeOffset.UtcNow));

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId, "Task", "desc",
                workerActor, reviewerActor, supervisorProbe, blackboardProbe,
                ActorRefs.Nobody, roleEngine, goapPlanner, _loggerFactory, _telemetry, uiEvents, registry, _options, null, null, null, 2, 0, null, null, null, null)),
            $"coord-{taskId}");

        return coordinator;
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

    private static List<UiEventEnvelope> PollForEvents(
        UiEventStream eventStream,
        Func<UiEventEnvelope, bool> predicate,
        int count,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var events = eventStream.GetRecent(200).Where(predicate).ToList();
            if (events.Count >= count) return events;
            Thread.Sleep(20);
        }

        return eventStream.GetRecent(200).Where(predicate).ToList();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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
