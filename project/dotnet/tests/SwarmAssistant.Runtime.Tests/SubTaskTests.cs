using Akka.Actor;
using Akka.Routing;
using Akka.TestKit.Xunit2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// Tests for sub-task spawning and hierarchical task management (Phase 14).
/// </summary>
public sealed class SubTaskTests : TestKit
{
    private readonly RuntimeOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RuntimeTelemetry _telemetry;
    private readonly UiEventStream _uiEvents;
    private readonly TaskRegistry _taskRegistry;

    public SubTaskTests()
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

    // --- Message record tests ---

    [Fact]
    public void SpawnSubTask_Record_HasCorrectProperties()
    {
        var msg = new SpawnSubTask("parent-1", "child-1", "Child Task", "Do something", 1);

        Assert.Equal("parent-1", msg.ParentTaskId);
        Assert.Equal("child-1", msg.ChildTaskId);
        Assert.Equal("Child Task", msg.Title);
        Assert.Equal("Do something", msg.Description);
        Assert.Equal(1, msg.Depth);
    }

    [Fact]
    public void SpawnSubTask_DefaultDepth_IsZero()
    {
        var msg = new SpawnSubTask("parent-1", "child-1", "Title", "Desc");

        Assert.Equal(0, msg.Depth);
    }

    [Fact]
    public void SubTaskCompleted_Record_HasCorrectProperties()
    {
        var msg = new SubTaskCompleted("parent-1", "child-1", "Task output");

        Assert.Equal("parent-1", msg.ParentTaskId);
        Assert.Equal("child-1", msg.ChildTaskId);
        Assert.Equal("Task output", msg.Output);
    }

    [Fact]
    public void SubTaskFailed_Record_HasCorrectProperties()
    {
        var msg = new SubTaskFailed("parent-1", "child-1", "Something went wrong");

        Assert.Equal("parent-1", msg.ParentTaskId);
        Assert.Equal("child-1", msg.ChildTaskId);
        Assert.Equal("Something went wrong", msg.Error);
    }

    // --- TaskSnapshot parent/child tests ---

    [Fact]
    public void TaskSnapshot_CarriesParentChildInfo()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TaskSnapshot(
            "child-1",
            "Child Task",
            "Description",
            TaskState.Done,
            now,
            now,
            ParentTaskId: "parent-1",
            ChildTaskIds: ["grandchild-1"]);

