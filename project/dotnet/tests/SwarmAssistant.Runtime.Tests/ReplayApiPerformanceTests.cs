using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Tasks;

namespace SwarmAssistant.Runtime.Tests;

/// <summary>
/// Performance and reliability tests for the replay APIs (Phase 8 / Issue 20).
/// Validates: cursor-based pagination at high event volumes, limit clamping,
/// concurrent-append sequence safety, in-memory parsing throughput, and
/// error-resilience under HTTP failures.
/// </summary>
public sealed class ReplayApiPerformanceTests
{
    // -------------------------------------------------------------------------
    // Pagination at high event volumes
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListByTaskAsync_MultiPagePagination_ReturnsAllEventsInOrder()
    {
        // Walk through 1 000 events in pages of 200 using cursor-based pagination.
        const int totalEvents = 1_000;
        const int pageSize = 200;

        var handler = new PaginatedTaskEventHandler("task-big", totalEvents, pageSize);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var repository = CreateRepository(factory.Object);
        var collected = new List<TaskExecutionEvent>();

        long afterSequence = 0;
        while (true)
        {
            var page = await repository.ListByTaskAsync("task-big", afterSequence, pageSize);
            if (page.Count == 0)
            {
                break;
            }

            collected.AddRange(page);
            afterSequence = collected[^1].TaskSequence;
        }

        Assert.Equal(totalEvents, collected.Count);

        // Sequences must be strictly ascending and gap-free
        for (var i = 0; i < collected.Count; i++)
        {
            Assert.Equal(i + 1L, collected[i].TaskSequence);
        }
    }

    [Fact]
    public async Task ListByRunAsync_MultiPagePagination_ReturnsAllEventsInOrder()
    {
        const int totalEvents = 500;
        const int pageSize = 100;

        var handler = new PaginatedRunEventHandler("run-big", totalEvents, pageSize);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var repository = CreateRepository(factory.Object);
        var collected = new List<TaskExecutionEvent>();

        long afterSequence = 0;
        while (true)
        {
            var page = await repository.ListByRunAsync("run-big", afterSequence, pageSize);
            if (page.Count == 0)
            {
                break;
            }

            collected.AddRange(page);
            afterSequence = collected[^1].RunSequence;
        }

        Assert.Equal(totalEvents, collected.Count);

        for (var i = 0; i < collected.Count; i++)
        {
            Assert.Equal(i + 1L, collected[i].RunSequence);
        }
    }

    // -------------------------------------------------------------------------
    // Cursor correctness: no overlap between successive pages
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListByTaskAsync_CursorResume_DoesNotReturnAlreadySeenEvents()
    {
        // First page: events 1-200; second page (partial): events 201-250.
        var firstBody = BuildTaskEventJson("task-cursor", "run-1", Enumerable.Range(1, 200));
        var secondBody = BuildTaskEventJson("task-cursor", "run-1", Enumerable.Range(201, 50));

        var handler = new SequentialResponseHandler([firstBody, secondBody]);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var repository = CreateRepository(factory.Object);

        var firstPage = await repository.ListByTaskAsync("task-cursor", afterSequence: 0, limit: 200);
        Assert.Equal(200, firstPage.Count);
        Assert.Equal(1L, firstPage[0].TaskSequence);
        Assert.Equal(200L, firstPage[^1].TaskSequence);

        var secondPage = await repository.ListByTaskAsync("task-cursor", afterSequence: firstPage[^1].TaskSequence, limit: 200);
        Assert.Equal(50, secondPage.Count);
        Assert.Equal(201L, secondPage[0].TaskSequence);
        Assert.Equal(250L, secondPage[^1].TaskSequence);

        // Zero overlap
        var allSeqs = firstPage.Select(e => e.TaskSequence)
            .Concat(secondPage.Select(e => e.TaskSequence))
            .ToList();
        Assert.Equal(250, allSeqs.Distinct().Count());
    }

