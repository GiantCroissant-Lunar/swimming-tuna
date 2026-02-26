using System.Text;
using System.Text.Json;

namespace SwarmAssistant.Runtime.Langfuse;

public sealed class HttpLangfuseApiClient : ILangfuseApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IHttpClientFactory _httpClientFactory;

    public HttpLangfuseApiClient(
        IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task PostScoreAsync(LangfuseScore score, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(score, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var client = _httpClientFactory.CreateClient("langfuse");
        var response = await client.PostAsync("/api/public/scores", content, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<LangfuseTraceList> GetTracesAsync(LangfuseTraceQuery query, CancellationToken ct)
    {
        var queryString = BuildTracesQueryString(query);
        var client = _httpClientFactory.CreateClient("langfuse");
        var response = await client.GetAsync($"/api/public/traces{queryString}", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<LangfuseTraceList>(json, JsonOptions);

        return result ?? new LangfuseTraceList(Array.Empty<LangfuseTrace>());
    }

    public async Task PostCommentAsync(LangfuseComment comment, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(comment, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var client = _httpClientFactory.CreateClient("langfuse");
        var response = await client.PostAsync("/api/public/comments", content, ct);
        response.EnsureSuccessStatusCode();
    }

    private static string BuildTracesQueryString(LangfuseTraceQuery query)
    {
        var parameters = new List<string>();

        if (!string.IsNullOrEmpty(query.Tags))
        {
            parameters.Add($"tags={Uri.EscapeDataString(query.Tags)}");
        }

        parameters.Add($"limit={query.Limit}");

        return parameters.Count > 0 ? "?" + string.Join("&", parameters) : string.Empty;
    }
}
