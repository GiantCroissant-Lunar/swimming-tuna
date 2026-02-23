using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime.Tasks;

public sealed class ArcadeDbSwarmRunRepository : ISwarmRunWriter, ISwarmRunReader
{
    private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromSeconds(15);

    private readonly RuntimeOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArcadeDbSwarmRunRepository> _logger;

    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaEnsured;
    private DateTimeOffset _lastErrorLogAt = DateTimeOffset.MinValue;
    private int _consecutiveFailures;

    public ArcadeDbSwarmRunRepository(
        IOptions<RuntimeOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<ArcadeDbSwarmRunRepository> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task UpsertAsync(SwarmRun run, CancellationToken cancellationToken = default)
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

            await ExecuteCommandAsync(
                client,
                "UPDATE SwarmRun SET " +
                "runId = :runId, taskId = :taskId, role = :role, adapter = :adapter, status = :status, " +
                "createdAt = :createdAt, updatedAt = :updatedAt, output = :output, runError = :runError " +
                "UPSERT WHERE runId = :runId",
                new Dictionary<string, object?>
                {
                    ["runId"] = run.RunId,
                    ["taskId"] = run.TaskId,
                    ["role"] = run.Role,
                    ["adapter"] = run.Adapter,
                    ["status"] = run.Status,
                    ["createdAt"] = run.CreatedAt.ToString("O"),
                    ["updatedAt"] = run.UpdatedAt.ToString("O"),
                    ["output"] = run.Output,
                    ["runError"] = run.Error
                },
                cancellationToken);

            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
        catch (Exception exception)
        {
            LogArcadeDbFailure(exception, "upsert");
        }
    }

    public async Task<IReadOnlyList<SwarmRun>> ListAsync(int limit = 50, CancellationToken cancellationToken = default)
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
                "SELECT FROM SwarmRun ORDER BY updatedAt DESC LIMIT :limit",
                new Dictionary<string, object?>
                {
                    ["limit"] = Math.Clamp(limit, 1, 500)
                },
                cancellationToken);

            Interlocked.Exchange(ref _consecutiveFailures, 0);
            return ParseRuns(body);
        }
        catch (Exception exception)
        {
            LogArcadeDbFailure(exception, "list");
            return [];
        }
    }

    public async Task<SwarmRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!_options.ArcadeDbEnabled || string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("arcadedb");
            var body = await ExecuteCommandAsync(
                client,
                "SELECT FROM SwarmRun WHERE runId = :runId LIMIT 1",
                new Dictionary<string, object?>
                {
                    ["runId"] = runId
                },
                cancellationToken);

            Interlocked.Exchange(ref _consecutiveFailures, 0);
            return ParseRuns(body).FirstOrDefault();
        }
        catch (Exception exception)
        {
            LogArcadeDbFailure(exception, "get");
            return null;
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
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE DOCUMENT TYPE SwarmRun IF NOT EXISTS", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmRun.runId IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmRun.taskId IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmRun.role IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmRun.adapter IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmRun.status IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmRun.createdAt IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmRun.updatedAt IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmRun.output IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmRun.runError IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE INDEX ON SwarmRun (runId) UNIQUE IF NOT EXISTS", cancellationToken);

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
            _logger.LogDebug(exception, "ArcadeDB schema bootstrap command failed command={Command}", command);
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

    private static IReadOnlyList<SwarmRun> ParseRuns(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var runs = new List<SwarmRun>();
        foreach (var item in result.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var run = ParseRun(item);
            if (run is not null)
            {
                runs.Add(run);
            }
        }

        return runs;
    }

    private static SwarmRun? ParseRun(JsonElement item)
    {
        var runId = GetString(item, "runId");
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        var createdAt = ParseTimestamp(GetString(item, "createdAt"));
        var updatedAt = ParseTimestamp(GetString(item, "updatedAt"));

        return new SwarmRun(
            RunId: runId,
            TaskId: GetString(item, "taskId") ?? string.Empty,
            Role: GetString(item, "role") ?? string.Empty,
            Adapter: GetString(item, "adapter"),
            Status: GetString(item, "status") ?? "queued",
            CreatedAt: createdAt ?? DateTimeOffset.UtcNow,
            UpdatedAt: updatedAt ?? createdAt ?? DateTimeOffset.UtcNow,
            Output: GetString(item, "output"),
            Error: GetString(item, "runError") ?? GetString(item, "error"));
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
            "ArcadeDB run {Operation} failed endpoint={Endpoint} db={Database} consecutiveFailures={ConsecutiveFailures}",
            operation,
            _options.ArcadeDbHttpUrl,
            _options.ArcadeDbDatabase,
            _consecutiveFailures);
    }
}