        Assert.Equal("parent-1", snapshot.ParentTaskId);
        Assert.NotNull(snapshot.ChildTaskIds);
        Assert.Single(snapshot.ChildTaskIds!);
        Assert.Equal("grandchild-1", snapshot.ChildTaskIds![0]);
    }

    [Fact]
    public void TaskSnapshot_DefaultParentChildFields_AreNull()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TaskSnapshot("t-1", "Title", "Desc", TaskState.Queued, now, now);

        Assert.Null(snapshot.ParentTaskId);
        Assert.Null(snapshot.ChildTaskIds);
    }

    // --- TaskRegistry sub-task tests ---

    [Fact]
    public void TaskRegistry_RegisterSubTask_SetsParentTaskId()
    {
        var assigned = new TaskAssigned("parent-1", "Parent Task", "Description", DateTimeOffset.UtcNow);
        _taskRegistry.Register(assigned);

        var childSnapshot = _taskRegistry.RegisterSubTask("child-1", "Child Task", "Description", "parent-1");

        Assert.NotNull(childSnapshot);
        Assert.Equal("parent-1", childSnapshot.ParentTaskId);
        Assert.Equal(TaskState.Queued, childSnapshot.Status);
    }

    [Fact]
    public void TaskRegistry_RegisterSubTask_UpdatesParentChildTaskIds()
    {
        var assigned = new TaskAssigned("parent-2", "Parent Task", "Description", DateTimeOffset.UtcNow);
        _taskRegistry.Register(assigned);

        _taskRegistry.RegisterSubTask("child-a", "Child A", "Description", "parent-2");

        var parent = _taskRegistry.GetTask("parent-2");
        Assert.NotNull(parent);
        Assert.Contains("child-a", parent!.ChildTaskIds ?? []);
    }

    [Fact]
    public void TaskRegistry_RegisterSubTask_MultipleChildren_AllTracked()
    {
        var assigned = new TaskAssigned("parent-3", "Parent Task", "Description", DateTimeOffset.UtcNow);
        _taskRegistry.Register(assigned);

        _taskRegistry.RegisterSubTask("child-x", "Child X", "Description", "parent-3");
        _taskRegistry.RegisterSubTask("child-y", "Child Y", "Description", "parent-3");

        var parent = _taskRegistry.GetTask("parent-3");
        Assert.NotNull(parent);
        var childIds = parent!.ChildTaskIds ?? [];
        Assert.Equal(2, childIds.Count);
        Assert.Contains("child-x", childIds);
        Assert.Contains("child-y", childIds);
    }

    [Fact]
    public void TaskRegistry_RegisterSubTask_DoesNotOverwriteExisting()
    {
        var assigned = new TaskAssigned("parent-4", "Parent Task", "Description", DateTimeOffset.UtcNow);
        _taskRegistry.Register(assigned);

        _taskRegistry.RegisterSubTask("child-z", "Child Z", "Description", "parent-4");
        var second = _taskRegistry.RegisterSubTask("child-z", "Updated Child Z", "Description", "parent-4");

        Assert.Equal("Child Z", second.Title);
    }

    // --- GOAP WaitForSubTasks action tests ---

    [Fact]
    public void WaitForSubTasks_NotPlanned_WhenSubTasksNotSpawned()
    {
        // With no SubTasksSpawned, WaitForSubTasks precondition is not satisfied
        var planner = new GoapPlanner(SwarmActions.All);
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.AdapterAvailable, true);

        var plan = planner.Plan(state, SwarmActions.CompleteTask);

        Assert.False(plan.DeadEnd);
        Assert.NotNull(plan.RecommendedPlan);
        Assert.DoesNotContain(plan.RecommendedPlan!, a => a.Name == "WaitForSubTasks");
    }

    [Fact]
    public void WaitForSubTasks_PlannedWhenSubTasksSpawnedAndNotCompleted()
    {
        var planner = new GoapPlanner(SwarmActions.All);
        // State after sub-tasks spawned but before they complete AND before Build
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.AdapterAvailable, true)
            .With(WorldKey.SubTasksSpawned, true)
            .With(WorldKey.SubTasksCompleted, false);

        var plan = planner.Plan(state, new Goal(
            "WaitForCompletion",
            new Dictionary<WorldKey, bool> { [WorldKey.SubTasksCompleted] = true }));

        Assert.False(plan.DeadEnd);
        Assert.NotNull(plan.RecommendedPlan);
        Assert.NotEmpty(plan.RecommendedPlan!);
        Assert.Equal("WaitForSubTasks", plan.RecommendedPlan![0].Name);
    }

    // --- Circular dependency / depth limit test ---

    [Fact]
    public void TaskCoordinatorActor_MaxSubTaskDepth_IsBounded()
    {
        Assert.True(TaskCoordinatorActor.MaxSubTaskDepth > 0,
            "MaxSubTaskDepth should be positive to allow at least one level of sub-tasks");
        Assert.True(TaskCoordinatorActor.MaxSubTaskDepth <= 10,
            "MaxSubTaskDepth should have a reasonable upper bound to prevent runaway recursion");
    }

    // --- Integration: DispatcherActor spawns child coordinator ---

    [Fact]
    public async Task DispatcherActor_SpawnSubTask_RegistersChildInRegistry()
    {
        var (_, _, dispatcherActor) = BuildActorHierarchy("spawn-reg");

        var parentTaskId = $"parent-{Guid.NewGuid():N}";
        var childTaskId = $"child-{Guid.NewGuid():N}";

        _taskRegistry.Register(new TaskAssigned(parentTaskId, "Parent Task", "Desc", DateTimeOffset.UtcNow));
        dispatcherActor.Tell(new SpawnSubTask(parentTaskId, childTaskId, "Child Task", "Do child work", 1));

        await WaitForConditionAsync(() => _taskRegistry.GetTask(childTaskId) != null,
            TimeSpan.FromSeconds(10));

        var childSnapshot = _taskRegistry.GetTask(childTaskId);
        Assert.NotNull(childSnapshot);
        Assert.Equal(parentTaskId, childSnapshot!.ParentTaskId);
    }

    [Fact]
    public async Task DispatcherActor_SpawnSubTask_NotifiesParentOnCompletion()
    {
        var (_, _, dispatcherActor) = BuildActorHierarchy("spawn-notify");

        var parentTaskId = $"parent-notify-{Guid.NewGuid():N}";
        var childTaskId = $"child-notify-{Guid.NewGuid():N}";

        _taskRegistry.Register(new TaskAssigned(parentTaskId, "Parent Task", "Desc", DateTimeOffset.UtcNow));

        // Use TestActor as the "parent coordinator" (sender of SpawnSubTask)
        dispatcherActor.Tell(
            new SpawnSubTask(parentTaskId, childTaskId, "Child Task", "Do child work", 1),
            TestActor);

        // The child coordinator will run with local-echo adapters and complete
        var msg = await Task.Run(() =>
            FishForMessage(m => m is SubTaskCompleted or SubTaskFailed, TimeSpan.FromSeconds(30)));

        Assert.True(msg is SubTaskCompleted or SubTaskFailed,
            $"Expected SubTaskCompleted or SubTaskFailed, got {msg?.GetType().Name}");

        switch (msg)
        {
            case SubTaskCompleted completed:
                Assert.Equal(parentTaskId, completed.ParentTaskId);
                Assert.Equal(childTaskId, completed.ChildTaskId);
                break;
            case SubTaskFailed failed:
                Assert.Equal(parentTaskId, failed.ParentTaskId);
                Assert.Equal(childTaskId, failed.ChildTaskId);
                break;
        }
    }

    [Fact]
    public async Task DispatcherActor_DuplicateSpawnSubTask_Ignored()
    {
        var (_, _, dispatcherActor) = BuildActorHierarchy("spawn-dup");

        var parentTaskId = $"parent-dup-{Guid.NewGuid():N}";
        var childTaskId = $"child-dup-{Guid.NewGuid():N}";

        _taskRegistry.Register(new TaskAssigned(parentTaskId, "Parent Task", "Desc", DateTimeOffset.UtcNow));

        dispatcherActor.Tell(new SpawnSubTask(parentTaskId, childTaskId, "Child Task", "Work", 1), TestActor);
        dispatcherActor.Tell(new SpawnSubTask(parentTaskId, childTaskId, "Child Task", "Work", 1), TestActor);

        // Only one child should be registered even after duplicate message
        await WaitForConditionAsync(() => _taskRegistry.GetTask(childTaskId) != null,
            TimeSpan.FromSeconds(10));

        Assert.Equal(1, _taskRegistry.GetTasks().Count(s => s.TaskId == childTaskId));
    }

    // --- Helpers ---

    private (IActorRef workerActor, IActorRef reviewerActor, IActorRef dispatcherActor) BuildActorHierarchy(string suffix)
    {
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);

        var supervisorActor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, _telemetry, null)),
            $"supervisor-{suffix}");

        var blackboardActor = Sys.ActorOf(
            Props.Create(() => new BlackboardActor(_loggerFactory)),
            $"blackboard-{suffix}");

        var workerActor = Sys.ActorOf(
            Props.Create(() => new WorkerActor(_options, _loggerFactory, roleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(_options.WorkerPoolSize)),
            $"worker-{suffix}");

        var reviewerActor = Sys.ActorOf(
            Props.Create(() => new ReviewerActor(_options, _loggerFactory, roleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(_options.ReviewerPoolSize)),
            $"reviewer-{suffix}");

        var dispatcherActor = Sys.ActorOf(
            Props.Create(() => new DispatcherActor(
                workerActor,
                reviewerActor,
                supervisorActor,
                blackboardActor,
                roleEngine,
                _loggerFactory,
                _telemetry,
                _uiEvents,
                _taskRegistry)),
            $"dispatcher-{suffix}");

        return (workerActor, reviewerActor, dispatcherActor);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Condition not met within {timeout.TotalSeconds}s");
    }

    private sealed class NoOpTaskMemoryWriter : ITaskMemoryWriter
    {
        public Task WriteAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