    // -------------------------------------------------------------------------
    // Limit clamping
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListByTaskAsync_LimitAboveMax_IsClamped()
    {
        var captured = new List<(string Command, Dictionary<string, object?> Params)>();
        var handler = new CapturingCommandWithParamsHandler(captured);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var repository = CreateRepository(factory.Object);
        await repository.ListByTaskAsync("task-clamp", afterSequence: 0, limit: int.MaxValue);

        var query = Assert.Single(captured, c => c.Command.Contains("WHERE taskId", StringComparison.Ordinal));
        Assert.True(query.Params.TryGetValue("limit", out var limitVal));
        Assert.InRange(Convert.ToInt32(limitVal), 1, 1000);
    }

    [Fact]
    public async Task ListByRunAsync_LimitAboveMax_IsClamped()
    {
        var captured = new List<(string Command, Dictionary<string, object?> Params)>();
        var handler = new CapturingCommandWithParamsHandler(captured);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var repository = CreateRepository(factory.Object);
        await repository.ListByRunAsync("run-clamp", afterSequence: 0, limit: int.MaxValue);

        var query = Assert.Single(captured, c => c.Command.Contains("WHERE runId", StringComparison.Ordinal));
        Assert.True(query.Params.TryGetValue("limit", out var limitVal));
        Assert.InRange(Convert.ToInt32(limitVal), 1, 1000);
    }

    // -------------------------------------------------------------------------
    // Concurrent append – sequence uniqueness
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AppendAsync_ConcurrentMultipleTaskIds_NoSequenceCollisions()
    {
        var recorder = new ConcurrentCommandRecorder();
        using var client = new HttpClient(recorder);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var repository = CreateRepository(factory.Object);

        const int eventsPerTask = 20;
        const int taskCount = 5;

        var appends = Enumerable.Range(0, taskCount)
            .SelectMany(t => Enumerable.Range(0, eventsPerTask)
                .Select(i => repository.AppendAsync(
                    MakeEvent($"e-t{t}-{i}", "run-1", $"task-{t}", "step"))))
            .ToList();

        await Task.WhenAll(appends);

        for (var t = 0; t < taskCount; t++)
        {
            var seqs = recorder.GetTaskSequences($"task-{t}");
            Assert.Equal(eventsPerTask, seqs.Count);
            Assert.Equal(eventsPerTask, seqs.Distinct().Count());
        }
    }

    // -------------------------------------------------------------------------
    // In-memory parsing performance target
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListByTaskAsync_LargeResponseParsing_CompletesWithinPerformanceTarget()
    {
        // Performance target: parsing 1,000 events should complete quickly.
        // CI can be noisier, so allow a slightly higher threshold there.
        const int eventCount = 1_000;
        var responseBody = BuildTaskEventJson("task-perf", "run-perf", Enumerable.Range(1, eventCount));

        var handler = new FixedResponseHandler(responseBody);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var repository = CreateRepository(factory.Object);

        var sw = Stopwatch.StartNew();
        var events = await repository.ListByTaskAsync("task-perf", limit: 1000);
        sw.Stop();

        Assert.Equal(eventCount, events.Count);
        var targetSeconds = IsCiEnvironment() ? 5.0 : 2.0;
        Assert.True(
            sw.Elapsed.TotalSeconds < targetSeconds,
            $"Parsing {eventCount} events took {sw.Elapsed.TotalMilliseconds:F0}ms; target < {targetSeconds * 1000:F0}ms");
    }

    // -------------------------------------------------------------------------
    // Error resilience: failures return empty without throwing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListByTaskAsync_MalformedJsonResponse_ReturnsEmptyWithoutException()
    {
        var handler = new FixedResponseHandler("not-valid-json{{{");
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var result = await CreateRepository(factory.Object).ListByTaskAsync("task-bad");

        Assert.Empty(result);
    }

