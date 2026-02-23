using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Tasks;

namespace SwarmAssistant.Runtime.Tests;

public sealed class ArcadeDbTaskExecutionEventRepositoryTests
{
    [Fact]
    public async Task AppendAsync_AutoCreateSchema_EnsuresSchemaOnlyOnce()
    {
        var recorder = new CommandRecorderHandler();
        using var client = new HttpClient(recorder);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var repository = CreateRepository(factory.Object, autoCreateSchema: true);

        var evt1 = MakeEvent("evt-1", "run-1", "task-1", "task.started");
        var evt2 = MakeEvent("evt-2", "run-1", "task-1", "role.completed");

        await repository.AppendAsync(evt1);
        await repository.AppendAsync(evt2);

        // Schema should be bootstrapped only once
        Assert.Equal(1, recorder.CountCommand("CREATE DOCUMENT TYPE TaskExecutionEvent IF NOT EXISTS"));

        // Two INSERT commands
        Assert.Equal(2, recorder.CountContaining("INSERT INTO TaskExecutionEvent SET"));
    }

    [Fact]
    public async Task AppendAsync_AssignsMonotonicallyIncreasingTaskSequence()
    {
        var recorder = new CommandRecorderHandler();
        using var client = new HttpClient(recorder);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var repository = CreateRepository(factory.Object, autoCreateSchema: false);

        for (var i = 0; i < 5; i++)
        {
            await repository.AppendAsync(MakeEvent($"evt-{i}", "run-1", "task-abc", "step"));
        }

        var sequences = recorder.CapturedTaskSequences;
        Assert.Equal(5, sequences.Count);
        for (var i = 0; i < sequences.Count; i++)
        {
            Assert.Equal(i + 1, sequences[i]);
        }
    }

    [Fact]
    public async Task AppendAsync_SeparateRunsHaveIndependentRunSequences()
    {
        var recorder = new CommandRecorderHandler();
        using var client = new HttpClient(recorder);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var repository = CreateRepository(factory.Object, autoCreateSchema: false);

        await repository.AppendAsync(MakeEvent("e1", "run-A", "task-1", "step"));
        await repository.AppendAsync(MakeEvent("e2", "run-B", "task-1", "step"));
        await repository.AppendAsync(MakeEvent("e3", "run-A", "task-1", "step"));

        // run-A should have sequences 1, 2; run-B should have sequence 1
        Assert.Equal([1L, 2L], recorder.RunSequencesByRun("run-A"));
        Assert.Equal([1L], recorder.RunSequencesByRun("run-B"));
    }

    [Fact]
    public async Task AppendAsync_ArcadeDbDisabled_DoesNotCallHttp()
    {
        var factory = new Mock<IHttpClientFactory>();
        var repository = CreateRepository(factory.Object, autoCreateSchema: false, enabled: false);

        await repository.AppendAsync(MakeEvent("evt-1", "run-1", "task-1", "step"));

        factory.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ListByTaskAsync_ReturnsOrderedEvents()
    {
        var now = DateTimeOffset.UtcNow;
        var responseBody = $$"""
        {
          "result": [
            {
              "eventId": "e-1",
              "runId": "run-x",
              "taskId": "task-y",
              "eventType": "task.started",
              "payload": null,
              "occurredAt": "{{now.AddSeconds(-10).ToString("O")}}",
              "taskSequence": 1,
              "runSequence": 1
            },
            {
              "eventId": "e-2",
              "runId": "run-x",
              "taskId": "task-y",
              "eventType": "role.completed",
              "payload": "{\"role\":\"planner\"}",
              "occurredAt": "{{now.ToString("O")}}",
              "taskSequence": 2,
              "runSequence": 2
            }
          ]
        }
        """;

        var handler = new FixedResponseHandler(responseBody);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var repository = CreateRepository(factory.Object, autoCreateSchema: false);
        var events = await repository.ListByTaskAsync("task-y");

        Assert.Equal(2, events.Count);
        Assert.Equal("e-1", events[0].EventId);
        Assert.Equal(1L, events[0].TaskSequence);
        Assert.Equal("e-2", events[1].EventId);
        Assert.Equal(2L, events[1].TaskSequence);
        Assert.Equal("{\"role\":\"planner\"}", events[1].Payload);
    }

    [Fact]
    public async Task ListByRunAsync_ReturnsOrderedEvents()
    {
        var now = DateTimeOffset.UtcNow;
        var responseBody = $$"""
        {
          "result": [
            {
              "eventId": "e-10",
              "runId": "run-z",
              "taskId": "task-a",
              "eventType": "task.started",
              "payload": null,
              "occurredAt": "{{now.ToString("O")}}",
              "taskSequence": 1,
              "runSequence": 1
            }
          ]
        }
        """;

        var handler = new FixedResponseHandler(responseBody);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var repository = CreateRepository(factory.Object, autoCreateSchema: false);
        var events = await repository.ListByRunAsync("run-z");

        Assert.Single(events);
        Assert.Equal("e-10", events[0].EventId);
        Assert.Equal("run-z", events[0].RunId);
        Assert.Equal(1L, events[0].RunSequence);
    }

    [Fact]
    public async Task ListByTaskAsync_ArcadeDbDisabled_ReturnsEmpty()
    {
        var factory = new Mock<IHttpClientFactory>();
        var repository = CreateRepository(factory.Object, autoCreateSchema: false, enabled: false);

        var result = await repository.ListByTaskAsync("task-1");

        Assert.Empty(result);
        factory.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Never);
    }

