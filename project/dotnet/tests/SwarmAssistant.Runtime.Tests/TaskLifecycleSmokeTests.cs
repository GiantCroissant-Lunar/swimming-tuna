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
/// End-to-end smoke tests that boot the full Akka actor hierarchy with local-echo
/// adapters and verify complete task lifecycle: submit → orchestrate → plan → build → review → done.
/// </summary>
public sealed class TaskLifecycleSmokeTests : TestKit
{
    private readonly RuntimeOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RuntimeTelemetry _telemetry;
    private readonly UiEventStream _uiEvents;
    private readonly TaskRegistry _taskRegistry;

    public TaskLifecycleSmokeTests()
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

    [Fact]
    public async Task FullLifecycle_HappyPath_TaskReachesDone()
    {
        // Arrange: build the full actor hierarchy
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);

        var blackboardActor = Sys.ActorOf(
            Props.Create(() => new BlackboardActor(_loggerFactory)),
            "blackboard");

        var supervisorActor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, _telemetry, null, blackboardActor)),
            "supervisor");

        var workerActor = Sys.ActorOf(
            Props.Create(() => new WorkerActor(_options, _loggerFactory, roleEngine, _telemetry, null))
                .WithRouter(new SmallestMailboxPool(_options.WorkerPoolSize)),
            "worker-pool");

        var reviewerActor = Sys.ActorOf(
            Props.Create(() => new ReviewerActor(_options, _loggerFactory, roleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(_options.ReviewerPoolSize)),
            "reviewer-pool");

        var consensusActor = Sys.ActorOf(
            Props.Create(() => new ConsensusActor(_loggerFactory.CreateLogger<ConsensusActor>())),
            "consensus-pool");

        var dispatcherActor = Sys.ActorOf(
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
                null,
                null,
                null,
                null)),
            "dispatcher");

        var taskId = $"smoke-{Guid.NewGuid():N}";
        var task = new TaskAssigned(taskId, "Smoke Test Task", "Verify full lifecycle.", DateTimeOffset.UtcNow);

        // Act: submit a task
        dispatcherActor.Tell(task);

        // Assert: wait for the task to reach Done status
        await WaitForTaskStatus(taskId, TaskState.Done, timeout: TimeSpan.FromSeconds(30));

        var snapshot = _taskRegistry.GetTask(taskId);
        Assert.NotNull(snapshot);
        Assert.Equal(TaskState.Done, snapshot.Status);
        Assert.NotNull(snapshot.PlanningOutput);
        Assert.NotNull(snapshot.BuildOutput);
        Assert.NotNull(snapshot.ReviewOutput);
        Assert.NotNull(snapshot.Summary);
        Assert.Null(snapshot.Error);
    }

    [Fact]
    public async Task FullLifecycle_HappyPath_UiEventsPublished()
    {
        // Arrange
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);

        var blackboardActor = Sys.ActorOf(
            Props.Create(() => new BlackboardActor(_loggerFactory)),
            "blackboard-ui");

        var supervisorActor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, _telemetry, null, blackboardActor)),
            "supervisor-ui");

        var workerActor = Sys.ActorOf(
            Props.Create(() => new WorkerActor(_options, _loggerFactory, roleEngine, _telemetry, null))
                .WithRouter(new SmallestMailboxPool(_options.WorkerPoolSize)),
            "worker-pool-ui");

        var reviewerActor = Sys.ActorOf(
            Props.Create(() => new ReviewerActor(_options, _loggerFactory, roleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(_options.ReviewerPoolSize)),
            "reviewer-pool-ui");

        var consensusActor = Sys.ActorOf(
            Props.Create(() => new ConsensusActor(_loggerFactory.CreateLogger<ConsensusActor>())),
            "consensus-pool-ui");

        var dispatcherActor = Sys.ActorOf(
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
                null,
                null,
                null,
                null)),
            "dispatcher-ui");

        var taskId = $"smoke-ui-{Guid.NewGuid():N}";
        var task = new TaskAssigned(taskId, "UI Events Test", "Verify UI events.", DateTimeOffset.UtcNow);

        // Act
        dispatcherActor.Tell(task);
        await WaitForTaskStatus(taskId, TaskState.Done, timeout: TimeSpan.FromSeconds(30));

        // Assert: UI events were published for the task
        var events = _uiEvents.GetRecent(200);
        var taskEvents = events.Where(e => e.TaskId == taskId).ToList();

        Assert.True(taskEvents.Count >= 4,
            $"Expected at least 4 UI events for the task, got {taskEvents.Count}");

        // Should have a task submitted event
        Assert.Contains(taskEvents, e => e.Type == "agui.task.submitted");

        // Should have a task done event
        Assert.Contains(taskEvents, e => e.Type == "agui.task.done");
    }

    [Fact]
    public async Task FullLifecycle_MultipleTasksInParallel_AllComplete()
    {
        // Arrange
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);

        var blackboardActor = Sys.ActorOf(
            Props.Create(() => new BlackboardActor(_loggerFactory)),
            "blackboard-parallel");

        var supervisorActor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, _telemetry, null, blackboardActor)),
            "supervisor-parallel");

        var workerActor = Sys.ActorOf(
            Props.Create(() => new WorkerActor(_options, _loggerFactory, roleEngine, _telemetry, null))
                .WithRouter(new SmallestMailboxPool(_options.WorkerPoolSize)),
            "worker-pool-parallel");

        var reviewerActor = Sys.ActorOf(
            Props.Create(() => new ReviewerActor(_options, _loggerFactory, roleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(_options.ReviewerPoolSize)),
            "reviewer-pool-parallel");

        var consensusActor = Sys.ActorOf(
            Props.Create(() => new ConsensusActor(_loggerFactory.CreateLogger<ConsensusActor>())),
            "consensus-pool-parallel");

        var dispatcherActor = Sys.ActorOf(
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
                null,
                null,
                null,
                null)),
            "dispatcher-parallel");

        var taskIds = Enumerable.Range(1, 3)
            .Select(i => $"smoke-parallel-{i}-{Guid.NewGuid():N}")
            .ToList();

        // Act: submit 3 tasks concurrently
        foreach (var taskId in taskIds)
        {
            dispatcherActor.Tell(new TaskAssigned(
                taskId, $"Parallel Task {taskId[..16]}", "Parallel test.", DateTimeOffset.UtcNow));
        }

        // Assert: all tasks reach Done
        var waitTasks = taskIds
            .Select(id => WaitForTaskStatus(id, TaskState.Done, timeout: TimeSpan.FromSeconds(30)))
            .ToArray();
        await Task.WhenAll(waitTasks);

        foreach (var taskId in taskIds)
        {
            var snapshot = _taskRegistry.GetTask(taskId);
            Assert.NotNull(snapshot);
            Assert.Equal(TaskState.Done, snapshot.Status);
        }
    }

    [Fact]
    public async Task FullLifecycle_SupervisorTracksCompletion()
    {
        // Arrange
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);

        var blackboardActor = Sys.ActorOf(
            Props.Create(() => new BlackboardActor(_loggerFactory)),
            "blackboard-tracking");

        var supervisorActor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, _telemetry, null, blackboardActor)),
            "supervisor-tracking");

        var workerActor = Sys.ActorOf(
            Props.Create(() => new WorkerActor(_options, _loggerFactory, roleEngine, _telemetry, null))
                .WithRouter(new SmallestMailboxPool(_options.WorkerPoolSize)),
            "worker-pool-tracking");

        var reviewerActor = Sys.ActorOf(
            Props.Create(() => new ReviewerActor(_options, _loggerFactory, roleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(_options.ReviewerPoolSize)),
            "reviewer-pool-tracking");

        var consensusActor = Sys.ActorOf(
            Props.Create(() => new ConsensusActor(_loggerFactory.CreateLogger<ConsensusActor>())),
            "consensus-pool-tracking");

        var dispatcherActor = Sys.ActorOf(
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
                null,
                null,
                null,
                null)),
            "dispatcher-tracking");

        var taskId = $"smoke-track-{Guid.NewGuid():N}";
        dispatcherActor.Tell(new TaskAssigned(taskId, "Track Test", "Verify supervisor.", DateTimeOffset.UtcNow));

        await WaitForTaskStatus(taskId, TaskState.Done, timeout: TimeSpan.FromSeconds(30));

        // Assert: supervisor has tracked the completion
        var probe = CreateTestProbe();
        supervisorActor.Tell(new GetSupervisorSnapshot(), probe);
        var snapshot = probe.ExpectMsg<SupervisorSnapshot>(TimeSpan.FromSeconds(5));

        Assert.True(snapshot.Started > 0, "Supervisor should have tracked at least one started task");
        Assert.True(snapshot.Completed > 0, "Supervisor should have tracked at least one completed task");
        Assert.Equal(0, snapshot.Escalations);
    }

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

            // Fail fast if the task is blocked — it won't recover without external intervention
            if (snapshot?.Status == TaskState.Blocked)
            {
                throw new InvalidOperationException(
                    $"Task {taskId} reached Blocked state instead of {expected}. Error: {snapshot.Error}");
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
