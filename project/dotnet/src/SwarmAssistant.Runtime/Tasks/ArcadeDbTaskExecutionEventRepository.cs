using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime.Tasks;

/// <summary>
/// Append-only ArcadeDB store for <see cref="TaskExecutionEvent"/> records.
/// Sequence counters are maintained per-task and per-run using in-process
/// atomic increments, which guarantees no collisions within a single runtime node.
/// </summary>
public sealed class ArcadeDbTaskExecutionEventRepository
    : ITaskExecutionEventWriter, ITaskExecutionEventReader
{
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

            // Allocate monotonically increasing sequence numbers without DB round-trips.
            var taskSeq = _taskSequences.AddOrUpdate(evt.TaskId, 1L, (_, v) => v + 1L);
            var runSeq = _runSequences.AddOrUpdate(evt.RunId, 1L, (_, v) => v + 1L);

            await ExecuteCommandAsync(
                client,
                "INSERT INTO TaskExecutionEvent SET " +
                "eventId = :eventId, runId = :runId, taskId = :taskId, " +
                "eventType = :eventType, payload = :payload, occurredAt = :occurredAt, " +
                "taskSequence = :taskSequence, runSequence = :runSequence",
                new Dictionary<string, object?>
                {
                    ["eventId"] = evt.EventId,
                    ["runId"] = evt.RunId,
                    ["taskId"] = evt.TaskId,
                    ["eventType"] = evt.EventType,
                    ["payload"] = evt.Payload,
                    ["occurredAt"] = evt.OccurredAt.ToString("O"),
                    ["taskSequence"] = taskSeq,
                    ["runSequence"] = runSeq
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
                "SELECT FROM TaskExecutionEvent WHERE taskId = :taskId " +
                "ORDER BY taskSequence ASC LIMIT :limit",
                new Dictionary<string, object?>
                {
                    ["taskId"] = taskId,
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
                "SELECT FROM TaskExecutionEvent WHERE runId = :runId " +
                "ORDER BY runSequence ASC LIMIT :limit",
                new Dictionary<string, object?>
                {
                    ["runId"] = runId,
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

    private static IReadOnlyList<TaskExecutionEvent> ParseEvents(string json)
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

    private static TaskExecutionEvent? ParseEvent(JsonElement item)
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
            // Value was present but unparseable – fall back to now so the record is not lost,
            // but the caller can detect data quality issues via the timestamp being suspiciously recent.
        }

        var taskSequence = GetLong(item, "taskSequence");
        var runSequence = GetLong(item, "runSequence");

        return new TaskExecutionEvent(
            EventId: eventId,
            RunId: GetString(item, "runId") ?? string.Empty,
            TaskId: GetString(item, "taskId") ?? string.Empty,
            EventType: GetString(item, "eventType") ?? string.Empty,
            Payload: GetString(item, "payload"),
            OccurredAt: occurredAt ?? DateTimeOffset.UtcNow,
            TaskSequence: taskSequence,
            RunSequence: runSequence);
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
}
