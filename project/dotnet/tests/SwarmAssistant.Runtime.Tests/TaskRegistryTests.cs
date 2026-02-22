using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Tasks;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tests;

public sealed class TaskRegistryTests
{
    [Fact]
    public void Registry_TracksLifecycleAndOutputs()
    {
        var writer = new InMemoryTaskMemoryWriter();
        var registry = new TaskRegistry(writer, NullLogger<TaskRegistry>.Instance);

        var assigned = new TaskAssigned(
            TaskId: "task-1",
            Title: "Test Task",
            Description: "Description",
            AssignedAt: DateTimeOffset.UtcNow);

        registry.Register(assigned);
        registry.Transition("task-1", TaskState.Planning);
        registry.SetRoleOutput("task-1", SwarmRole.Planner, "plan");
        registry.Transition("task-1", TaskState.Building);
        registry.SetRoleOutput("task-1", SwarmRole.Builder, "build");
        registry.Transition("task-1", TaskState.Reviewing);
        registry.SetRoleOutput("task-1", SwarmRole.Reviewer, "review");
        registry.MarkDone("task-1", "summary");

        var snapshot = registry.GetTask("task-1");

        Assert.NotNull(snapshot);
        Assert.Equal(TaskState.Done, snapshot!.Status);
        Assert.Equal("plan", snapshot.PlanningOutput);
        Assert.Equal("build", snapshot.BuildOutput);
        Assert.Equal("review", snapshot.ReviewOutput);
        Assert.Equal("summary", snapshot.Summary);
        Assert.Null(snapshot.Error);
    }

    [Fact]
    public void Registry_MarkFailedSetsBlockedStatus()
    {
        var registry = new TaskRegistry(new InMemoryTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
        var assigned = new TaskAssigned(
            TaskId: "task-2",
            Title: "Failure Task",
            Description: "Description",
            AssignedAt: DateTimeOffset.UtcNow);

        registry.Register(assigned);
        registry.MarkFailed("task-2", "boom");

        var snapshot = registry.GetTask("task-2");
        Assert.NotNull(snapshot);
        Assert.Equal(TaskState.Blocked, snapshot!.Status);
        Assert.Equal("boom", snapshot.Error);
    }

    [Fact]
    public void Registry_RegisterDoesNotOverwriteExistingTask()
    {
        var registry = new TaskRegistry(new InMemoryTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
        var assigned = new TaskAssigned(
            TaskId: "task-3",
            Title: "Original Title",
            Description: "Description",
            AssignedAt: DateTimeOffset.UtcNow);

        registry.Register(assigned);
        registry.Transition("task-3", TaskState.Planning);
        registry.SetRoleOutput("task-3", SwarmRole.Planner, "existing plan");

        registry.Register(assigned with { Title = "Overwriting Title" });

        var snapshot = registry.GetTask("task-3");
        Assert.NotNull(snapshot);
        Assert.Equal(TaskState.Planning, snapshot!.Status);
        Assert.Equal("existing plan", snapshot.PlanningOutput);
        Assert.Equal("Original Title", snapshot.Title);
    }

    private sealed class InMemoryTaskMemoryWriter : ITaskMemoryWriter
    {
        public Task WriteAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
