using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmAssistant.Runtime.Configuration;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tasks;

/// <summary>
/// Writes task outcomes to ArcadeDB for persistent learning data.
/// </summary>
public sealed class ArcadeDbOutcomeWriter : IOutcomeWriter
{
    internal const char RoleExecutionDelimiter = '|';
    internal const char KeywordDelimiter = ',';

    private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromSeconds(15);

    private readonly RuntimeOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArcadeDbOutcomeWriter> _logger;

    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaEnsured;
    private DateTimeOffset _lastErrorLogAt = DateTimeOffset.MinValue;
    private int _consecutiveFailures;

    public ArcadeDbOutcomeWriter(
        IOptions<RuntimeOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<ArcadeDbOutcomeWriter> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task WriteAsync(TaskOutcome outcome, CancellationToken cancellationToken = default)
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

            var parameters = new Dictionary<string, object?>
            {
                ["taskId"] = outcome.TaskId,
                ["title"] = outcome.Title,
                ["description"] = outcome.Description,
                ["finalStatus"] = outcome.FinalStatus.ToString().ToLowerInvariant(),
                ["createdAt"] = outcome.CreatedAt.ToString("O"),
                ["completedAt"] = outcome.CompletedAt.ToString("O"),
                ["totalDurationMs"] = outcome.TotalDuration.TotalMilliseconds,
                ["titleKeywords"] = outcome.TitleKeywords is { Count: > 0 }
                    ? string.Join(KeywordDelimiter, outcome.TitleKeywords)
                    : null,
                ["descriptionLength"] = outcome.DescriptionLength,
                ["subTaskCount"] = outcome.SubTaskCount,
                ["totalRetries"] = outcome.TotalRetries,
                ["roleExecutions"] = SerializeRoleExecutions(outcome.RoleExecutions),
                ["failedRole"] = outcome.FailedRole?.ToString(),
                ["failureReason"] = outcome.FailureReason,
                ["summary"] = outcome.Summary
            };

            // Atomic UPSERT
            await ExecuteCommandAsync(
                client,
                "UPDATE TaskOutcome SET " +
                "taskId = :taskId, title = :title, description = :description, " +
                "finalStatus = :finalStatus, createdAt = :createdAt, completedAt = :completedAt, " +
                "totalDurationMs = :totalDurationMs, titleKeywords = :titleKeywords, " +
                "descriptionLength = :descriptionLength, subTaskCount = :subTaskCount, " +
                "totalRetries = :totalRetries, roleExecutions = :roleExecutions, " +
                "failedRole = :failedRole, failureReason = :failureReason, summary = :summary " +
                "UPSERT WHERE taskId = :taskId",
                parameters,
                cancellationToken);

            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
        catch (Exception exception)
        {
            LogArcadeDbFailure(exception);
        }
    }

    private static string? SerializeRoleExecutions(IReadOnlyList<RoleExecutionRecord> records)
    {
        if (records is null or { Count: 0 })
        {
            return null;
        }

        // Format: role1|adapter1|retryCount1|succeeded1|confidence1;role2|...
        var parts = records.Select(r =>
            $"{r.Role}" +
            $"{RoleExecutionDelimiter}{r.AdapterUsed ?? ""}" +
            $"{RoleExecutionDelimiter}{r.RetryCount}" +
            $"{RoleExecutionDelimiter}{r.Succeeded}" +
            $"{RoleExecutionDelimiter}{r.Confidence:F2}");

        return string.Join(';', parts);
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
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE DOCUMENT TYPE TaskOutcome IF NOT EXISTS", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE PROPERTY TaskOutcome.taskId IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE PROPERTY TaskOutcome.title IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE PROPERTY TaskOutcome.description IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE PROPERTY TaskOutcome.finalStatus IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE PROPERTY TaskOutcome.createdAt IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE PROPERTY TaskOutcome.completedAt IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE PROPERTY TaskOutcome.totalDurationMs IF NOT EXISTS DOUBLE", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE PROPERTY TaskOutcome.titleKeywords IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE PROPERTY TaskOutcome.descriptionLength IF NOT EXISTS INTEGER", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE PROPERTY TaskOutcome.subTaskCount IF NOT EXISTS INTEGER", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE PROPERTY TaskOutcome.totalRetries IF NOT EXISTS INTEGER", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE PROPERTY TaskOutcome.roleExecutions IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE PROPERTY TaskOutcome.failedRole IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE PROPERTY TaskOutcome.failureReason IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE PROPERTY TaskOutcome.summary IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(
                client, "CREATE INDEX ON TaskOutcome (taskId) UNIQUE IF NOT EXISTS", cancellationToken);

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

    private async Task ExecuteCommandAsync(
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

    private void LogArcadeDbFailure(Exception exception)
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
            "ArcadeDB outcome write failed endpoint={Endpoint} db={Database} consecutiveFailures={ConsecutiveFailures}",
            _options.ArcadeDbHttpUrl,
            _options.ArcadeDbDatabase,
            _consecutiveFailures);
    }
}

/// <summary>
/// Null implementation of outcome writer for when ArcadeDB is disabled.
/// </summary>
public sealed class NullOutcomeWriter : IOutcomeWriter
{
    public Task WriteAsync(TaskOutcome outcome, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
