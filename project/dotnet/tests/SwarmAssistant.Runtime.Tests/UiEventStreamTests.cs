using SwarmAssistant.Runtime.Ui;

namespace SwarmAssistant.Runtime.Tests;

public sealed class UiEventStreamTests
{
    [Fact]
    public async Task Subscribe_ReceivesPublishedMessages()
    {
        var stream = new UiEventStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var receiveTask = Task.Run(async () =>
        {
            await foreach (var evt in stream.Subscribe(cts.Token))
            {
                return evt;
            }

            throw new InvalidOperationException("No events received.");
        }, cts.Token);

        stream.Publish("agui.test", "task-1", new { message = "hello" });

        var received = await receiveTask;
        Assert.Equal("agui.test", received.Type);
        Assert.Equal("task-1", received.TaskId);
        Assert.Equal(1, received.Sequence);
    }

    [Fact]
    public void GetRecent_ReturnsPublishedHistory()
    {
        var stream = new UiEventStream();
        stream.Publish("first", "task-a", new { value = 1 });
        stream.Publish("second", "task-b", new { value = 2 });

        var recent = stream.GetRecent(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal("first", recent[0].Type);
        Assert.Equal("second", recent[1].Type);
    }

    [Fact]
    public void GetRecent_WhenCountIsSmaller_ReturnsLatestEntries()
    {
        var stream = new UiEventStream();
        stream.Publish("one", "task-1", new { value = 1 });
        stream.Publish("two", "task-2", new { value = 2 });
        stream.Publish("three", "task-3", new { value = 3 });

        var recent = stream.GetRecent(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal("two", recent[0].Type);
        Assert.Equal("three", recent[1].Type);
    }
}
