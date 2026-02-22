using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Options;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime.Tasks;

public sealed class ArcadeDbTaskMemoryWriter : ITaskMemoryWriter
{
    private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromSeconds(15);

    private readonly RuntimeOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArcadeDbTaskMemoryWriter> _logger;

    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaEnsured;
    private DateTimeOffset _lastErrorLogAt = DateTimeOffset.MinValue;
    private int _consecutiveFailures;

    public ArcadeDbTaskMemoryWriter(
        IOptions<RuntimeOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<ArcadeDbTaskMemoryWriter> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task WriteAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default)
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
                ["taskId"] = snapshot.TaskId,
                ["title"] = snapshot.Title,
                ["description"] = snapshot.Description,
                ["status"] = snapshot.Status.ToString().ToLowerInvariant(),
                ["createdAt"] = snapshot.CreatedAt.ToString("O"),
                ["updatedAt"] = snapshot.UpdatedAt.ToString("O"),
                ["planningOutput"] = snapshot.PlanningOutput,
                ["buildOutput"] = snapshot.BuildOutput,
                ["reviewOutput"] = snapshot.ReviewOutput,
                ["summary"] = snapshot.Summary,
                ["taskError"] = snapshot.Error
            };

            // Atomic UPSERT: updates the existing record or inserts a new one in a single statement.
            await ExecuteCommandAsync(
                client,
                "UPDATE SwarmTask SET " +
                "taskId = :taskId, title = :title, description = :description, status = :status, " +
                "createdAt = :createdAt, updatedAt = :updatedAt, " +
                "planningOutput = :planningOutput, buildOutput = :buildOutput, reviewOutput = :reviewOutput, " +
                "summary = :summary, taskError = :taskError " +
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

            // Schema bootstrap: all commands use IF NOT EXISTS so retrying is safe.
            // Only mark as ensured when every command succeeds; transient failures allow retry on next write.
            var allSucceeded = true;
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE DOCUMENT TYPE SwarmTask IF NOT EXISTS", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmTask.taskId IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmTask.title IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmTask.description IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmTask.status IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmTask.createdAt IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmTask.updatedAt IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmTask.planningOutput IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmTask.buildOutput IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmTask.reviewOutput IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmTask.summary IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE PROPERTY SwarmTask.taskError IF NOT EXISTS STRING", cancellationToken);
            allSucceeded &= await TryExecuteSchemaCommandAsync(client, "CREATE INDEX ON SwarmTask (taskId) UNIQUE IF NOT EXISTS", cancellationToken);

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

        // RFC 7617: the username must not contain a colon.
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
            "ArcadeDB write failed endpoint={Endpoint} db={Database} consecutiveFailures={ConsecutiveFailures}",
            _options.ArcadeDbHttpUrl,
            _options.ArcadeDbDatabase,
            _consecutiveFailures);
    }
}