    [Fact]
    public async Task ListByRunAsync_MalformedJsonResponse_ReturnsEmptyWithoutException()
    {
        var handler = new FixedResponseHandler("not-valid-json{{{");
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var result = await CreateRepository(factory.Object).ListByRunAsync("run-bad");

        Assert.Empty(result);
    }

    [Fact]
    public async Task ListByTaskAsync_HttpServerError_ReturnsEmptyWithoutException()
    {
        var handler = new ErrorResponseHandler(HttpStatusCode.InternalServerError);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var result = await CreateRepository(factory.Object).ListByTaskAsync("task-fail");

        Assert.Empty(result);
    }

    [Fact]
    public async Task ListByRunAsync_HttpServiceUnavailable_ReturnsEmptyWithoutException()
    {
        var handler = new ErrorResponseHandler(HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var result = await CreateRepository(factory.Object).ListByRunAsync("run-fail");

        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ArcadeDbTaskExecutionEventRepository CreateRepository(IHttpClientFactory factory)
    {
        var options = Options.Create(new RuntimeOptions
        {
            ArcadeDbEnabled = true,
            ArcadeDbHttpUrl = "http://arcadedb.test:2480",
            ArcadeDbDatabase = "swarm_assistant",
            ArcadeDbAutoCreateSchema = false
        });

        return new ArcadeDbTaskExecutionEventRepository(
            options,
            factory,
            NullLogger<ArcadeDbTaskExecutionEventRepository>.Instance);
    }

    private static TaskExecutionEvent MakeEvent(string eventId, string runId, string taskId, string eventType) =>
        new(EventId: eventId, RunId: runId, TaskId: taskId, EventType: eventType,
            Payload: null, OccurredAt: DateTimeOffset.UtcNow, TaskSequence: 0, RunSequence: 0);

    private static string BuildTaskEventJson(string taskId, string runId, IEnumerable<int> sequences)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var items = sequences.Select(seq =>
            $$"""{"eventId":"e-{{seq}}","runId":"{{runId}}","taskId":"{{taskId}}","eventType":"step","payload":null,"occurredAt":"{{now}}","taskSequence":{{seq}},"runSequence":{{seq}}}""");
        return $$"""{"result":[{{string.Join(",", items)}}]}""";
    }

    private static bool IsCiEnvironment()
    {
        return string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // HTTP handlers
    // -------------------------------------------------------------------------

    /// <summary>Slices task events by the <c>afterSequence</c> parameter to simulate real pagination.</summary>
    private sealed class PaginatedTaskEventHandler : HttpMessageHandler
    {
        private readonly string _taskId;
        private readonly int _totalEvents;
        private readonly int _pageSize;

        public PaginatedTaskEventHandler(string taskId, int totalEvents, int pageSize)
        {
            _taskId = taskId;
            _totalEvents = totalEvents;
            _pageSize = pageSize;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            long afterSeq = 0;
            if (doc.RootElement.TryGetProperty("params", out var p) &&
                p.TryGetProperty("afterSequence", out var a))
            {
                a.TryGetInt64(out afterSeq);
            }

            var start = (int)afterSeq + 1;
            var count = Math.Min(_pageSize, _totalEvents - start + 1);
            var now = DateTimeOffset.UtcNow.ToString("O");
            var items = count <= 0
                ? Enumerable.Empty<string>()
                : Enumerable.Range(start, count).Select(seq =>
                    $$"""{"eventId":"e-{{seq}}","runId":"run-1","taskId":"{{_taskId}}","eventType":"step","payload":null,"occurredAt":"{{now}}","taskSequence":{{seq}},"runSequence":{{seq}}}""");

            return Ok($$"""{"result":[{{string.Join(",", items)}}]}""");
        }
    }

    /// <summary>Slices run events by the <c>afterSequence</c> parameter to simulate real pagination.</summary>
    private sealed class PaginatedRunEventHandler : HttpMessageHandler
    {
        private readonly string _runId;
        private readonly int _totalEvents;
        private readonly int _pageSize;

        public PaginatedRunEventHandler(string runId, int totalEvents, int pageSize)
        {
            _runId = runId;
            _totalEvents = totalEvents;
            _pageSize = pageSize;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            long afterSeq = 0;
            if (doc.RootElement.TryGetProperty("params", out var p) &&
                p.TryGetProperty("afterSequence", out var a))
            {
                a.TryGetInt64(out afterSeq);
            }

            var start = (int)afterSeq + 1;
            var count = Math.Min(_pageSize, _totalEvents - start + 1);
            var now = DateTimeOffset.UtcNow.ToString("O");
            var items = count <= 0
                ? Enumerable.Empty<string>()
                : Enumerable.Range(start, count).Select(seq =>
                    $$"""{"eventId":"re-{{seq}}","runId":"{{_runId}}","taskId":"task-x","eventType":"step","payload":null,"occurredAt":"{{now}}","taskSequence":{{seq}},"runSequence":{{seq}}}""");

            return Ok($$"""{"result":[{{string.Join(",", items)}}]}""");
        }
    }

    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly string _body;
        public FixedResponseHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Ok(_body));
    }

