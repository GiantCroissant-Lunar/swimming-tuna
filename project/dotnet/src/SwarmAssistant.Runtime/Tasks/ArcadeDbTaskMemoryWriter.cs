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

            await ExecuteCommandAsync(
                client,
                "UPDATE SwarmTask SET " +
                "title = :title, description = :description, status = :status, " +
                "createdAt = :createdAt, updatedAt = :updatedAt, " +
                "planningOutput = :planningOutput, buildOutput = :buildOutput, reviewOutput = :reviewOutput, " +
                "summary = :summary, error = :error UPSERT WHERE taskId = :taskId",
                new Dictionary<string, object?>
                {
                    ["taskId"] = snapshot.TaskId,
                    ["title"] = snapshot.Title,
                    ["description"] = snapshot.Description,
                    ["status"] = snapshot.Status.ToString().ToLowerInvariant(),
                    ["createdAt"] = snapshot.CreatedAt.UtcDateTime,
                    ["updatedAt"] = snapshot.UpdatedAt.UtcDateTime,
                    ["planningOutput"] = snapshot.PlanningOutput,
                    ["buildOutput"] = snapshot.BuildOutput,
                    ["reviewOutput"] = snapshot.ReviewOutput,
                    ["summary"] = snapshot.Summary,
                    ["error"] = snapshot.Error
                },
                cancellationToken);
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

            // Best-effort schema bootstrap; commands may fail if already present or restricted.
            await ExecuteCommandIgnoringFailureAsync(client, "CREATE DOCUMENT TYPE SwarmTask IF NOT EXISTS", cancellationToken);
            await ExecuteCommandIgnoringFailureAsync(client, "CREATE PROPERTY SwarmTask.taskId IF NOT EXISTS STRING", cancellationToken);
            await ExecuteCommandIgnoringFailureAsync(client, "CREATE PROPERTY SwarmTask.title IF NOT EXISTS STRING", cancellationToken);
            await ExecuteCommandIgnoringFailureAsync(client, "CREATE PROPERTY SwarmTask.description IF NOT EXISTS STRING", cancellationToken);
            await ExecuteCommandIgnoringFailureAsync(client, "CREATE PROPERTY SwarmTask.status IF NOT EXISTS STRING", cancellationToken);
            await ExecuteCommandIgnoringFailureAsync(client, "CREATE PROPERTY SwarmTask.createdAt IF NOT EXISTS DATETIME", cancellationToken);
            await ExecuteCommandIgnoringFailureAsync(client, "CREATE PROPERTY SwarmTask.updatedAt IF NOT EXISTS DATETIME", cancellationToken);
            await ExecuteCommandIgnoringFailureAsync(client, "CREATE PROPERTY SwarmTask.planningOutput IF NOT EXISTS STRING", cancellationToken);
            await ExecuteCommandIgnoringFailureAsync(client, "CREATE PROPERTY SwarmTask.buildOutput IF NOT EXISTS STRING", cancellationToken);
            await ExecuteCommandIgnoringFailureAsync(client, "CREATE PROPERTY SwarmTask.reviewOutput IF NOT EXISTS STRING", cancellationToken);
            await ExecuteCommandIgnoringFailureAsync(client, "CREATE PROPERTY SwarmTask.summary IF NOT EXISTS STRING", cancellationToken);
            await ExecuteCommandIgnoringFailureAsync(client, "CREATE PROPERTY SwarmTask.error IF NOT EXISTS STRING", cancellationToken);
            await ExecuteCommandIgnoringFailureAsync(client, "CREATE INDEX ON SwarmTask (taskId) UNIQUE IF NOT EXISTS", cancellationToken);

            _schemaEnsured = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private async Task ExecuteCommandIgnoringFailureAsync(
        HttpClient client,
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteCommandAsync(client, command, parameters: null, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "ArcadeDB schema bootstrap command failed command={Command}", command);
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

        var credentials = $"{_options.ArcadeDbUser}:{_options.ArcadeDbPassword}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
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
            "ArcadeDB write failed endpoint={Endpoint} db={Database}",
            _options.ArcadeDbHttpUrl,
            _options.ArcadeDbDatabase);
    }
}
