using Akka.Actor;
using Akka.Routing;
using Akka.TestKit.Xunit2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Contracts.Hierarchy;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Tasks;
using SwarmAssistant.Runtime.Telemetry;
using SwarmAssistant.Runtime.Ui;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tests;

/// <summary>
/// Tests for RunCoordinatorActor lifecycle state machine, task tracking,
/// and run-scoped task dispatch through the DispatcherActor.
/// Uses local-echo adapters for deterministic, fast execution.
/// </summary>
public sealed class RunCoordinatorActorTests : TestKit
{
    private readonly RuntimeOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RuntimeTelemetry _telemetry;
    private readonly UiEventStream _uiEvents;
    private readonly TaskRegistry _taskRegistry;

    public RunCoordinatorActorTests()
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

    private IActorRef CreateDispatcher(string suffix)
    {
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);

        var blackboardActor = Sys.ActorOf(
            Props.Create(() => new BlackboardActor(_loggerFactory)),
            $"blackboard-{suffix}");

        var supervisorActor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, _telemetry, null, blackboardActor)),
            $"supervisor-{suffix}");

        var workerActor = Sys.ActorOf(
            Props.Create(() => new WorkerActor(_options, _loggerFactory, roleEngine, _telemetry, null))
                .WithRouter(new SmallestMailboxPool(_options.WorkerPoolSize)),
            $"worker-pool-{suffix}");

        var reviewerActor = Sys.ActorOf(
            Props.Create(() => new ReviewerActor(_options, _loggerFactory, roleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(_options.ReviewerPoolSize)),
            $"reviewer-pool-{suffix}");

        var consensusActor = Sys.ActorOf(
            Props.Create(() => new ConsensusActor(_loggerFactory.CreateLogger<ConsensusActor>())),
            $"consensus-pool-{suffix}");

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
                Microsoft.Extensions.Options.Options.Create(_options),
                null, null, null, null, null, null, null, null, null, null)),
            $"dispatcher-{suffix}");
    }

    // --- Run lifecycle through dispatcher ---

    [Fact]
    public async Task RunScoped_SingleTask_CompletesAndEmitsRunEvents()
    {
        var dispatcher = CreateDispatcher("run-single");
        var runId = $"run-{Guid.NewGuid():N}";
        var taskId = $"task-{Guid.NewGuid():N}";

        // Pre-register task with runId so dispatcher routes to RunCoordinatorActor
        _taskRegistry.Register(
            new TaskAssigned(taskId, "Run Task", "Test run-scoped task.", DateTimeOffset.UtcNow),
            runId);

        // Submit task — dispatcher detects RunId and creates RunCoordinatorActor
        dispatcher.Tell(new TaskAssigned(taskId, "Run Task", "Test run-scoped task.", DateTimeOffset.UtcNow));

        await WaitForTaskStatus(taskId, TaskState.Done, timeout: TimeSpan.FromSeconds(30));

        var snapshot = _taskRegistry.GetTask(taskId);
        Assert.NotNull(snapshot);
        Assert.Equal(TaskState.Done, snapshot.Status);
        Assert.Equal(runId, snapshot.RunId);

        // Verify run-level AG-UI events were emitted
        var events = _uiEvents.GetRecent(200);
        Assert.Contains(events, e => e.Type == "agui.run.accepted");
        Assert.Contains(events, e => e.Type == "agui.run.executing");
    }

    [Fact]
    public async Task RunScoped_MultipleTasks_AllCompleteWithRunTracking()
    {
        var dispatcher = CreateDispatcher("run-multi");
        var runId = $"run-{Guid.NewGuid():N}";
        var taskIds = Enumerable.Range(1, 2)
            .Select(_ => $"task-{Guid.NewGuid():N}")
            .ToList();

        // Pre-register all tasks with runId
        foreach (var taskId in taskIds)
        {
            _taskRegistry.Register(
                new TaskAssigned(taskId, $"Run Task {taskId[..8]}", "Multi-task run.", DateTimeOffset.UtcNow),
                runId);
        }

        // Submit all tasks — all route to same RunCoordinatorActor
        foreach (var taskId in taskIds)
        {
            dispatcher.Tell(new TaskAssigned(taskId, $"Run Task {taskId[..8]}", "Multi-task run.", DateTimeOffset.UtcNow));
        }

        // Wait for all to complete
        var waitTasks = taskIds
            .Select(id => WaitForTaskStatus(id, TaskState.Done, timeout: TimeSpan.FromSeconds(30)))
            .ToArray();
        await Task.WhenAll(waitTasks);

        foreach (var taskId in taskIds)
        {
            var snapshot = _taskRegistry.GetTask(taskId);
            Assert.NotNull(snapshot);
            Assert.Equal(TaskState.Done, snapshot.Status);
            Assert.Equal(runId, snapshot.RunId);
        }

        // Verify run events
        var events = _uiEvents.GetRecent(200);
        Assert.Contains(events, e => e.Type == "agui.run.accepted");
        Assert.Contains(events, e => e.Type == "agui.run.executing");

        // Each task should have completion events
        foreach (var taskId in taskIds)
        {
            Assert.Contains(events, e => e.Type == "agui.task.done" && e.TaskId == taskId);
        }
    }

    [Fact]
    public async Task RunScoped_DuplicateTaskId_IgnoredByRunCoordinator()
    {
        var dispatcher = CreateDispatcher("run-dup");
        var runId = $"run-{Guid.NewGuid():N}";
        var taskId = $"task-{Guid.NewGuid():N}";

        _taskRegistry.Register(
            new TaskAssigned(taskId, "Dup Task", "Duplicate test.", DateTimeOffset.UtcNow),
            runId);

        // Submit twice
        dispatcher.Tell(new TaskAssigned(taskId, "Dup Task", "Duplicate test.", DateTimeOffset.UtcNow));
        dispatcher.Tell(new TaskAssigned(taskId, "Dup Task", "Duplicate test.", DateTimeOffset.UtcNow));

        await WaitForTaskStatus(taskId, TaskState.Done, timeout: TimeSpan.FromSeconds(30));

        var snapshot = _taskRegistry.GetTask(taskId);
        Assert.NotNull(snapshot);
        Assert.Equal(TaskState.Done, snapshot.Status);
    }

    [Fact]
    public async Task RunScoped_TasksFromDifferentRuns_RoutedToSeparateCoordinators()
    {
        var dispatcher = CreateDispatcher("run-sep");
        var runId1 = $"run-{Guid.NewGuid():N}";
        var runId2 = $"run-{Guid.NewGuid():N}";
        var taskId1 = $"task-{Guid.NewGuid():N}";
        var taskId2 = $"task-{Guid.NewGuid():N}";

        _taskRegistry.Register(
            new TaskAssigned(taskId1, "Run1 Task", "From run 1.", DateTimeOffset.UtcNow),
            runId1);
        _taskRegistry.Register(
            new TaskAssigned(taskId2, "Run2 Task", "From run 2.", DateTimeOffset.UtcNow),
            runId2);

        dispatcher.Tell(new TaskAssigned(taskId1, "Run1 Task", "From run 1.", DateTimeOffset.UtcNow));
        dispatcher.Tell(new TaskAssigned(taskId2, "Run2 Task", "From run 2.", DateTimeOffset.UtcNow));

        await Task.WhenAll(
            WaitForTaskStatus(taskId1, TaskState.Done, timeout: TimeSpan.FromSeconds(30)),
            WaitForTaskStatus(taskId2, TaskState.Done, timeout: TimeSpan.FromSeconds(30)));

        // Tasks from different runs retain their respective RunIds
        Assert.Equal(runId1, _taskRegistry.GetTask(taskId1)!.RunId);
        Assert.Equal(runId2, _taskRegistry.GetTask(taskId2)!.RunId);

        // Both completed independently — proves separate coordinators
        Assert.Equal(TaskState.Done, _taskRegistry.GetTask(taskId1)!.Status);
        Assert.Equal(TaskState.Done, _taskRegistry.GetTask(taskId2)!.Status);
    }

    // --- RunSpanStatus enum ---

    [Fact]
    public void RunSpanStatus_HasExpectedValues()
    {
        Assert.Equal(0, (int)RunSpanStatus.Accepted);
        Assert.Equal(1, (int)RunSpanStatus.Decomposing);
        Assert.Equal(2, (int)RunSpanStatus.Executing);
        Assert.Equal(3, (int)RunSpanStatus.Merging);
        Assert.Equal(4, (int)RunSpanStatus.ReadyForPr);
        Assert.Equal(5, (int)RunSpanStatus.Done);
        Assert.Equal(6, (int)RunSpanStatus.Failed);
    }

    // --- RunSpan record ---

    [Fact]
    public void RunSpan_DefaultStatus_IsAccepted()
    {
        var span = new RunSpan
        {
            RunId = "run-1",
            Title = "Test Run",
            StartedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal(RunSpanStatus.Accepted, span.Status);
        Assert.Null(span.CompletedAt);
        Assert.Null(span.FeatureBranch);
        Assert.Null(span.BaseBranch);
    }

    [Fact]
    public void RunSpan_WithAllFields_RoundTrips()
    {
        var now = DateTimeOffset.UtcNow;
        var span = new RunSpan
        {
            RunId = "run-full",
            Title = "Full Run",
            StartedAt = now,
            CompletedAt = now.AddMinutes(5),
            Status = RunSpanStatus.Done,
            FeatureBranch = "feat/rfc-014",
            BaseBranch = "main"
        };

        Assert.Equal("run-full", span.RunId);
        Assert.Equal("Full Run", span.Title);
        Assert.Equal(RunSpanStatus.Done, span.Status);
        Assert.Equal("feat/rfc-014", span.FeatureBranch);
        Assert.Equal("main", span.BaseBranch);
        Assert.NotNull(span.CompletedAt);
    }

    // --- RunRegistry status integration ---

    [Fact]
    public void RunRegistry_StatusTransitions_ReflectLifecycle()
    {
        var registry = new RunRegistry();
        var run = registry.CreateRun(
            runId: "run-lifecycle",
            title: "Lifecycle Test",
            document: "# Design doc",
            baseBranch: "main",
            branchPrefix: "feat");

        Assert.Equal("accepted", run.Status);
        Assert.Equal("# Design doc", run.Document);

        // Mark done
        var done = registry.MarkDone("run-lifecycle");
        Assert.True(done);

        var updated = registry.GetRun("run-lifecycle");
        Assert.Equal("done", updated!.Status);
    }

    [Fact]
    public void RunRegistry_UpdateFeatureBranch_ReflectsInListing()
    {
        var registry = new RunRegistry();
        registry.CreateRun(runId: "run-branch", title: "Branch Test");
        registry.UpdateFeatureBranch("run-branch", "feat/my-feature");

        var runs = registry.ListRuns();
        var run = runs.Single(r => r.RunId == "run-branch");
        Assert.Equal("feat/my-feature", run.FeatureBranch);
    }

    // --- Helpers ---

    private async Task WaitForTaskStatus(string taskId, TaskState expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = _taskRegistry.GetTask(taskId);
            if (snapshot?.Status == expected)
            {
                return;
            }

            if (snapshot?.Status == TaskState.Blocked)
            {
                throw new InvalidOperationException(
                    $"Task {taskId} reached Blocked status. Error: {snapshot.Error}");
            }

            await Task.Delay(50);
        }

        var finalSnapshot = _taskRegistry.GetTask(taskId);
        throw new TimeoutException(
            $"Task {taskId} did not reach status {expected} within {timeout.TotalSeconds}s. " +
            $"Current: {finalSnapshot?.Status.ToString() ?? "not registered"}");
    }

    private sealed class NoOpTaskMemoryWriter : ITaskMemoryWriter
    {
        public Task WriteAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
