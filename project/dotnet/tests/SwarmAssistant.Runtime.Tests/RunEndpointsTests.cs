using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Tasks;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tests;

public sealed class RunEndpointsTests
{
    // RunRegistry tests

    [Fact]
    public void RunRegistry_CreateRun_GeneratesRunId()
    {
        var registry = new RunRegistry();
        var run = registry.CreateRun();

        Assert.False(string.IsNullOrWhiteSpace(run.RunId));
        Assert.StartsWith("run-", run.RunId, StringComparison.Ordinal);
        Assert.Null(run.Title);
    }

    [Fact]
    public void RunRegistry_CreateRun_WithExplicitIdAndTitle()
    {
        var registry = new RunRegistry();
        var run = registry.CreateRun(runId: "run-explicit-1", title: "My Run");

        Assert.Equal("run-explicit-1", run.RunId);
        Assert.Equal("My Run", run.Title);
        Assert.True(run.CreatedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void RunRegistry_GetRun_ReturnsNullForUnknownId()
    {
        var registry = new RunRegistry();
        var run = registry.GetRun("run-unknown");

        Assert.Null(run);
    }

    [Fact]
    public void RunRegistry_GetRun_ReturnsPreviouslyCreatedRun()
    {
        var registry = new RunRegistry();
        registry.CreateRun(runId: "run-abc", title: "ABC");

        var run = registry.GetRun("run-abc");

        Assert.NotNull(run);
        Assert.Equal("run-abc", run!.RunId);
        Assert.Equal("ABC", run.Title);
    }

    [Fact]
    public void RunRegistry_CreateRun_DuplicateIdReturnsExistingEntry()
    {
        var registry = new RunRegistry();
        var first = registry.CreateRun(runId: "run-dup", title: "First");
        var second = registry.CreateRun(runId: "run-dup", title: "Second");

        Assert.Equal("First", first.Title);
        Assert.Equal("First", second.Title);

        var stored = registry.GetRun("run-dup");
        Assert.Equal("First", stored!.Title);
    }

    // TaskRegistry.GetTasksByRunId tests

    [Fact]
    public void TaskRegistry_GetTasksByRunId_ReturnsMatchingTasks()
    {
        var registry = new TaskRegistry(new NoOpWriter(), NullLogger<TaskRegistry>.Instance);
        var now = DateTimeOffset.UtcNow;

        registry.ImportSnapshots(new[]
        {
            new TaskSnapshot("task-r1", "Task R1", "Desc", TaskState.Done, now, now, RunId: "run-x"),
            new TaskSnapshot("task-r2", "Task R2", "Desc", TaskState.Queued, now, now, RunId: "run-x"),
            new TaskSnapshot("task-r3", "Task R3", "Desc", TaskState.Done, now, now, RunId: "run-y")
        }, overwrite: true);

        var tasks = registry.GetTasksByRunId("run-x");

        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, t => Assert.Equal("run-x", t.RunId));
    }

    [Fact]
    public void TaskRegistry_GetTasksByRunId_ReturnsEmptyForUnknownRunId()
    {
        var registry = new TaskRegistry(new NoOpWriter(), NullLogger<TaskRegistry>.Instance);

        var tasks = registry.GetTasksByRunId("run-unknown");

        Assert.Empty(tasks);
    }

    [Fact]
    public void TaskRegistry_GetTasksByRunId_RespectsLimit()
    {
        var registry = new TaskRegistry(new NoOpWriter(), NullLogger<TaskRegistry>.Instance);
        var now = DateTimeOffset.UtcNow;

        registry.ImportSnapshots(Enumerable.Range(1, 5).Select(i =>
            new TaskSnapshot($"task-l{i}", $"Task L{i}", "Desc", TaskState.Done, now, now.AddMinutes(i), RunId: "run-limit")),
            overwrite: true);

        var tasks = registry.GetTasksByRunId("run-limit", limit: 3);

        Assert.Equal(3, tasks.Count);
    }

    // ArcadeDbTaskMemoryReader.ListByRunIdAsync tests

    [Fact]
    public async Task ArcadeDbTaskMemoryReader_ListByRunIdAsync_DisabledReturnsEmpty()
    {
        var factory = new Moq.Mock<IHttpClientFactory>();
        var options = Microsoft.Extensions.Options.Options.Create(new Configuration.RuntimeOptions
        {
            ArcadeDbEnabled = false
        });
        var reader = new ArcadeDbTaskMemoryReader(options, factory.Object, NullLogger<ArcadeDbTaskMemoryReader>.Instance);

        var result = await reader.ListByRunIdAsync("run-xyz");

        Assert.Empty(result);
    }

    [Fact]
    public async Task ArcadeDbTaskMemoryReader_ListByRunIdAsync_EmptyRunIdReturnsEmpty()
    {
        var factory = new Moq.Mock<IHttpClientFactory>();
        var options = Microsoft.Extensions.Options.Options.Create(new Configuration.RuntimeOptions
        {
            ArcadeDbEnabled = true,
            ArcadeDbHttpUrl = "http://arcadedb.test:2480",
            ArcadeDbDatabase = "swarm_assistant"
        });
        var reader = new ArcadeDbTaskMemoryReader(options, factory.Object, NullLogger<ArcadeDbTaskMemoryReader>.Instance);

        var result = await reader.ListByRunIdAsync("");

        Assert.Empty(result);
        factory.Verify(f => f.CreateClient(Moq.It.IsAny<string>()), Moq.Times.Never);
    }

    private sealed class NoOpWriter : ITaskMemoryWriter
    {
        public Task WriteAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
