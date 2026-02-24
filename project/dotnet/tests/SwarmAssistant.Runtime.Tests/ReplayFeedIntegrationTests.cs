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
/// CI-safe integration tests that validate replay feeds after task execution.
/// Uses an in-memory event writer so no external ArcadeDB instance is required.
/// Acceptance criteria: replay feed is non-empty for both task and run after a
/// completed/terminal task, and contains minimal expected event types.
/// </summary>
public sealed class ReplayFeedIntegrationTests : TestKit
{
    private readonly RuntimeOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RuntimeTelemetry _telemetry;
    private readonly UiEventStream _uiEvents;
    private readonly TaskRegistry _taskRegistry;

    public ReplayFeedIntegrationTests()
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
            $"rf-bb{suffix}-{Guid.NewGuid():N}");

        var supervisor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, _telemetry, null, blackboard)),
            $"rf-sv{suffix}-{Guid.NewGuid():N}");

        var worker = Sys.ActorOf(
            Props.Create(() => new WorkerActor(_options, _loggerFactory, roleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(_options.WorkerPoolSize)),
            $"rf-wk{suffix}-{Guid.NewGuid():N}");

        var reviewer = Sys.ActorOf(
            Props.Create(() => new ReviewerActor(_options, _loggerFactory, roleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(_options.ReviewerPoolSize)),
            $"rf-rv{suffix}-{Guid.NewGuid():N}");

        var consensus = Sys.ActorOf(
            Props.Create(() => new ConsensusActor(_loggerFactory.CreateLogger<ConsensusActor>())),
            $"rf-cs{suffix}-{Guid.NewGuid():N}");

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
            $"rf-dp{suffix}-{Guid.NewGuid():N}");

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
            $"Task {taskId} did not reach {expected} within {timeout.TotalSeconds}s. " +
            $"Current: {final?.Status.ToString() ?? "not registered"}");
    }

    private static async Task WaitForWriterEventsAsync(
        InMemoryEventWriter writer,
        Func<IReadOnlyList<TaskExecutionEvent>, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate(writer.Events)) return;
            await Task.Delay(50);
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReplayFeed_AfterTaskCompletion_TaskFeedIsNonEmpty()
    {
        var (dispatcher, writer) = BuildDispatcher("task-feed");
        var taskId = $"rf-task-{Guid.NewGuid():N}";
        var runId = $"run-rf-{Guid.NewGuid():N}";

        _taskRegistry.Register(
            new TaskAssigned(taskId, "Replay Feed Test", "Verify task feed is non-empty.", DateTimeOffset.UtcNow),
            runId);

        dispatcher.Tell(new TaskAssigned(taskId, "Replay Feed Test", "Verify task feed is non-empty.", DateTimeOffset.UtcNow));

        await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));
        await WaitForWriterEventsAsync(
            writer,
            events => events.Any(e => e.TaskId == taskId && e.EventType == RuntimeEventRecorder.TaskDone),
            TimeSpan.FromSeconds(5));

        var taskEvents = writer.Events.Where(e => e.TaskId == taskId).ToList();

        Assert.True(
            taskEvents.Count > 0,
            $"Expected non-empty replay feed for taskId={taskId} runId={runId}, " +
            $"but got 0 events. Total recorded events: {writer.Events.Count}");
    }

    [Fact]
    public async Task ReplayFeed_AfterTaskCompletion_RunFeedIsNonEmpty()
    {
        var (dispatcher, writer) = BuildDispatcher("run-feed");
        var taskId = $"rf-run-{Guid.NewGuid():N}";
        var runId = $"run-rf-explicit-{Guid.NewGuid():N}";

        _taskRegistry.Register(
            new TaskAssigned(taskId, "Run Feed Test", "Verify run feed is non-empty.", DateTimeOffset.UtcNow),
            runId);

        dispatcher.Tell(new TaskAssigned(taskId, "Run Feed Test", "Verify run feed is non-empty.", DateTimeOffset.UtcNow));

        await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));
        await WaitForWriterEventsAsync(
            writer,
            events => events.Any(e => e.RunId == runId),
            TimeSpan.FromSeconds(5));

        var runEvents = writer.Events.Where(e => e.RunId == runId).ToList();

        Assert.True(
            runEvents.Count > 0,
            $"Expected non-empty run-level replay feed for runId={runId} taskId={taskId}, " +
            $"but got 0 events. Recorded runIds: {string.Join(", ", writer.Events.Select(e => e.RunId).Distinct())}");
    }

    [Fact]
    public async Task ReplayFeed_AfterTaskCompletion_ContainsTerminalEvent()
    {
        var (dispatcher, writer) = BuildDispatcher("terminal");
        var taskId = $"rf-terminal-{Guid.NewGuid():N}";

        dispatcher.Tell(new TaskAssigned(taskId, "Terminal Event Test", "Verify terminal event is recorded.", DateTimeOffset.UtcNow));

        await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));
        await WaitForWriterEventsAsync(
            writer,
            events => events.Any(e => e.TaskId == taskId &&
                (e.EventType == RuntimeEventRecorder.TaskDone || e.EventType == RuntimeEventRecorder.TaskFailed)),
            TimeSpan.FromSeconds(5));

        var taskEvents = writer.Events.Where(e => e.TaskId == taskId).ToList();
        var hasTerminalEvent = taskEvents.Any(e =>
            e.EventType == RuntimeEventRecorder.TaskDone ||
            e.EventType == RuntimeEventRecorder.TaskFailed);

        Assert.True(
            hasTerminalEvent,
            $"Expected a terminal event ({RuntimeEventRecorder.TaskDone} or {RuntimeEventRecorder.TaskFailed}) " +
            $"in replay feed for taskId={taskId}. " +
            $"Recorded event types: {string.Join(", ", taskEvents.Select(e => e.EventType))}");
    }

    [Fact]
    public async Task ReplayFeed_AfterTaskCompletion_ContainsMinimalExpectedEventTypes()
    {
        var (dispatcher, writer) = BuildDispatcher("event-types");
        var taskId = $"rf-types-{Guid.NewGuid():N}";
        var runId = $"run-rf-types-{Guid.NewGuid():N}";

        _taskRegistry.Register(
            new TaskAssigned(taskId, "Event Types Test", "Verify minimal event types are present.", DateTimeOffset.UtcNow),
            runId);

        dispatcher.Tell(new TaskAssigned(taskId, "Event Types Test", "Verify minimal event types are present.", DateTimeOffset.UtcNow));

        await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));
        await WaitForWriterEventsAsync(
            writer,
            events => events.Count(e => e.TaskId == taskId) >= 3,
            TimeSpan.FromSeconds(5));

        var taskEvents = writer.Events.Where(e => e.TaskId == taskId).ToList();

        Assert.True(
            taskEvents.Count >= 3,
            $"Expected at least 3 events (task.submitted, role.completed, task.done) for taskId={taskId}, " +
            $"got {taskEvents.Count}. Types: {string.Join(", ", taskEvents.Select(e => e.EventType))}");

        Assert.Contains(taskEvents, e => e.EventType == RuntimeEventRecorder.TaskSubmitted);
        Assert.Contains(taskEvents, e => e.EventType == RuntimeEventRecorder.TaskDone);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loggerFactory.Dispose();
        }

        base.Dispose(disposing);
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
}
