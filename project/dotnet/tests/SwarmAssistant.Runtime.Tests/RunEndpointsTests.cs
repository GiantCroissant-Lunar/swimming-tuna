using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public void RunRegistry_CreateRun_WithDocumentAndBranch()
    {
        var registry = new RunRegistry();
        var run = registry.CreateRun(
            title: "RFC-014",
            document: "# RFC-014 design doc",
            baseBranch: "develop",
            branchPrefix: "feat");

        Assert.Equal("RFC-014", run.Title);
        Assert.Equal("# RFC-014 design doc", run.Document);
        Assert.Equal("develop", run.BaseBranch);
        Assert.Equal("feat", run.BranchPrefix);
        Assert.Equal("accepted", run.Status);
    }

    [Fact]
    public void RunRegistry_CreateRun_DefaultsBranchFields()
    {
        var registry = new RunRegistry();
        var run = registry.CreateRun(title: "Test Run");

        Assert.Equal("main", run.BaseBranch);
        Assert.Equal("feat", run.BranchPrefix);
    }

    [Fact]
    public void RunRegistry_ListRuns_ReturnsAllRuns()
    {
        var registry = new RunRegistry();
        registry.CreateRun(runId: "run-1", title: "First");
        registry.CreateRun(runId: "run-2", title: "Second");

        var runs = registry.ListRuns();

        Assert.Equal(2, runs.Count);
    }

    [Fact]
    public void RunRegistry_MarkDone_UpdatesStatus()
    {
        var registry = new RunRegistry();
        registry.CreateRun(runId: "run-done", title: "Done Run");

        var result = registry.MarkDone("run-done");

        Assert.True(result);
        var run = registry.GetRun("run-done");
        Assert.Equal("done", run!.Status);
    }

    [Fact]
    public void RunRegistry_MarkDone_ReturnsFalseForUnknown()
    {
        var registry = new RunRegistry();

        var result = registry.MarkDone("run-unknown");

        Assert.False(result);
    }

    [Fact]
    public void RunRegistry_UpdateFeatureBranch_SetsField()
    {
        var registry = new RunRegistry();
        registry.CreateRun(runId: "run-fb", title: "FB Run");

        registry.UpdateFeatureBranch("run-fb", "feat/rfc-014");

        var run = registry.GetRun("run-fb");
        Assert.Equal("feat/rfc-014", run!.FeatureBranch);
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

    // ITaskExecutionEventReader cursor pagination tests

    [Fact]
    public async Task ArcadeDbTaskExecutionEventRepository_ListByTaskAsync_DisabledReturnsEmpty()
    {
        var factory = new Moq.Mock<IHttpClientFactory>();
        var options = Microsoft.Extensions.Options.Options.Create(new Configuration.RuntimeOptions
        {
            ArcadeDbEnabled = false
        });
        var repo = new ArcadeDbTaskExecutionEventRepository(
            options, factory.Object,
            NullLogger<ArcadeDbTaskExecutionEventRepository>.Instance);

        var result = await repo.ListByTaskAsync("task-1", afterSequence: 0, limit: 50);

        Assert.Empty(result);
        factory.Verify(f => f.CreateClient(Moq.It.IsAny<string>()), Moq.Times.Never);
    }

    [Fact]
    public async Task ArcadeDbTaskExecutionEventRepository_ListByRunAsync_DisabledReturnsEmpty()
    {
        var factory = new Moq.Mock<IHttpClientFactory>();
        var options = Microsoft.Extensions.Options.Options.Create(new Configuration.RuntimeOptions
        {
            ArcadeDbEnabled = false
        });
        var repo = new ArcadeDbTaskExecutionEventRepository(
            options, factory.Object,
            NullLogger<ArcadeDbTaskExecutionEventRepository>.Instance);

        var result = await repo.ListByRunAsync("run-1", afterSequence: 0, limit: 50);

        Assert.Empty(result);
        factory.Verify(f => f.CreateClient(Moq.It.IsAny<string>()), Moq.Times.Never);
    }

    [Fact]
    public async Task ArcadeDbTaskExecutionEventRepository_ListByTaskAsync_EmptyTaskIdReturnsEmpty()
    {
        var factory = new Moq.Mock<IHttpClientFactory>();
        var options = Microsoft.Extensions.Options.Options.Create(new Configuration.RuntimeOptions
        {
            ArcadeDbEnabled = true,
            ArcadeDbHttpUrl = "http://arcadedb.test:2480",
            ArcadeDbDatabase = "swarm_assistant"
        });
        var repo = new ArcadeDbTaskExecutionEventRepository(
            options, factory.Object,
            NullLogger<ArcadeDbTaskExecutionEventRepository>.Instance);

        var result = await repo.ListByTaskAsync("", afterSequence: 0);

        Assert.Empty(result);
        factory.Verify(f => f.CreateClient(Moq.It.IsAny<string>()), Moq.Times.Never);
    }

    [Fact]
    public async Task ArcadeDbTaskExecutionEventRepository_ListByRunAsync_EmptyRunIdReturnsEmpty()
    {
        var factory = new Moq.Mock<IHttpClientFactory>();
        var options = Microsoft.Extensions.Options.Options.Create(new Configuration.RuntimeOptions
        {
            ArcadeDbEnabled = true,
            ArcadeDbHttpUrl = "http://arcadedb.test:2480",
            ArcadeDbDatabase = "swarm_assistant"
        });
        var repo = new ArcadeDbTaskExecutionEventRepository(
            options, factory.Object,
            NullLogger<ArcadeDbTaskExecutionEventRepository>.Instance);

        var result = await repo.ListByRunAsync("", afterSequence: 0);

        Assert.Empty(result);
        factory.Verify(f => f.CreateClient(Moq.It.IsAny<string>()), Moq.Times.Never);
    }

    // Unified run existence policy inputs used by GET /runs/{runId}/events

    [Fact]
    public async Task RunExistencePolicy_ExplicitRunCreation_SatisfiesExistence()
    {
        var runRegistry = new RunRegistry();
        var taskRegistry = new TaskRegistry(new NoOpWriter(), NullLogger<TaskRegistry>.Instance);
        runRegistry.CreateRun(runId: "run-explicit");

        var memoryReader = new Moq.Mock<ITaskMemoryReader>();
        memoryReader
            .Setup(x => x.ListByRunIdAsync("run-explicit", 1, Moq.It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<TaskSnapshot>>(Array.Empty<TaskSnapshot>()));
        var runReader = new Moq.Mock<ISwarmRunReader>();
        runReader
            .Setup(x => x.GetAsync("run-explicit", Moq.It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<SwarmRun?>(null));

        var exists = await RunExistsAsync("run-explicit", runRegistry, taskRegistry, memoryReader.Object, runReader.Object);

        Assert.True(exists);
    }

    [Fact]
    public async Task RunExistencePolicy_ImplicitRunViaPersistentTasks_SatisfiesExistence()
    {
        var runRegistry = new RunRegistry();
        var taskRegistry = new TaskRegistry(new NoOpWriter(), NullLogger<TaskRegistry>.Instance);
        var now = DateTimeOffset.UtcNow;

        var memoryReader = new Moq.Mock<ITaskMemoryReader>();
        memoryReader
            .Setup(x => x.ListByRunIdAsync("run-implicit", 1, Moq.It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<TaskSnapshot>>(new[]
            {
                new TaskSnapshot("task-impl-1", "Task 1", "Desc", TaskState.Queued, now, now, RunId: "run-implicit")
            }));
        var runReader = new Moq.Mock<ISwarmRunReader>();
        runReader
            .Setup(x => x.GetAsync("run-implicit", Moq.It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<SwarmRun?>(null));

        var exists = await RunExistsAsync("run-implicit", runRegistry, taskRegistry, memoryReader.Object, runReader.Object);

        Assert.True(exists);
    }

    [Fact]
    public async Task RunExistencePolicy_ImplicitRunViaRunRepository_SatisfiesExistence()
    {
        var runRegistry = new RunRegistry();
        var taskRegistry = new TaskRegistry(new NoOpWriter(), NullLogger<TaskRegistry>.Instance);
        var now = DateTimeOffset.UtcNow;

        var memoryReader = new Moq.Mock<ITaskMemoryReader>();
        memoryReader
            .Setup(x => x.ListByRunIdAsync("run-from-repo", 1, Moq.It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<TaskSnapshot>>(Array.Empty<TaskSnapshot>()));
        var runReader = new Moq.Mock<ISwarmRunReader>();
        runReader
            .Setup(x => x.GetAsync("run-from-repo", Moq.It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<SwarmRun?>(new SwarmRun("run-from-repo", "task-1", "builder", null, "running", now, now)));

        var exists = await RunExistsAsync("run-from-repo", runRegistry, taskRegistry, memoryReader.Object, runReader.Object);

        Assert.True(exists);
    }

    [Fact]
    public async Task RunExistencePolicy_NoRunNoTasks_ReturnsFalse()
    {
        var runRegistry = new RunRegistry();
        var taskRegistry = new TaskRegistry(new NoOpWriter(), NullLogger<TaskRegistry>.Instance);

        var memoryReader = new Moq.Mock<ITaskMemoryReader>();
        memoryReader
            .Setup(x => x.ListByRunIdAsync("run-ghost", 1, Moq.It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<TaskSnapshot>>(Array.Empty<TaskSnapshot>()));
        var runReader = new Moq.Mock<ISwarmRunReader>();
        runReader
            .Setup(x => x.GetAsync("run-ghost", Moq.It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<SwarmRun?>(null));

        var exists = await RunExistsAsync("run-ghost", runRegistry, taskRegistry, memoryReader.Object, runReader.Object);

        Assert.False(exists);
    }

    private static async Task<bool> RunExistsAsync(
        string runId,
        RunRegistry runRegistry,
        TaskRegistry taskRegistry,
        ITaskMemoryReader memoryReader,
        ISwarmRunReader runReader,
        CancellationToken cancellationToken = default)
    {
        return runRegistry.GetRun(runId) is not null
            || taskRegistry.GetTasksByRunId(runId, 1).Count > 0
            || (await memoryReader.ListByRunIdAsync(runId, 1, cancellationToken)).Count > 0
            || await runReader.GetAsync(runId, cancellationToken) is not null;
    }

    private sealed class NoOpWriter : ITaskMemoryWriter
    {
        public Task WriteAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
