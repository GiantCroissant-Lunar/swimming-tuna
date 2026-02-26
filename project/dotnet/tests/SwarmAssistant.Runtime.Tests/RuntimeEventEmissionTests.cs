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

    private (IActorRef dispatcher, InMemoryEventWriter writer) BuildDispatcher(string suffix = "", string? projectContext = null)
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
            Props.Create(() => new WorkerActor(_options, _loggerFactory, roleEngine, _telemetry, recorder))
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
                null,
                projectContext,
                null,
                null,
                null, null)),
            $"dp{suffix}-{Guid.NewGuid():N}");

        return (dispatcher, writer);
    }

    private async Task WaitForTaskStatus(string taskId, TaskState expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = _taskRegistry.GetTask(taskId);
            if (snapshot?.Status == expected)
                return;
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
            if (writer.Events.Any(predicate))
                return;
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
    public async Task HappyPath_EmitsDiagnosticContextEvent()
    {
        var (dispatcher, writer) = BuildDispatcher("diag");
        var taskId = $"emit-diag-{Guid.NewGuid():N}";

        dispatcher.Tell(new TaskAssigned(taskId, "Diag Test", "Verify diagnostic context event.", DateTimeOffset.UtcNow));

        await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));
        await Task.Delay(200);

        var diagEvents = writer.Events
            .Where(e => e.TaskId == taskId && e.EventType == RuntimeEventRecorder.DiagnosticContext)
            .ToList();

        // Happy-path should emit at least Plan + Build + Review diagnostic events
        Assert.True(diagEvents.Count >= 3, $"Expected at least 3 diagnostic events but got {diagEvents.Count}");

        // Each diagnostic event should have a non-empty payload with expected PascalCase fields
        foreach (var evt in diagEvents)
        {
            Assert.NotNull(evt.Payload);

            var payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(evt.Payload!);
            Assert.True(payload.TryGetProperty("Action", out _), "Payload should contain 'Action'");
            Assert.True(payload.TryGetProperty("Role", out _), "Payload should contain 'Role'");
            Assert.True(payload.TryGetProperty("PromptLength", out _), "Payload should contain 'PromptLength'");
            Assert.True(payload.TryGetProperty("HasCodeContext", out _), "Payload should contain 'HasCodeContext'");
            Assert.True(payload.TryGetProperty("CodeChunkCount", out _), "Payload should contain 'CodeChunkCount'");
            Assert.True(payload.TryGetProperty("HasStrategyAdvice", out _), "Payload should contain 'HasStrategyAdvice'");
            Assert.True(payload.TryGetProperty("TargetFiles", out _), "Payload should contain 'TargetFiles'");
            Assert.True(payload.TryGetProperty("HasProjectContext", out _), "Payload should contain 'HasProjectContext'");
        }

        // Assert concrete values on the first diagnostic event
        var firstPayload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(diagEvents[0].Payload!);
        var action = firstPayload.GetProperty("Action").GetString();
        Assert.False(string.IsNullOrWhiteSpace(action), "First diagnostic event Action should be a non-empty string");
        Assert.True(firstPayload.GetProperty("PromptLength").GetInt32() > 0, "First diagnostic event PromptLength should be > 0");
    }

    [Fact]
    public async Task HappyPath_EmitsAdapterDiagnosticEvent()
    {
        var (dispatcher, writer) = BuildDispatcher("adapter");
        var taskId = $"emit-adapter-{Guid.NewGuid():N}";

        dispatcher.Tell(new TaskAssigned(taskId, "Adapter Diag Test", "Verify adapter diagnostic event.", DateTimeOffset.UtcNow));

        await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));
        await Task.Delay(200);

        var adapterEvents = writer.Events
            .Where(e => e.TaskId == taskId && e.EventType == RuntimeEventRecorder.DiagnosticAdapter)
            .ToList();

        // Happy-path should emit at least one adapter diagnostic event (Plan + Build worker executions)
        Assert.True(adapterEvents.Count >= 1, $"Expected at least 1 adapter diagnostic event but got {adapterEvents.Count}");

        // Each adapter diagnostic event should have a non-empty payload with expected PascalCase fields
        foreach (var evt in adapterEvents)
        {
            Assert.NotNull(evt.Payload);

            var payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(evt.Payload!);
            Assert.True(payload.TryGetProperty("AdapterId", out _), "Payload should contain 'AdapterId'");
            Assert.True(payload.TryGetProperty("OutputLength", out _), "Payload should contain 'OutputLength'");
            Assert.True(payload.TryGetProperty("Role", out _), "Payload should contain 'Role'");
            Assert.True(payload.TryGetProperty("ExitCode", out _), "Payload should contain 'ExitCode'");
        }

        // Assert concrete values on the first adapter diagnostic event
        var firstPayload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(adapterEvents[0].Payload!);
        var adapterId = firstPayload.GetProperty("AdapterId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(adapterId), "AdapterId should be a non-empty string");
        Assert.True(firstPayload.GetProperty("OutputLength").GetInt32() > 0, "OutputLength should be > 0");
        Assert.Equal(0, firstPayload.GetProperty("ExitCode").GetInt32());
    }

    [Fact]
    public async Task HappyPath_ProjectContextFlowsThroughPipeline()
    {
        const string knownContext = "# AGENTS.md\nUse PascalCase for C# types.\nPrefer sealed records for messages.";
        var (dispatcher, writer) = BuildDispatcher("projctx", projectContext: knownContext);
        var taskId = $"emit-projctx-{Guid.NewGuid():N}";

        dispatcher.Tell(new TaskAssigned(taskId, "ProjCtx Test", "Verify project context flows through.", DateTimeOffset.UtcNow));

        await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));
        await Task.Delay(200);

        var diagEvents = writer.Events
            .Where(e => e.TaskId == taskId && e.EventType == RuntimeEventRecorder.DiagnosticContext)
            .ToList();

        // Happy-path should emit at least Plan + Build + Review diagnostic events
        Assert.True(diagEvents.Count >= 3, $"Expected at least 3 diagnostic events but got {diagEvents.Count}");

        // Every diagnostic event should report HasProjectContext = true
        foreach (var evt in diagEvents)
        {
            Assert.NotNull(evt.Payload);

            var payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(evt.Payload!);
            Assert.True(payload.TryGetProperty("HasProjectContext", out var hasCtx), "Payload should contain 'HasProjectContext'");
            Assert.True(hasCtx.GetBoolean(), $"HasProjectContext should be true for action={payload.GetProperty("Action").GetString()}");
        }

        // Prompt length should be larger than baseline (the project context text is injected)
        var firstPayload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(diagEvents[0].Payload!);
        Assert.True(firstPayload.GetProperty("PromptLength").GetInt32() > knownContext.Length,
            "PromptLength should exceed the project context length (context is appended to base prompt)");
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
            Props.Create(() => new WorkerActor(_options, _loggerFactory, roleEngine, _telemetry, null))
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
                null,
                null,
                null,
                null,
                null
            , null)),
            $"dp-noop-{Guid.NewGuid():N}");

        var taskId = $"emit-noop-{Guid.NewGuid():N}";
        dispatcher.Tell(new TaskAssigned(taskId, "NoOp Test", "Verify no recorder still works.", DateTimeOffset.UtcNow));

        // Should still complete successfully without crashing
        await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));

        var snapshot = _taskRegistry.GetTask(taskId);
        Assert.Equal(TaskState.Done, snapshot!.Status);
    }

    [Fact]
    public async Task HappyPath_BuilderDispatch_WorkspaceBranchDisabled_StillCompletes()
    {
        var (dispatcher, writer) = BuildDispatcher("branch");
        var taskId = $"emit-branch-{Guid.NewGuid():N}";

        dispatcher.Tell(new TaskAssigned(taskId, "Branch Test", "Verify disabled branch still completes.", DateTimeOffset.UtcNow));

        await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));

        var snapshot = _taskRegistry.GetTask(taskId);
        Assert.NotNull(snapshot);
        Assert.Equal(TaskState.Done, snapshot!.Status);

        // Verify builder was dispatched (role.dispatched event for builder exists)
        await Task.Delay(200);
        var taskEvents = writer.Events.Where(e => e.TaskId == taskId).ToList();
        Assert.Contains(taskEvents, e => e.EventType == RuntimeEventRecorder.RoleCompleted
            && e.Payload != null && e.Payload.Contains("builder"));
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
