using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tasks;

/// <summary>
/// Reads task outcomes from ArcadeDB for learning and adaptation.
/// </summary>
public sealed class ArcadeDbOutcomeReader : IOutcomeReader
{
    private readonly RuntimeOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArcadeDbOutcomeReader> _logger;

    public ArcadeDbOutcomeReader(
        IOptions<RuntimeOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<ArcadeDbOutcomeReader> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TaskOutcome>> FindSimilarAsync(
        IReadOnlyList<string> keywords,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!_options.ArcadeDbEnabled || keywords is { Count: 0 })
        {
            return Array.Empty<TaskOutcome>();
        }

        try
        {
            var client = _httpClientFactory.CreateClient("arcadedb");

            // Build a query to find outcomes with matching keywords
            // Uses CONTAINSANY for array matching or LIKE for string matching
            var keywordPattern = string.Join("|", keywords.Select(k => k.ToLowerInvariant()));
            var command = $@"
                SELECT FROM TaskOutcome
                WHERE titleKeywords IS NOT NULL
                AND (
                    titleKeywords.toLowerCase() LIKE '%{string.Join("%' OR titleKeywords.toLowerCase() LIKE '%", keywords.Select(EscapeSqlString))}%'
                )
                ORDER BY completedAt DESC
                LIMIT {Math.Min(limit, 100)}";

            var results = await ExecuteQueryAsync<TaskOutcomeRecord>(client, command, cancellationToken);
            return results.Select(MapToOutcome).ToList();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to query similar outcomes keywords={Keywords}",
                string.Join(", ", keywords));
            return Array.Empty<TaskOutcome>();
        }
    }

    public async Task<TaskOutcome?> GetAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.ArcadeDbEnabled)
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("arcadedb");
            var command = $"SELECT FROM TaskOutcome WHERE taskId = :taskId LIMIT 1";
            var parameters = new Dictionary<string, object?> { ["taskId"] = taskId };

            var results = await ExecuteQueryAsync<TaskOutcomeRecord>(client, command, cancellationToken, parameters);
            return results.FirstOrDefault() is { } record ? MapToOutcome(record) : null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to get outcome taskId={TaskId}",
                taskId);
            return null;
        }
    }

    private async Task<IReadOnlyList<T>> ExecuteQueryAsync<T>(
        HttpClient client,
        string command,
        CancellationToken cancellationToken,
        Dictionary<string, object?>? parameters = null)
    {
        var endpoint = BuildEndpointUrl();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                language = "sql",
                command,
                serializer = "record",
                @params = parameters
            })
        };

        ApplyBasicAuth(request);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"ArcadeDB query failed status={(int)response.StatusCode} body={body}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseQueryResult<T>(json);
    }

    private IReadOnlyList<T> ParseQueryResult<T>(string json)
    {
        var results = new List<T>();
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.TryGetProperty("result", out var resultArray) &&
            resultArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in resultArray.EnumerateArray())
            {
                try
                {
                    var record = JsonSerializer.Deserialize<T>(
                        item.GetRawText(),
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                    if (record is not null)
                    {
                        results.Add(record);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping malformed record from ArcadeDB result");
                }
            }
        }

        return results;
    }

    private static TaskOutcome MapToOutcome(TaskOutcomeRecord record)
    {
        return new TaskOutcome
        {
            TaskId = record.TaskId ?? string.Empty,
            Title = record.Title ?? string.Empty,
            Description = record.Description,
            FinalStatus = Enum.TryParse<TaskState>(record.FinalStatus, ignoreCase: true, out var status)
                ? status
                : TaskState.Blocked,
            CreatedAt = DateTimeOffset.TryParse(record.CreatedAt, out var createdAt)
                ? createdAt
                : DateTimeOffset.MinValue,
            CompletedAt = DateTimeOffset.TryParse(record.CompletedAt, out var completedAt)
                ? completedAt
                : DateTimeOffset.UtcNow,
            TitleKeywords = ParseKeywordList(record.TitleKeywords),
            DescriptionLength = record.DescriptionLength,
            SubTaskCount = record.SubTaskCount,
            RoleExecutions = ParseRoleExecutions(record.RoleExecutions, record.TaskId ?? string.Empty),
            FailedRole = Enum.TryParse<SwarmRole>(record.FailedRole, ignoreCase: true, out var role)
                ? role
                : null,
            FailureReason = record.FailureReason,
            Summary = record.Summary
        };
    }

    private static IReadOnlyList<string> ParseKeywordList(string? keywords)
    {
        if (string.IsNullOrWhiteSpace(keywords))
        {
            return Array.Empty<string>();
        }

        return keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IReadOnlyList<RoleExecutionRecord> ParseRoleExecutions(string? executions, string taskId)
    {
        if (string.IsNullOrWhiteSpace(executions))
        {
            return Array.Empty<RoleExecutionRecord>();
        }

        var records = new List<RoleExecutionRecord>();
        var parts = executions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var fields = part.Split('|');
            if (fields.Length >= 5 &&
                Enum.TryParse<SwarmRole>(fields[0], ignoreCase: true, out var role))
            {
                records.Add(new RoleExecutionRecord
                {
                    TaskId = taskId,
                    Role = role,
                    AdapterUsed = string.IsNullOrEmpty(fields[1]) ? null : fields[1],
                    RetryCount = int.TryParse(fields[2], out var retries) ? retries : 0,
                    Succeeded = bool.TryParse(fields[3], out var succeeded) && succeeded,
                    Confidence = double.TryParse(fields[4], out var confidence) ? confidence : 1.0
                });
            }
        }

        return records;
    }

    private static string EscapeSqlString(string value)
    {
        return value.Replace("'", "''");
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

    /// <summary>
    /// Internal record for deserializing ArcadeDB query results.
    /// </summary>
    private sealed class TaskOutcomeRecord
    {
        public string? TaskId { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? FinalStatus { get; set; }
        public string? CreatedAt { get; set; }
        public string? CompletedAt { get; set; }
        public double TotalDurationMs { get; set; }
        public string? TitleKeywords { get; set; }
        public int DescriptionLength { get; set; }
        public int SubTaskCount { get; set; }
        public int TotalRetries { get; set; }
        public string? RoleExecutions { get; set; }
        public string? FailedRole { get; set; }
        public string? FailureReason { get; set; }
        public string? Summary { get; set; }
    }
}

/// <summary>
/// Null implementation of outcome reader for when ArcadeDB is disabled.
/// </summary>
public sealed class NullOutcomeReader : IOutcomeReader
{
    public Task<IReadOnlyList<TaskOutcome>> FindSimilarAsync(
        IReadOnlyList<string> keywords,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<TaskOutcome>>(Array.Empty<TaskOutcome>());
    }

    public Task<TaskOutcome?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<TaskOutcome?>(null);
    }
}
