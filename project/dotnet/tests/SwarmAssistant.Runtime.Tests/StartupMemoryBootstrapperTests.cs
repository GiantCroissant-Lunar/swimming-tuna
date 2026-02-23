using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Runtime.Tasks;
using SwarmAssistant.Runtime.Ui;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tests;

public sealed class StartupMemoryBootstrapperTests
{
    [Fact]
    public async Task RestoreAsync_ImportsSnapshotsAndPublishesBootstrapEvents()
    {
        var registry = new TaskRegistry(new NoopWriter(), NullLogger<TaskRegistry>.Instance);
        var stream = new UiEventStream();
        var reader = new FakeReader(
        [
            new TaskSnapshot("task-a", "Task A", "Desc A", TaskState.Planning, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new TaskSnapshot("task-b", "Task B", "Desc B", TaskState.Done, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        var bootstrapper = new StartupMemoryBootstrapper(
            reader,
            registry,
            stream,
            NullLogger<StartupMemoryBootstrapper>.Instance);

        var imported = await bootstrapper.RestoreAsync(enabled: true, limit: 200, CancellationToken.None);

        Assert.Equal(2, imported);
        Assert.Equal(2, registry.Count);
        var recent = stream.GetRecent(20);
        Assert.Contains(recent, evt => evt.Type == "agui.memory.bootstrap");
        Assert.Contains(recent, evt => evt.Type == "agui.memory.tasks");
        Assert.Contains(recent, evt => evt.Type == "agui.ui.surface");
        Assert.Contains(recent, evt => evt.Type == "agui.ui.patch");
    }

    [Fact]
    public async Task RestoreAsync_WhenReaderThrows_PublishesFailureEvent()
    {
        var registry = new TaskRegistry(new NoopWriter(), NullLogger<TaskRegistry>.Instance);
        var stream = new UiEventStream();
        var reader = new ThrowingReader();
        var bootstrapper = new StartupMemoryBootstrapper(
            reader,
            registry,
            stream,
            NullLogger<StartupMemoryBootstrapper>.Instance);

        var imported = await bootstrapper.RestoreAsync(enabled: true, limit: 200, CancellationToken.None);

        Assert.Equal(0, imported);
        Assert.Equal(0, registry.Count);
        var recent = stream.GetRecent(10);
        Assert.Contains(recent, evt => evt.Type == "agui.memory.bootstrap.failed");
    }

    [Fact]
    public async Task RestoreAsync_WhenDisabled_DoesNothing()
    {
        var registry = new TaskRegistry(new NoopWriter(), NullLogger<TaskRegistry>.Instance);
        var stream = new UiEventStream();
        var reader = new FakeReader(
        [
            new TaskSnapshot("task-a", "Task A", "Desc A", TaskState.Queued, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        var bootstrapper = new StartupMemoryBootstrapper(
            reader,
            registry,
            stream,
            NullLogger<StartupMemoryBootstrapper>.Instance);

        var imported = await bootstrapper.RestoreAsync(enabled: false, limit: 200, CancellationToken.None);

        Assert.Equal(0, imported);
        Assert.Equal(0, registry.Count);
        Assert.Empty(stream.GetRecent(10));
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 2, false)]
    [InlineData(false, 0, false)]
    public void ShouldAutoSubmitDemoTask_RespectsBootstrapState(bool autoSubmit, int count, bool expected)
    {
        var actual = StartupMemoryBootstrapper.ShouldAutoSubmitDemoTask(autoSubmit, count);
        Assert.Equal(expected, actual);
    }

    private sealed class NoopWriter : ITaskMemoryWriter
    {
        public Task WriteAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReader : ITaskMemoryReader
    {
        private readonly IReadOnlyList<TaskSnapshot> _snapshots;

        public FakeReader(IReadOnlyList<TaskSnapshot> snapshots)
        {
            _snapshots = snapshots;
        }

        public Task<IReadOnlyList<TaskSnapshot>> ListAsync(int limit = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TaskSnapshot>>(_snapshots.Take(limit).ToList());
        }

        public Task<TaskSnapshot?> GetAsync(string taskId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_snapshots.FirstOrDefault(snapshot => snapshot.TaskId == taskId));
        }

        public Task<IReadOnlyList<TaskSnapshot>> ListByRunIdAsync(string runId, int limit = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TaskSnapshot>>(
                _snapshots.Where(s => s.RunId == runId).Take(limit).ToList());
        }
    }

    private sealed class ThrowingReader : ITaskMemoryReader
    {
        public Task<IReadOnlyList<TaskSnapshot>> ListAsync(int limit = 50, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("ArcadeDB unavailable");
        }

        public Task<TaskSnapshot?> GetAsync(string taskId, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("ArcadeDB unavailable");
        }

        public Task<IReadOnlyList<TaskSnapshot>> ListByRunIdAsync(string runId, int limit = 50, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("ArcadeDB unavailable");
        }
    }
}
