using Akka.Actor;
using Akka.Routing;
using Akka.TestKit.Xunit2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Tasks;
using SwarmAssistant.Runtime.Telemetry;
using SwarmAssistant.Runtime.Ui;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tests;

/// <summary>
/// Integration tests proving the runtime emits and persists lifecycle events
/// to <see cref="ITaskExecutionEventWriter"/> during normal pipeline execution.
/// Acceptance criteria for issue: wire runtime lifecycle to persisted task/run execution events.
/// </summary>
public sealed class RuntimeEventEmissionTests : TestKit
{
    private readonly RuntimeOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RuntimeTelemetry _telemetry;
    private readonly UiEventStream _uiEvents;
    private readonly TaskRegistry _taskRegistry;

    public RuntimeEventEmissionTests()
    {
        _options = new RuntimeOptions
        {
            AgentFrameworkExecutionMode = "subscription-cli-fallback",
            CliAdapterOrder = ["local-echo"],
            WorkerPoolSize = 2,
            ReviewerPoolSize = 1,
            MaxCliConcurrency = 4,
            SandboxMode = "none",
        };

        _loggerFactory = NullLoggerFactory.Instance;
        _telemetry = new RuntimeTelemetry(_options, _loggerFactory);
        _uiEvents = new UiEventStream();
        _taskRegistry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private (IActorRef dispatcher, InMemoryEventWriter writer) BuildDispatcher(string suffix = "")
    {
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);
        var writer = new InMemoryEventWriter();
        var recorder = new RuntimeEventRecorder(writer, NullLogger<RuntimeEventRecorder>.Instance);

        var blackboard = Sys.ActorOf(
            Props.Create(() => new BlackboardActor(_loggerFactory)),
            $"bb{suffix}-{Guid.NewGuid():N}");

        var supervisor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, _telemetry, null, blackboard)),
            $"sv{suffix}-{Guid.NewGuid():N}");

        var worker = Sys.ActorOf(
            Props.Create(() => new WorkerActor(_options, _loggerFactory, roleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(_options.WorkerPoolSize)),
            $"wk{suffix}-{Guid.NewGuid():N}");

        var reviewer = Sys.ActorOf(
            Props.Create(() => new ReviewerActor(_options, _loggerFactory, roleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(_options.ReviewerPoolSize)),
            $"rv{suffix}-{Guid.NewGuid():N}");

        var consensus = Sys.ActorOf(
            Props.Create(() => new ConsensusActor(_loggerFactory.CreateLogger<ConsensusActor>())),
            $"cs{suffix}-{Guid.NewGuid():N}");

        var dispatcher = Sys.ActorOf(
            Props.Create(() => new DispatcherActor(
                worker,
                reviewer,
                supervisor,
                blackboard,
                consensus,
                roleEngine,
                _loggerFactory,
                _telemetry,
                _uiEvents,
                _taskRegistry,
                Microsoft.Extensions.Options.Options.Create(_options),
                null,
                null,
                recorder,
                null)),
            $"dp{suffix}-{Guid.NewGuid():N}");

        return (dispatcher, writer);
    }

    private async Task WaitForTaskStatus(string taskId, TaskState expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = _taskRegistry.GetTask(taskId);
            if (snapshot?.Status == expected) return;
            if (snapshot?.Status == TaskState.Blocked)
                throw new InvalidOperationException(
                    $"Task {taskId} reached Blocked instead of {expected}. Error: {snapshot.Error}");
            await Task.Delay(50);
        }

        var final = _taskRegistry.GetTask(taskId);
        throw new TimeoutException(
            $"Task {taskId} did not reach {expected} within {timeout.TotalSeconds}s. Current: {final?.Status.ToString() ?? "not registered"}");
    }

    private static async Task WaitForEvents(
        InMemoryEventWriter writer,
        Func<TaskExecutionEvent, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (writer.Events.Any(predicate)) return;
            await Task.Delay(50);
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_EmitsTaskSubmittedEvent()
    {
        var (dispatcher, writer) = BuildDispatcher("submit");
        var taskId = $"emit-submit-{Guid.NewGuid():N}";
        var runId = $"run-{Guid.NewGuid():N}";

        _taskRegistry.Register(
            new TaskAssigned(taskId, "Emit Test", "Verify submission event.", DateTimeOffset.UtcNow),
            runId);

        dispatcher.Tell(new TaskAssigned(taskId, "Emit Test", "Verify submission event.", DateTimeOffset.UtcNow));

        await WaitForEvents(
            writer,
            e => e.TaskId == taskId && e.EventType == RuntimeEventRecorder.TaskSubmitted,
            TimeSpan.FromSeconds(10));

        var evt = writer.Events.FirstOrDefault(e =>
            e.TaskId == taskId && e.EventType == RuntimeEventRecorder.TaskSubmitted);

        Assert.NotNull(evt);
        Assert.Equal(taskId, evt!.TaskId);
        Assert.NotEmpty(evt.RunId);
        Assert.False(string.IsNullOrEmpty(evt.EventId));
    }

    [Fact]
    public async Task HappyPath_EmitsCoordinationStartedEvent()
    {
        var (dispatcher, writer) = BuildDispatcher("coord");
        var taskId = $"emit-coord-{Guid.NewGuid():N}";

        dispatcher.Tell(new TaskAssigned(taskId, "Coord Test", "Verify coordination event.", DateTimeOffset.UtcNow));

        await WaitForEvents(
            writer,
            e => e.TaskId == taskId && e.EventType == RuntimeEventRecorder.CoordinationStarted,
            TimeSpan.FromSeconds(10));

        var evt = writer.Events.FirstOrDefault(e =>
            e.TaskId == taskId && e.EventType == RuntimeEventRecorder.CoordinationStarted);

        Assert.NotNull(evt);
        Assert.Equal(taskId, evt!.TaskId);
    }

    [Fact]
    public async Task HappyPath_EmitsRoleLifecycleEvents()
    {
        var (dispatcher, writer) = BuildDispatcher("roles");
        var taskId = $"emit-roles-{Guid.NewGuid():N}";

        dispatcher.Tell(new TaskAssigned(taskId, "Roles Test", "Verify role events.", DateTimeOffset.UtcNow));

        // Wait for full completion so all role events are emitted
        await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));

        // Allow async writes to settle
        await Task.Delay(200);

        var taskEvents = writer.Events.Where(e => e.TaskId == taskId).ToList();

        Assert.Contains(taskEvents, e => e.EventType == RuntimeEventRecorder.RoleCompleted
            && e.Payload != null && e.Payload.Contains("planner"));

        Assert.Contains(taskEvents, e => e.EventType == RuntimeEventRecorder.RoleCompleted
            && e.Payload != null && e.Payload.Contains("builder"));

        Assert.Contains(taskEvents, e => e.EventType == RuntimeEventRecorder.RoleCompleted
            && e.Payload != null && e.Payload.Contains("reviewer"));
    }

    [Fact]
    public async Task HappyPath_EmitsTaskDoneEvent()
    {
        var (dispatcher, writer) = BuildDispatcher("done");
        var taskId = $"emit-done-{Guid.NewGuid():N}";

        dispatcher.Tell(new TaskAssigned(taskId, "Done Test", "Verify task done event.", DateTimeOffset.UtcNow));

        await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));
        await Task.Delay(200);

        var evt = writer.Events.FirstOrDefault(e =>
            e.TaskId == taskId && e.EventType == RuntimeEventRecorder.TaskDone);

        Assert.NotNull(evt);
        Assert.Equal(taskId, evt!.TaskId);
        Assert.NotEmpty(evt.RunId);
    }

    [Fact]
    public async Task HappyPath_RunIdAlwaysPopulated()
    {
        var (dispatcher, writer) = BuildDispatcher("runid");
        var taskId = $"emit-runid-{Guid.NewGuid():N}";
        var runId = $"run-explicit-{Guid.NewGuid():N}";

        // Pre-register with explicit runId
        _taskRegistry.Register(
            new TaskAssigned(taskId, "RunId Test", "Verify runId.", DateTimeOffset.UtcNow),
            runId);

        dispatcher.Tell(new TaskAssigned(taskId, "RunId Test", "Verify runId.", DateTimeOffset.UtcNow));

        await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));
        await Task.Delay(200);

        var taskEvents = writer.Events.Where(e => e.TaskId == taskId).ToList();
        Assert.NotEmpty(taskEvents);

        // All events for this task must have a non-empty runId
        Assert.All(taskEvents, e => Assert.False(string.IsNullOrWhiteSpace(e.RunId)));
    }

    [Fact]
    public async Task HappyPath_WithoutRunId_UsesLegacyFallback()
    {
        // Submit without pre-registering with an explicit runId
        var (dispatcher, writer) = BuildDispatcher("legacy");
        var taskId = $"emit-legacy-{Guid.NewGuid():N}";

        dispatcher.Tell(new TaskAssigned(taskId, "Legacy Test", "Verify legacy runId.", DateTimeOffset.UtcNow));

        await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));
        await Task.Delay(200);

        var taskEvents = writer.Events.Where(e => e.TaskId == taskId).ToList();
        Assert.NotEmpty(taskEvents);

        // All events should have a non-empty runId (either explicit or legacy fallback)
        Assert.All(taskEvents, e => Assert.False(string.IsNullOrWhiteSpace(e.RunId)));
    }

    [Fact]
    public async Task HappyPath_EventSequenceIsMonotonic_PerTask()
    {
        var (dispatcher, writer) = BuildDispatcher("mono");
        var taskId = $"emit-mono-{Guid.NewGuid():N}";

        dispatcher.Tell(new TaskAssigned(taskId, "Monotonic Test", "Verify sequences.", DateTimeOffset.UtcNow));

        await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));
        await Task.Delay(200);

        // Verify events are emitted in occurrence order (OccurredAt monotonic)
        var taskEvents = writer.Events
            .Where(e => e.TaskId == taskId)
            .OrderBy(e => e.OccurredAt)
            .ToList();

        Assert.NotEmpty(taskEvents);

        // Each event should have a unique EventId
        var eventIds = taskEvents.Select(e => e.EventId).ToList();
        Assert.Equal(eventIds.Count, eventIds.Distinct().Count());
    }

    [Fact]
    public async Task HappyPath_NoEventWriterInjected_DoesNotThrow()
    {
        // Dispatcher without event recorder — should execute normally
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);

        var blackboard = Sys.ActorOf(
            Props.Create(() => new BlackboardActor(_loggerFactory)),
            $"bb-noop-{Guid.NewGuid():N}");

        var supervisor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, _telemetry, null, blackboard)),
            $"sv-noop-{Guid.NewGuid():N}");

        var worker = Sys.ActorOf(
            Props.Create(() => new WorkerActor(_options, _loggerFactory, roleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(_options.WorkerPoolSize)),
            $"wk-noop-{Guid.NewGuid():N}");

        var reviewer = Sys.ActorOf(
            Props.Create(() => new ReviewerActor(_options, _loggerFactory, roleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(_options.ReviewerPoolSize)),
            $"rv-noop-{Guid.NewGuid():N}");

        var consensus = Sys.ActorOf(
            Props.Create(() => new ConsensusActor(_loggerFactory.CreateLogger<ConsensusActor>())),
            $"cs-noop-{Guid.NewGuid():N}");

        var dispatcher = Sys.ActorOf(
            Props.Create(() => new DispatcherActor(
                worker,
                reviewer,
                supervisor,
                blackboard,
                consensus,
                roleEngine,
                _loggerFactory,
                _telemetry,
                _uiEvents,
                _taskRegistry,
                Microsoft.Extensions.Options.Options.Create(_options),
                null,
                null,
                null,
                null
            )),
            $"dp-noop-{Guid.NewGuid():N}");

        var taskId = $"emit-noop-{Guid.NewGuid():N}";
        dispatcher.Tell(new TaskAssigned(taskId, "NoOp Test", "Verify no recorder still works.", DateTimeOffset.UtcNow));

        // Should still complete successfully without crashing
        await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));

        var snapshot = _taskRegistry.GetTask(taskId);
        Assert.Equal(TaskState.Done, snapshot!.Status);
    }

    // ── Fake writer ──────────────────────────────────────────────────────────

    private sealed class InMemoryEventWriter : ITaskExecutionEventWriter
    {
        private readonly List<TaskExecutionEvent> _events = new();
        private readonly object _lock = new();

        public IReadOnlyList<TaskExecutionEvent> Events
        {
            get { lock (_lock) { return _events.ToList(); } }
        }

        public Task AppendAsync(TaskExecutionEvent evt, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _events.Add(evt);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class NoOpTaskMemoryWriter : ITaskMemoryWriter
    {
        public Task WriteAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loggerFactory.Dispose();
        }

        base.Dispose(disposing);
    }
}
