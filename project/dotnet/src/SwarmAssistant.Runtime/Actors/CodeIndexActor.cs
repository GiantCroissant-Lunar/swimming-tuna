using System.Diagnostics;
using Akka.Actor;
using Microsoft.Extensions.Options;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime.Actors;

/// <summary>
/// Actor that provides codebase-aware context to swarm agents.
/// Queries the code-index retrieval API for structurally relevant code chunks.
/// </summary>
public sealed class CodeIndexActor : ReceiveActor
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CodeIndexActor> _logger;
    private readonly string _apiBaseUrl;
    private readonly bool _enabled;

    public CodeIndexActor(
        HttpClient httpClient,
        ILogger<CodeIndexActor> logger,
        IOptions<RuntimeOptions> options)
    {
        _httpClient = httpClient;
        _logger = logger;
        _enabled = options.Value.CodeIndexEnabled;
        _apiBaseUrl = options.Value.CodeIndexUrl?.TrimEnd('/') ?? "http://localhost:8080";

        Receive<CodeIndexQuery>(OnQuery);
        Receive<HealthCheckCodeIndex>(OnHealthCheck);
    }

    /// <summary>
    /// Query the code index for relevant code chunks.
    /// </summary>
    private void OnQuery(CodeIndexQuery query)
    {
        if (!_enabled)
        {
            _logger.LogDebug("Code index is disabled; returning empty results for query: {Query}", query.Query);
            Sender.Tell(new CodeIndexResult(query.Query, Array.Empty<CodeChunkInfo>()));
            return;
        }

        var sender = Sender;

        Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var activity = new Activity("code-index.query").Start();
                activity?.SetTag("query", query.Query);
                activity?.SetTag("top_k", query.TopK);

                var request = new
                {
                    query = query.Query,
                    top_k = query.TopK,
                    languages = query.Languages,
                    node_types = query.NodeTypes,
                    file_path_prefix = query.FilePathPrefix
                };

                var response = await _httpClient.PostAsJsonAsync($"{_apiBaseUrl}/search", request, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Code index query failed: {StatusCode} - {Error}",
                        response.StatusCode, error);
                    return new CodeIndexResult(query.Query, Array.Empty<CodeChunkInfo>(), error);
                }

                var result = await response.Content.ReadFromJsonAsync<CodeIndexApiResponse>();

                if (result?.results == null)
                {
                    return new CodeIndexResult(query.Query, Array.Empty<CodeChunkInfo>());
                }

                var chunks = result.results.Select(r => new CodeChunkInfo(
                    r.chunk.file_path,
                    r.chunk.fully_qualified_name,
                    r.chunk.node_type,
                    r.chunk.language,
                    r.chunk.content,
                    r.chunk.start_line,
                    r.chunk.end_line,
                    r.similarity_score
                )).ToArray();

                activity?.SetTag("results_count", chunks.Length);
                _logger.LogDebug("Code index query '{Query}' returned {Count} results",
                    query.Query, chunks.Length);

                return new CodeIndexResult(query.Query, chunks);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Code index query failed for: {Query}", query.Query);
                return new CodeIndexResult(query.Query, Array.Empty<CodeChunkInfo>(), ex.Message);
            }
        }).PipeTo(sender);
    }

    private void OnHealthCheck(HealthCheckCodeIndex _)
    {
        if (!_enabled)
        {
            Sender.Tell(new CodeIndexHealthResponse(false));
            return;
        }

        var sender = Sender;
        Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var response = await _httpClient.GetAsync($"{_apiBaseUrl}/health", cts.Token);
                return new CodeIndexHealthResponse(response.IsSuccessStatusCode);
            }
            catch
            {
                return new CodeIndexHealthResponse(false);
            }
        }).PipeTo(sender);
    }

    protected override void PostStop()
    {
        _httpClient.Dispose();
        base.PostStop();
    }
}

// Messages

public sealed record CodeIndexQuery(
    string Query,
    int TopK = 10,
    IReadOnlyList<string>? Languages = null,
    IReadOnlyList<string>? NodeTypes = null,
    string? FilePathPrefix = null);

public sealed record CodeChunkInfo(
    string FilePath,
    string FullyQualifiedName,
    string NodeType,
    string Language,
    string Content,
    int StartLine,
    int EndLine,
    float SimilarityScore);

public sealed record CodeIndexResult(
    string Query,
    IReadOnlyList<CodeChunkInfo> Chunks,
    string? Error = null)
{
    public bool IsSuccess => Error == null;
    public bool HasResults => Chunks.Count > 0;
}

public sealed record HealthCheckCodeIndex;
public sealed record CodeIndexHealthResponse(bool IsHealthy);

// API Response Models (JSON deserialization targets — snake_case maps to Python API)
file sealed class CodeIndexApiResponse
{
    public string query { get; init; } = "";
    public List<CodeIndexApiResult> results { get; init; } = new();
    public int total_found { get; init; }
    public float duration_ms { get; init; }
}

file sealed class CodeIndexApiResult
{
    public CodeChunkApiModel chunk { get; init; } = new();
    public float similarity_score { get; init; }
    public int rank { get; init; }
}

file sealed class CodeChunkApiModel
{
    public string? id { get; init; }
    public string file_path { get; init; } = "";
    public string fully_qualified_name { get; init; } = "";
    public string node_type { get; init; } = "";
    public string language { get; init; } = "";
    public string content { get; init; } = "";
    public int start_line { get; init; }
    public int end_line { get; init; }
    public int? token_count { get; init; }
    public int char_count { get; init; }
}
