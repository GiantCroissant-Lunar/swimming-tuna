using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace SwarmAssistant.Runtime.Execution;

internal sealed class AnthropicModelProvider : IModelProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly bool _ownsHttpClient;

    public AnthropicModelProvider(string apiKey, string baseUrl, int requestTimeoutSeconds = 120)
        : this(
            apiKey,
            new HttpClient
            {
                BaseAddress = new Uri(NormalizeBaseUrl(baseUrl)),
                Timeout = TimeSpan.FromSeconds(Math.Max(1, requestTimeoutSeconds))
            },
            ownsHttpClient: true)
    {
    }

    internal AnthropicModelProvider(string apiKey, HttpClient httpClient, bool ownsHttpClient = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("Anthropic API key cannot be empty.", nameof(apiKey));
        }

        ArgumentNullException.ThrowIfNull(httpClient);

        _apiKey = apiKey;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(NormalizeBaseUrl(string.Empty));
        }

        _httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public string ProviderId => "anthropic";

    public async Task<bool> ProbeAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return false;
        }

        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = "claude-haiku-4-5",
                ["max_tokens"] = 1,
                ["messages"] = new[]
                {
                    new Dictionary<string, string>
                    {
                        ["role"] = "user",
                        ["content"] = "hi"
                    }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "messages")
            {
                Content = JsonContent.Create(payload)
            };
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<ModelResponse> ExecuteAsync(
        ModelSpec model,
        string prompt,
        ModelExecutionOptions options,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model.Id,
            ["max_tokens"] = model.Capabilities.MaxOutputTokens,
            ["messages"] = new[]
            {
                new Dictionary<string, string>
                {
                    ["role"] = "user",
                    ["content"] = prompt
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(options.Reasoning))
        {
            var budgetTokens = MapReasoningToBudget(options.Reasoning);
            payload["thinking"] = new Dictionary<string, object?>
            {
                ["type"] = "enabled",
                ["budget_tokens"] = budgetTokens
            };
            payload["max_tokens"] = model.Capabilities.MaxOutputTokens + budgetTokens;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "messages")
        {
            Content = JsonContent.Create(payload)
        };

        var stopwatch = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(request, ct);
        var rawBody = await response.Content.ReadAsStringAsync(ct);
        stopwatch.Stop();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"anthropic provider error ({(int)response.StatusCode}): {rawBody}");
        }

        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;
        var output = ExtractOutput(root);
        var usage = ExtractUsage(root);

        return new ModelResponse
        {
            Output = output,
            Usage = usage,
            ModelId = root.TryGetProperty("model", out var modelElement) ? modelElement.GetString() : model.Id,
            Latency = stopwatch.Elapsed
        };
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "https://api.anthropic.com/v1/";
        }

        return baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : $"{baseUrl}/";
    }

    private static int MapReasoningToBudget(string reasoning)
    {
        return reasoning.ToLowerInvariant() switch
        {
            "low" => 1024,
            "medium" => 4096,
            "high" => 16384,
            "xhigh" => 32768,
            _ => 1024
        };
    }

    private static string ExtractOutput(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("anthropic provider response missing content.");
        }

        var textItems = content.EnumerateArray()
            .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "text")
            .Select(item => item.TryGetProperty("text", out var text) ? text.GetString() : null)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        return string.Join("\n", textItems);
    }

    private static TokenUsage ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return new TokenUsage();
        }

        return new TokenUsage
        {
            InputTokens = GetInt(usage, "input_tokens"),
            OutputTokens = GetInt(usage, "output_tokens"),
            CacheReadTokens = GetInt(usage, "cache_read_input_tokens"),
            CacheWriteTokens = GetInt(usage, "cache_creation_input_tokens")
        };
    }

    private static int GetInt(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var element) && element.TryGetInt32(out var value)
            ? value
            : 0;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
