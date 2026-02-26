using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime.Tasks;

/// <summary>
/// Append-only ArcadeDB store for <see cref="TaskExecutionEvent"/> records.
/// Sequence counters are maintained per-task and per-run using in-process
/// atomic increments, which guarantees no collisions within a single runtime node.
/// </summary>
public sealed class ArcadeDbTaskExecutionEventRepository
    : ITaskExecutionEventWriter, ITaskExecutionEventReader, IDisposable
{
    private const int MaxInMemorySequenceEntries = 10_000;
    private const int SequenceTrimBatchSize = 1_000;
    private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromSeconds(15);

    private static readonly string[] SchemaCommands =
    [
        "CREATE DOCUMENT TYPE TaskExecutionEvent IF NOT EXISTS",
        "CREATE PROPERTY TaskExecutionEvent.eventId IF NOT EXISTS STRING",
        "CREATE PROPERTY TaskExecutionEvent.runId IF NOT EXISTS STRING",
        "CREATE PROPERTY TaskExecutionEvent.taskId IF NOT EXISTS STRING",
        "CREATE PROPERTY TaskExecutionEvent.eventType IF NOT EXISTS STRING",
        "CREATE PROPERTY TaskExecutionEvent.payload IF NOT EXISTS STRING",
        "CREATE PROPERTY TaskExecutionEvent.occurredAt IF NOT EXISTS STRING",
        "CREATE PROPERTY TaskExecutionEvent.taskSequence IF NOT EXISTS LONG",
        "CREATE PROPERTY TaskExecutionEvent.runSequence IF NOT EXISTS LONG",
        "CREATE PROPERTY TaskExecutionEvent.traceId IF NOT EXISTS STRING",
        "CREATE PROPERTY TaskExecutionEvent.spanId IF NOT EXISTS STRING",
        "CREATE INDEX ON TaskExecutionEvent (eventId) UNIQUE IF NOT EXISTS",
        "CREATE INDEX ON TaskExecutionEvent (taskId, taskSequence) IF NOT EXISTS",
        "CREATE INDEX ON TaskExecutionEvent (runId, runSequence) IF NOT EXISTS"
    ];

    private readonly RuntimeOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArcadeDbTaskExecutionEventRepository> _logger;

    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaEnsured;

    // Per-task and per-run sequence counters; long values start at 0 and increment atomically.
    private readonly ConcurrentDictionary<string, long> _taskSequences = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _runSequences = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sequenceLock = new(1, 1);

    private DateTimeOffset _lastErrorLogAt = DateTimeOffset.MinValue;
    private int _consecutiveFailures;

    public ArcadeDbTaskExecutionEventRepository(
        IOptions<RuntimeOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<ArcadeDbTaskExecutionEventRepository> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task AppendAsync(TaskExecutionEvent evt, CancellationToken cancellationToken = default)
    {
        if (!_options.ArcadeDbEnabled)
        {
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("arcadedb");

            if (_options.ArcadeDbAutoCreateSchema)
            {
                await EnsureSchemaAsync(client, cancellationToken);
            }

            // Allocate monotonically increasing sequence numbers.
            // Misses are seeded from persisted max() values to avoid collisions if in-memory entries are trimmed.
            var taskSeq = await NextSequenceAsync(
                client,
                _taskSequences,
                key: evt.TaskId,
                selectorField: "taskId",
                sequenceField: "taskSequence",
                cancellationToken);
            var runSeq = await NextSequenceAsync(
                client,
                _runSequences,
                key: evt.RunId,
                selectorField: "runId",
                sequenceField: "runSequence",
                cancellationToken);

            await ExecuteCommandAsync(
                client,
                "INSERT INTO TaskExecutionEvent SET " +
                "eventId = :eventId, runId = :runId, taskId = :taskId, " +
                "eventType = :eventType, payload = :payload, occurredAt = :occurredAt, " +
                "taskSequence = :taskSequence, runSequence = :runSequence, " +
                "traceId = :traceId, spanId = :spanId",
                new Dictionary<string, object?>
                {
                    ["eventId"] = evt.EventId,
                    ["runId"] = evt.RunId,
                    ["taskId"] = evt.TaskId,
                    ["eventType"] = evt.EventType,
                    ["payload"] = evt.Payload,
                    ["occurredAt"] = evt.OccurredAt.ToString("O"),
                    ["taskSequence"] = taskSeq,
                    ["runSequence"] = runSeq,
                    ["traceId"] = evt.TraceId,
                    ["spanId"] = evt.SpanId
                },
                cancellationToken);

            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
        catch (Exception exception)
        {
            LogArcadeDbFailure(exception, "append");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskExecutionEvent>> ListByTaskAsync(
        string taskId,
        long afterSequence = 0,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (!_options.ArcadeDbEnabled || string.IsNullOrWhiteSpace(taskId))
        {
            return [];
        }

        try
        {
            var client = _httpClientFactory.CreateClient("arcadedb");
            var body = await ExecuteCommandAsync(
                client,
                "SELECT FROM TaskExecutionEvent WHERE taskId = :taskId AND taskSequence > :afterSequence " +
                "ORDER BY taskSequence ASC LIMIT :limit",
                new Dictionary<string, object?>
                {
                    ["taskId"] = taskId,
                    ["afterSequence"] = afterSequence,
                    ["limit"] = Math.Clamp(limit, 1, 1000)
                },
                cancellationToken);

            Interlocked.Exchange(ref _consecutiveFailures, 0);
            return ParseEvents(body);
        }
        catch (Exception exception)
        {
            LogArcadeDbFailure(exception, "list-by-task");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskExecutionEvent>> ListByRunAsync(
        string runId,
        long afterSequence = 0,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (!_options.ArcadeDbEnabled || string.IsNullOrWhiteSpace(runId))
        {
            return [];
        }

        try
        {
            var client = _httpClientFactory.CreateClient("arcadedb");
            var body = await ExecuteCommandAsync(
                client,
                "SELECT FROM TaskExecutionEvent WHERE runId = :runId AND runSequence > :afterSequence " +
                "ORDER BY runSequence ASC LIMIT :limit",
                new Dictionary<string, object?>
                {
                    ["runId"] = runId,
                    ["afterSequence"] = afterSequence,
                    ["limit"] = Math.Clamp(limit, 1, 1000)
                },
                cancellationToken);

            Interlocked.Exchange(ref _consecutiveFailures, 0);
            return ParseEvents(body);
        }
        catch (Exception exception)
        {
            LogArcadeDbFailure(exception, "list-by-run");
            return [];
        }
    }

    private async Task EnsureSchemaAsync(HttpClient client, CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaEnsured)
            {
                return;
            }

            var allSucceeded = true;
            foreach (var command in SchemaCommands)
            {
                allSucceeded &= await TryExecuteSchemaCommandAsync(client, command, cancellationToken);
            }

            _schemaEnsured = allSucceeded;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private async Task<bool> TryExecuteSchemaCommandAsync(
        HttpClient client,
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteCommandAsync(client, command, parameters: null, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "ArcadeDB schema bootstrap command failed command={Command}",
                command);
            return false;
        }
    }

    private async Task<string> ExecuteCommandAsync(
        HttpClient client,
        string command,
        Dictionary<string, object?>? parameters,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpointUrl();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                language = "sql",
                command,
                serializer = "record",
                autoCommit = true,
                @params = parameters
            })
        };

        ApplyBasicAuth(request);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"ArcadeDB command failed status={(int)response.StatusCode} body={body}");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private string BuildEndpointUrl()
    {
        var baseUrl = _options.ArcadeDbHttpUrl.TrimEnd('/');
        var database = Uri.EscapeDataString(_options.ArcadeDbDatabase);
        return $"{baseUrl}/api/v1/command/{database}";
    }

    private void ApplyBasicAuth(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(_options.ArcadeDbUser))
        {
            return;
        }

        if (_options.ArcadeDbUser.Contains(':', StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "ArcadeDbUser contains a colon which is invalid in an HTTP Basic Auth username (RFC 7617). " +
                "Authentication may fail.");
        }

        var credentials = $"{_options.ArcadeDbUser}:{_options.ArcadeDbPassword}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }

    private IReadOnlyList<TaskExecutionEvent> ParseEvents(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var events = new List<TaskExecutionEvent>();
        foreach (var item in result.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var evt = ParseEvent(item);
            if (evt is not null)
            {
                events.Add(evt);
            }
        }

        return events;
    }

    private TaskExecutionEvent? ParseEvent(JsonElement item)
    {
        var eventId = GetString(item, "eventId");
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return null;
        }

        var occurredAtRaw = GetString(item, "occurredAt");
        var occurredAt = ParseTimestamp(occurredAtRaw);
        if (occurredAt is null && !string.IsNullOrWhiteSpace(occurredAtRaw))
        {
            _logger.LogWarning(
                "Failed to parse 'occurredAt' timestamp '{Timestamp}' for event '{EventId}'. Falling back to current time.",
                occurredAtRaw,
                eventId);
        }

        var taskSequence = GetLong(item, "taskSequence");
        var runSequence = GetLong(item, "runSequence");
        var taskId = GetString(item, "taskId") ?? string.Empty;

        return new TaskExecutionEvent(
            EventId: eventId,
            RunId: LegacyRunId.Resolve(GetString(item, "runId"), taskId),
            TaskId: taskId,
            EventType: GetString(item, "eventType") ?? string.Empty,
            Payload: GetString(item, "payload"),
            OccurredAt: occurredAt ?? DateTimeOffset.UtcNow,
            TaskSequence: taskSequence,
            RunSequence: runSequence,
            TraceId: GetString(item, "traceId"),
            SpanId: GetString(item, "spanId"));
    }

    private static string? GetString(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static long GetLong(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var property))
        {
            return 0L;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value))
        {
            return value;
        }

        return long.TryParse(property.ToString(), out var parsed) ? parsed : 0L;
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private async Task<long> NextSequenceAsync(
        HttpClient client,
        ConcurrentDictionary<string, long> cache,
        string key,
        string selectorField,
        string sequenceField,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(key, out _))
        {
            return cache.AddOrUpdate(key, 1L, (_, current) => current + 1L);
        }

        await _sequenceLock.WaitAsync(cancellationToken);
        try
        {
            if (!cache.TryGetValue(key, out _))
            {
                var maxSequence = await GetMaxSequenceAsync(client, selectorField, sequenceField, key, cancellationToken);
                cache[key] = maxSequence;
                TrimSequenceCache(cache);
            }
        }
        finally
        {
            _sequenceLock.Release();
        }

        return cache.AddOrUpdate(key, 1L, (_, current) => current + 1L);
    }

    private async Task<long> GetMaxSequenceAsync(
        HttpClient client,
        string selectorField,
        string sequenceField,
        string selectorValue,
        CancellationToken cancellationToken)
    {
        var response = await ExecuteCommandAsync(
            client,
            $"SELECT max({sequenceField}) AS maxSequence FROM TaskExecutionEvent WHERE {selectorField} = :selectorValue",
            new Dictionary<string, object?>
            {
                ["selectorValue"] = selectorValue
            },
            cancellationToken);

        return ParseMaxSequence(response);
    }

    private static long ParseMaxSequence(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Array ||
            result.GetArrayLength() == 0)
        {
            return 0L;
        }

        var first = result[0];
        return GetLong(first, "maxSequence");
    }

    private static void TrimSequenceCache(ConcurrentDictionary<string, long> cache)
    {
        if (cache.Count <= MaxInMemorySequenceEntries)
        {
            return;
        }

        var toRemove = Math.Min(SequenceTrimBatchSize, cache.Count - MaxInMemorySequenceEntries);
        foreach (var entry in cache)
        {
            if (toRemove-- <= 0)
            {
                break;
            }

            cache.TryRemove(entry.Key, out _);
        }
    }

    private void LogArcadeDbFailure(Exception exception, string operation)
    {
        Interlocked.Increment(ref _consecutiveFailures);
        var now = DateTimeOffset.UtcNow;
        if (now - _lastErrorLogAt < ErrorLogInterval)
        {
            return;
        }

        _lastErrorLogAt = now;
        _logger.LogWarning(
            exception,
            "ArcadeDB event {Operation} failed endpoint={Endpoint} db={Database} consecutiveFailures={ConsecutiveFailures}",
            operation,
            _options.ArcadeDbHttpUrl,
            _options.ArcadeDbDatabase,
            _consecutiveFailures);
    }

    public void Dispose()
    {
        _schemaLock.Dispose();
        _sequenceLock.Dispose();
    }
}
