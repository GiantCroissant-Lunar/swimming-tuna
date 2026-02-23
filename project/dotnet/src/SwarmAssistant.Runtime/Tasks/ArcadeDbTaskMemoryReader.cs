using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SwarmAssistant.Runtime.Configuration;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tasks;

public sealed class ArcadeDbTaskMemoryReader : ITaskMemoryReader
{
    private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromSeconds(15);

    private readonly RuntimeOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArcadeDbTaskMemoryReader> _logger;

    private DateTimeOffset _lastErrorLogAt = DateTimeOffset.MinValue;

    public ArcadeDbTaskMemoryReader(
        IOptions<RuntimeOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<ArcadeDbTaskMemoryReader> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TaskSnapshot>> ListAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        if (!_options.ArcadeDbEnabled)
        {
            return [];
        }

        try
        {
            var client = _httpClientFactory.CreateClient("arcadedb");
            var body = await ExecuteCommandAsync(
                client,
                "SELECT FROM SwarmTask ORDER BY updatedAt DESC LIMIT :limit",
                new Dictionary<string, object?>
                {
                    ["limit"] = Math.Clamp(limit, 1, 500)
                },
                cancellationToken);

            return ParseSnapshots(body);
        }
        catch (Exception exception)
        {
            LogArcadeDbFailure(exception);
            return [];
        }
    }

    public async Task<TaskSnapshot?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (!_options.ArcadeDbEnabled || string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("arcadedb");
            var body = await ExecuteCommandAsync(
                client,
                "SELECT FROM SwarmTask WHERE taskId = :taskId LIMIT 1",
                new Dictionary<string, object?>
                {
                    ["taskId"] = taskId
                },
                cancellationToken);

            return ParseSnapshots(body).FirstOrDefault();
        }
        catch (Exception exception)
        {
            LogArcadeDbFailure(exception);
            return null;
        }
    }

    public async Task<IReadOnlyList<TaskSnapshot>> ListByRunIdAsync(string runId, int limit = 50, CancellationToken cancellationToken = default)
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
                "SELECT FROM SwarmTask WHERE runId = :runId ORDER BY updatedAt DESC LIMIT :limit",
                new Dictionary<string, object?>
                {
                    ["runId"] = runId,
                    ["limit"] = Math.Clamp(limit, 1, 500)
                },
                cancellationToken);

            return ParseSnapshots(body);
        }
        catch (Exception exception)
        {
            LogArcadeDbFailure(exception);
            return [];
        }
    }

    private async Task<string> ExecuteCommandAsync(
        HttpClient client,
        string command,
        Dictionary<string, object?> parameters,
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
                $"ArcadeDB read command failed status={(int)response.StatusCode} body={body}");
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

        var credentials = $"{_options.ArcadeDbUser}:{_options.ArcadeDbPassword}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }

    private static IReadOnlyList<TaskSnapshot> ParseSnapshots(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var snapshots = new List<TaskSnapshot>();
        foreach (var item in result.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var snapshot = ParseSnapshot(item);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    private static TaskSnapshot? ParseSnapshot(JsonElement item)
    {
        var taskId = GetString(item, "taskId");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        var status = ParseStatus(GetString(item, "status"));
        var createdAt = ParseTimestamp(GetString(item, "createdAt"));
        var updatedAt = ParseTimestamp(GetString(item, "updatedAt"));

        var parentTaskId = GetString(item, "parentTaskId");
        var childTaskIdsRaw = GetString(item, "childTaskIds");
        var childTaskIds = string.IsNullOrWhiteSpace(childTaskIdsRaw)
            ? null
            : (IReadOnlyList<string>)childTaskIdsRaw
                .Split(ArcadeDbTaskMemoryWriter.ChildTaskIdDelimiter,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

        return new TaskSnapshot(
            TaskId: taskId,
            Title: GetString(item, "title") ?? string.Empty,
            Description: GetString(item, "description") ?? string.Empty,
            Status: status,
            CreatedAt: createdAt ?? DateTimeOffset.UtcNow,
            UpdatedAt: updatedAt ?? createdAt ?? DateTimeOffset.UtcNow,
            PlanningOutput: GetString(item, "planningOutput"),
            BuildOutput: GetString(item, "buildOutput"),
            ReviewOutput: GetString(item, "reviewOutput"),
            Summary: GetString(item, "summary"),
            Error: GetString(item, "taskError") ?? GetString(item, "error"),
            ParentTaskId: parentTaskId,
            ChildTaskIds: childTaskIds,
            RunId: GetString(item, "runId"));
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

    private static TaskState ParseStatus(string? status)
    {
        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<TaskState>(status, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return TaskState.Queued;
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

    private void LogArcadeDbFailure(Exception exception)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastErrorLogAt < ErrorLogInterval)
        {
            return;
        }

        _lastErrorLogAt = now;
        _logger.LogWarning(
            exception,
            "ArcadeDB read failed endpoint={Endpoint} db={Database}",
            _options.ArcadeDbHttpUrl,
            _options.ArcadeDbDatabase);
    }
}
