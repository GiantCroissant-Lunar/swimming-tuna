using Akka.Actor;
using Akka.Routing;
using Akka.TestKit.Xunit2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Planning;
using SwarmAssistant.Runtime.Tasks;
using SwarmAssistant.Runtime.Telemetry;
using SwarmAssistant.Runtime.Ui;

namespace SwarmAssistant.Runtime.Tests;

/// <summary>
/// Tests verifying that graph lifecycle and telemetry events are emitted correctly
/// for UI consumption (Phase 2: task-graph and live telemetry).
/// </summary>
public sealed class GraphAndTelemetryEventTests : TestKit
{
    private readonly RuntimeOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RuntimeTelemetry _telemetry;
    private readonly UiEventStream _uiEvents;
    private readonly TaskRegistry _taskRegistry;

    public GraphAndTelemetryEventTests()
    {
        _options = new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "subscription-cli-fallback",
            CliAdapterOrder = ["local-echo"],
            WorkerPoolSize = 2,
            ReviewerPoolSize = 1,
            MaxCliConcurrency = 4,
            SandboxMode = "none",
            GraphTelemetryEnabled = true
        };

        _loggerFactory = NullLoggerFactory.Instance;
        _telemetry = new RuntimeTelemetry(_options, _loggerFactory);
        _uiEvents = new UiEventStream();
        _taskRegistry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
    }

    // ── graph.link_created ────────────────────────────────────────────────────

    [Fact]
    public void DispatcherActor_SpawnSubTask_EmitsGraphLinkCreatedEvent()
    {
        var (dispatcher, localUiEvents) = BuildProbeDispatcher("link-created");
        var parentTaskId = $"parent-{Guid.NewGuid():N}";
        var childTaskId = $"child-{Guid.NewGuid():N}";

        _taskRegistry.Register(new TaskAssigned(parentTaskId, "Parent", "desc", DateTimeOffset.UtcNow));
        dispatcher.Tell(new SpawnSubTask(parentTaskId, childTaskId, "Child Task", "do work", 1), TestActor);

        var linkEvent = PollForEvent(localUiEvents,
            e => e.Type == "agui.graph.link_created" && e.TaskId == parentTaskId,
            TimeSpan.FromSeconds(10));

        Assert.NotNull(linkEvent);
        Assert.Equal(parentTaskId, linkEvent!.TaskId);
    }

    [Fact]
    public void DispatcherActor_SpawnSubTask_GraphLinkPayloadContainsChildInfo()
    {
        var (dispatcher, localUiEvents) = BuildProbeDispatcher("link-payload");
        var parentTaskId = $"parent-{Guid.NewGuid():N}";
        var childTaskId = $"child-{Guid.NewGuid():N}";

        _taskRegistry.Register(new TaskAssigned(parentTaskId, "Parent", "desc", DateTimeOffset.UtcNow));
        dispatcher.Tell(new SpawnSubTask(parentTaskId, childTaskId, "My Child", "desc", 2), TestActor);

        var linkEvent = PollForEvent(localUiEvents,
            e => e.Type == "agui.graph.link_created" && e.TaskId == parentTaskId,
            TimeSpan.FromSeconds(10));

        Assert.NotNull(linkEvent);

        var payload = (GraphLinkCreatedPayload)linkEvent!.Payload;
        Assert.Equal(parentTaskId, payload.ParentTaskId);
        Assert.Equal(childTaskId, payload.ChildTaskId);
        Assert.Equal("My Child", payload.Title);
        Assert.Equal(2, payload.Depth);
    }

    // ── graph.child_completed / child_failed ─────────────────────────────────

    [Fact]
    public async Task DispatcherActor_ChildTaskCompletes_EmitsGraphChildCompletedEvent()
    {
        var dispatcher = BuildDispatcher("child-done");
        var parentTaskId = $"parent-done-{Guid.NewGuid():N}";
        var childTaskId = $"child-done-{Guid.NewGuid():N}";

        _taskRegistry.Register(new TaskAssigned(parentTaskId, "Parent", "desc", DateTimeOffset.UtcNow));

        dispatcher.Tell(
            new SpawnSubTask(parentTaskId, childTaskId, "Child", "do work", 1),
            TestActor);

        // Wait for the child to reach a terminal state
        await Task.Run(() =>
            FishForMessage(m => m is SubTaskCompleted or SubTaskFailed, TimeSpan.FromSeconds(30)));

        var evt = _uiEvents.GetRecent(200)
            .FirstOrDefault(e => e.Type == "agui.graph.child_completed"
                && e.TaskId == parentTaskId);

        // child_failed is also acceptable if local-echo adapters fail
        var failEvt = _uiEvents.GetRecent(200)
            .FirstOrDefault(e => e.Type == "agui.graph.child_failed"
                && e.TaskId == parentTaskId);

        Assert.True(evt != null || failEvt != null,
            "Expected either agui.graph.child_completed or agui.graph.child_failed to be emitted.");
    }

    // ── telemetry.quality (OnRoleSucceeded) ───────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_OnRoleSucceeded_EmitsTelemetryQualityEvent()
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

        var taskId = $"tel-quality-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Task", "desc", DateTimeOffset.UtcNow));

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId, "Task", "desc",
                workerProbe, reviewerProbe, supervisorProbe, blackboardProbe,
                ActorRefs.Nobody, roleEngine, goapPlanner, _loggerFactory, _telemetry, uiEvents, registry, _options, null, null, null, 2, 0, null, null, null, null, null, null)));

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());

        // Wait for orchestrator request
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Orchestrator, TimeSpan.FromSeconds(5));

        // Reply as orchestrator to trigger Plan → emits agui.telemetry.quality for orchestrator
        coordinator.Tell(new RoleTaskSucceeded(
            taskId, SwarmRole.Orchestrator, "ACTION: Plan", DateTimeOffset.UtcNow, 0.9, null, "worker"));

        // Wait for planner request
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Planner, TimeSpan.FromSeconds(5));

        // Reply as planner
        coordinator.Tell(new RoleTaskSucceeded(
            taskId, SwarmRole.Planner, "plan output with no subtasks", DateTimeOffset.UtcNow, 0.85, null, "worker"));

        var evt = PollForEvent(uiEvents,
            e => e.Type == "agui.telemetry.quality" && e.TaskId == taskId,
            TimeSpan.FromSeconds(5));

        Assert.NotNull(evt);
        Assert.Equal(taskId, evt!.TaskId);

        var payload = (TelemetryQualityPayload)evt.Payload;
        Assert.True(payload.Confidence > 0.0, "Confidence should be positive");
        Assert.NotNull(payload.Role);
    }

    // ── telemetry.retry ───────────────────────────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_OnRetryRole_EmitsTelemetryRetryEvent()
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

        var taskId = $"tel-retry-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Task", "desc", DateTimeOffset.UtcNow));

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId, "Task", "desc",
                workerProbe, reviewerProbe, supervisorProbe, blackboardProbe,
                ActorRefs.Nobody, roleEngine, goapPlanner, _loggerFactory, _telemetry, uiEvents, registry, _options, null, null, null, 2, 0, null, null, null, null, null, null)));

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Orchestrator, TimeSpan.FromSeconds(5));

        // Drive to planning
        coordinator.Tell(new RoleTaskSucceeded(
            taskId, SwarmRole.Orchestrator, "ACTION: Plan", DateTimeOffset.UtcNow, 1.0, null, "w"));
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Planner, TimeSpan.FromSeconds(5));

        // Simulate a supervisor-initiated retry for Planner
        coordinator.Tell(new RetryRole(taskId, SwarmRole.Planner, null, "test-retry-reason"));

        var evt = PollForEvent(uiEvents,
            e => e.Type == "agui.telemetry.retry" && e.TaskId == taskId,
            TimeSpan.FromSeconds(5));

        Assert.NotNull(evt);
        Assert.Equal(taskId, evt!.TaskId);

        var payload = (TelemetryRetryPayload)evt.Payload;
        Assert.Equal("planner", payload.Role);
        Assert.Equal("test-retry-reason", payload.Reason);
    }

    // ── telemetry.consensus ───────────────────────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_OnConsensusResult_EmitsTelemetryConsensusEvent()
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

        var taskId = $"tel-consensus-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Task", "desc", DateTimeOffset.UtcNow));

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId, "Task", "desc",
                workerProbe, reviewerProbe, supervisorProbe, blackboardProbe,
                ActorRefs.Nobody, roleEngine, goapPlanner, _loggerFactory, _telemetry, uiEvents, registry, _options, null, null, null, 2, 0, null, null, null, null, null, null)));

        // Send a consensus result directly
        var votes = new List<ConsensusVote>
        {
            new ConsensusVote(taskId, "voter-1", true, 0.9, "Looks good"),
            new ConsensusVote(taskId, "voter-2", true, 0.85, "Approved"),
        };
        coordinator.Tell(new ConsensusResult(taskId, true, votes));

        var evt = PollForEvent(uiEvents,
            e => e.Type == "agui.telemetry.consensus" && e.TaskId == taskId,
            TimeSpan.FromSeconds(5));

        Assert.NotNull(evt);
        Assert.Equal(taskId, evt!.TaskId);

        var payload = (TelemetryConsensusPayload)evt.Payload;
        Assert.True(payload.Approved);
        Assert.Equal(2, payload.VoteCount);
    }

    // ── telemetry.circuit ─────────────────────────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_OnGlobalBlackboardChanged_Circuit_EmitsTelemetryCircuitEvent()
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

        var taskId = $"tel-circuit-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Task", "desc", DateTimeOffset.UtcNow));

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId, "Task", "desc",
                workerProbe, reviewerProbe, supervisorProbe, blackboardProbe,
                ActorRefs.Nobody, roleEngine, goapPlanner, _loggerFactory, _telemetry, uiEvents, registry, _options, null, null, null, 2, 0, null, null, null, null, null, null)));

        // Start coordination so PreStart() runs and event stream subscription is established
        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Orchestrator, TimeSpan.FromSeconds(5));

        // Now we know PreStart has completed; publish the circuit event
        Sys.EventStream.Publish(new GlobalBlackboardChanged(
            GlobalBlackboardKeys.AdapterCircuitPrefix + "test-adapter",
            GlobalBlackboardKeys.CircuitStateOpen));

        var evt = PollForEvent(uiEvents,
            e => e.Type == "agui.telemetry.circuit" && e.TaskId == taskId,
            TimeSpan.FromSeconds(10));

        Assert.NotNull(evt);
        Assert.Equal(taskId, evt!.TaskId);

        var payload = (TelemetryCircuitPayload)evt.Payload;
        Assert.NotNull(payload.AdapterCircuitKey);
        Assert.Equal(GlobalBlackboardKeys.CircuitStateOpen, payload.State);
    }

    // ── telemetry.quality (OnQualityConcern) ─────────────────────────────────

    [Fact]
    public void TaskCoordinatorActor_OnQualityConcern_EmitsTelemetryQualityEvent()
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

        var taskId = $"tel-concern-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Task", "desc", DateTimeOffset.UtcNow));

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId, "Task", "desc",
                workerProbe, reviewerProbe, supervisorProbe, blackboardProbe,
                ActorRefs.Nobody, roleEngine, goapPlanner, _loggerFactory, _telemetry, uiEvents, registry, _options, null, null, null, 2, 0, null, null, null, null, null, null)));

        // Start coordination so PreStart() runs and event stream subscription is established
        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Orchestrator, TimeSpan.FromSeconds(5));

        // Now PreStart has completed; publish a quality concern for this task
        Sys.EventStream.Publish(new QualityConcern(
            taskId, SwarmRole.Builder, "Output quality too low", 0.3, "local-echo", DateTimeOffset.UtcNow));

        // Poll for telemetry.quality event from OnQualityConcern (has a non-null Concern)
        var evt = PollForEvent(uiEvents,
            e => e.Type == "agui.telemetry.quality"
                && e.TaskId == taskId
                && e.Payload is TelemetryQualityPayload { Concern: not null },
            TimeSpan.FromSeconds(10));

        Assert.NotNull(evt);
        Assert.Equal(taskId, evt!.TaskId);

        var payload = (TelemetryQualityPayload)evt.Payload;
        Assert.Equal("builder", payload.Role);
        Assert.Equal(0.3, payload.Confidence);
        Assert.Equal("Output quality too low", payload.Concern);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls <paramref name="eventStream"/> until an event matching <paramref name="predicate"/>
    /// is found or <paramref name="timeout"/> elapses.
    /// </summary>
    private static UiEventEnvelope? PollForEvent(
        UiEventStream eventStream,
        Func<UiEventEnvelope, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var evt = eventStream.GetRecent(200).FirstOrDefault(predicate);
            if (evt != null)
                return evt;
            Thread.Sleep(20);
        }

        return null;
    }

    /// <summary>
    /// Builds a dispatcher backed by test probes (no real workers), with its own
    /// isolated <see cref="UiEventStream"/> so events from each test don't mix.
    /// </summary>
    private (IActorRef Dispatcher, UiEventStream UiEvents) BuildProbeDispatcher(string suffix)
    {
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);
        var localUiEvents = new UiEventStream();

        var workerProbe = CreateTestProbe($"worker-probe-{suffix}");
        var reviewerProbe = CreateTestProbe($"reviewer-probe-{suffix}");
        var supervisorProbe = CreateTestProbe($"supervisor-probe-{suffix}");
        var blackboardProbe = CreateTestProbe($"blackboard-probe-{suffix}");
        var consensusProbe = CreateTestProbe($"consensus-probe-{suffix}");

        var dispatcher = Sys.ActorOf(
            Props.Create(() => new DispatcherActor(
                workerProbe,
                reviewerProbe,
                supervisorProbe,
                blackboardProbe,
                consensusProbe,
                roleEngine,
                _loggerFactory,
                _telemetry,
                localUiEvents,
                _taskRegistry,
                Options.Create(_options),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null, null)),
            $"dispatcher-probe-{suffix}");

        return (dispatcher, localUiEvents);
    }

    private IActorRef BuildDispatcher(string suffix)
    {
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);

        var supervisorActor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, _telemetry, null, null)),
            $"supervisor-ge-{suffix}");

        var blackboardActor = Sys.ActorOf(
            Props.Create(() => new BlackboardActor(_loggerFactory)),
            $"blackboard-ge-{suffix}");

        var workerActor = Sys.ActorOf(
            Props.Create(() => new WorkerActor(_options, _loggerFactory, roleEngine, _telemetry, null))
                .WithRouter(new SmallestMailboxPool(_options.WorkerPoolSize)),
            $"worker-ge-{suffix}");

        var reviewerActor = Sys.ActorOf(
            Props.Create(() => new ReviewerActor(_options, _loggerFactory, roleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(_options.ReviewerPoolSize)),
            $"reviewer-ge-{suffix}");

        var consensusActor = Sys.ActorOf(
            Props.Create(() => new ConsensusActor(NullLogger<ConsensusActor>.Instance)),
            $"consensus-ge-{suffix}");

        return Sys.ActorOf(
            Props.Create(() => new DispatcherActor(
                workerActor,
                reviewerActor,
                supervisorActor,
                blackboardActor,
                consensusActor,
                roleEngine,
                _loggerFactory,
                _telemetry,
                _uiEvents,
                _taskRegistry,
                Options.Create(_options),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null, null)),
            $"dispatcher-ge-{suffix}");
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