    private sealed class ErrorResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        public ErrorResponseHandler(HttpStatusCode code) => _code = code;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_code)
            {
                Content = new StringContent("error", Encoding.UTF8, "text/plain")
            });
    }

    private sealed class SequentialResponseHandler : HttpMessageHandler
    {
        private readonly string[] _responses;
        private int _index;
        public SequentialResponseHandler(string[] responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = _index < _responses.Length ? _responses[_index++] : """{"result":[]}""";
            return Task.FromResult(Ok(body));
        }
    }

    private sealed class CapturingCommandWithParamsHandler : HttpMessageHandler
    {
        private readonly List<(string Command, Dictionary<string, object?> Params)> _captured;
        public CapturingCommandWithParamsHandler(List<(string, Dictionary<string, object?>)> captured) =>
            _captured = captured;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var command = doc.RootElement.TryGetProperty("command", out var c) ? c.GetString() ?? string.Empty : string.Empty;
            var @params = new Dictionary<string, object?>(StringComparer.Ordinal);

            if (doc.RootElement.TryGetProperty("params", out var paramsEl))
            {
                foreach (var prop in paramsEl.EnumerateObject())
                {
                    @params[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object?)l : prop.Value.GetDouble(),
                        JsonValueKind.String => prop.Value.GetString(),
                        _ => null
                    };
                }
            }

            _captured.Add((command, @params));
            return Ok("""{"result":[]}""");
        }
    }

    private sealed class ConcurrentCommandRecorder : HttpMessageHandler
    {
        private readonly ConcurrentBag<(string TaskId, long Seq)> _captures = [];

        public List<long> GetTaskSequences(string taskId) =>
            _captures.Where(x => string.Equals(x.TaskId, taskId, StringComparison.Ordinal))
                     .Select(x => x.Seq)
                     .ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is null)
            {
                return OkEmpty();
            }

            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("command", out var cmdEl))
            {
                return OkEmpty();
            }

            var command = cmdEl.GetString() ?? string.Empty;

            if (command.Contains("SELECT max(", StringComparison.Ordinal))
            {
                return Ok("""{"result":[{"maxSequence":0}]}""");
            }

            if (command.Contains("INSERT INTO TaskExecutionEvent", StringComparison.Ordinal) &&
                root.TryGetProperty("params", out var p) &&
                p.TryGetProperty("taskId", out var tidEl) &&
                p.TryGetProperty("taskSequence", out var tseqEl) &&
                tseqEl.TryGetInt64(out var seq))
            {
                _captures.Add((tidEl.GetString() ?? string.Empty, seq));
            }

            return OkEmpty();
        }

        private static HttpResponseMessage OkEmpty() => Ok("""{"result":[]}""");
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
