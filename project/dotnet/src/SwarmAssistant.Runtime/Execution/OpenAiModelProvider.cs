using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SwarmAssistant.Runtime.Execution;

internal sealed class OpenAiModelProvider : IModelProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly bool _ownsHttpClient;

    public OpenAiModelProvider(string apiKey, string baseUrl, int requestTimeoutSeconds = 120)
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

    internal OpenAiModelProvider(string apiKey, HttpClient httpClient, bool ownsHttpClient = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("OpenAI API key cannot be empty.", nameof(apiKey));
        }

        ArgumentNullException.ThrowIfNull(httpClient);

        _apiKey = apiKey;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(NormalizeBaseUrl(string.Empty));
        }

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public string ProviderId => "openai";

    public async Task<bool> ProbeAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "models");
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
            payload["reasoning_effort"] = options.Reasoning;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload)
        };

        var stopwatch = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(request, ct);
        var rawBody = await response.Content.ReadAsStringAsync(ct);
        stopwatch.Stop();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"openai provider error ({(int)response.StatusCode}): {rawBody}");
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
            return "https://api.openai.com/v1/";
        }

        return baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : $"{baseUrl}/";
    }

    private static string ExtractOutput(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("openai provider response missing choices.");
        }

        var firstChoice = choices[0];
        if (!firstChoice.TryGetProperty("message", out var message))
        {
            throw new InvalidOperationException("openai provider response missing message.");
        }

        if (!message.TryGetProperty("content", out var contentElement))
        {
            throw new InvalidOperationException("openai provider response missing content.");
        }

        return contentElement.ValueKind switch
        {
            JsonValueKind.String => contentElement.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(
                "\n",
                contentElement.EnumerateArray()
                    .Select(item => item.TryGetProperty("text", out var text) ? text.GetString() : null)
                    .Where(text => !string.IsNullOrWhiteSpace(text))),
            _ => contentElement.ToString()
        };
    }

    private static TokenUsage ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return new TokenUsage();
        }

        var hasPromptTokenDetails = usage.TryGetProperty("prompt_tokens_details", out var promptTokenDetails);
        return new TokenUsage
        {
            InputTokens = GetInt(usage, "prompt_tokens"),
            OutputTokens = GetInt(usage, "completion_tokens"),
            CacheReadTokens = hasPromptTokenDetails ? GetInt(promptTokenDetails, "cached_tokens") : 0,
            CacheWriteTokens = 0
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