    private static ArcadeDbTaskExecutionEventRepository CreateRepository(
        IHttpClientFactory factory,
        bool autoCreateSchema,
        bool enabled = true)
    {
        var options = Options.Create(new RuntimeOptions
        {
            ArcadeDbEnabled = enabled,
            ArcadeDbHttpUrl = "http://arcadedb.test:2480",
            ArcadeDbDatabase = "swarm_assistant",
            ArcadeDbAutoCreateSchema = autoCreateSchema
        });

        return new ArcadeDbTaskExecutionEventRepository(
            options,
            factory,
            NullLogger<ArcadeDbTaskExecutionEventRepository>.Instance);
    }

    private static TaskExecutionEvent MakeEvent(string eventId, string runId, string taskId, string eventType)
    {
        return new TaskExecutionEvent(
            EventId: eventId,
            RunId: runId,
            TaskId: taskId,
            EventType: eventType,
            Payload: null,
            OccurredAt: DateTimeOffset.UtcNow,
            TaskSequence: 0,
            RunSequence: 0);
    }

    private sealed class CommandRecorderHandler : HttpMessageHandler
    {
        private readonly List<string> _commands = [];
        private readonly List<(string RunId, long RunSeq)> _runSeqCaptures = [];
        private readonly List<long> _taskSeqCaptures = [];

        public List<long> CapturedTaskSequences => _taskSeqCaptures;

        public IReadOnlyList<long> RunSequencesByRun(string runId)
        {
            return _runSeqCaptures
                .Where(x => string.Equals(x.RunId, runId, StringComparison.Ordinal))
                .Select(x => x.RunSeq)
                .ToList();
        }

        public int CountCommand(string command)
        {
            return _commands.Count(c => string.Equals(c, command, StringComparison.Ordinal));
        }

        public int CountContaining(string substring)
        {
            return _commands.Count(c => c.Contains(substring, StringComparison.Ordinal));
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var command = root.GetProperty("command").GetString();

            if (!string.IsNullOrWhiteSpace(command))
            {
                _commands.Add(command);

                // Capture sequence parameters from INSERT commands
                if (command.Contains("INSERT INTO TaskExecutionEvent", StringComparison.Ordinal) &&
                    root.TryGetProperty("params", out var paramsEl))
                {
                    if (paramsEl.TryGetProperty("taskSequence", out var tSeqEl) &&
                        tSeqEl.TryGetInt64(out var tSeq))
                    {
                        _taskSeqCaptures.Add(tSeq);
                    }

                    if (paramsEl.TryGetProperty("runId", out var runIdEl) &&
                        paramsEl.TryGetProperty("runSequence", out var rSeqEl) &&
                        rSeqEl.TryGetInt64(out var rSeq))
                    {
                        _runSeqCaptures.Add((runIdEl.GetString() ?? string.Empty, rSeq));
                    }
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":[]}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public FixedResponseHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
