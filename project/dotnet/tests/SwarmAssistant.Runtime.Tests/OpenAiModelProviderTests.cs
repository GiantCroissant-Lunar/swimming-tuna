using System.Net;
using System.Net.Http;
using System.Text;
using SwarmAssistant.Runtime.Execution;

namespace SwarmAssistant.Runtime.Tests;

public sealed class OpenAiModelProviderTests
{
    [Fact]
    public async Task ExecuteAsync_ParsesNestedCachedTokens()
    {
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri?.ToString());

            var body = """
                {
                  "model": "gpt-4o-mini",
                  "choices": [
                    { "message": { "content": "ok" } }
                  ],
                  "usage": {
                    "prompt_tokens": 11,
                    "completion_tokens": 7,
                    "prompt_tokens_details": {
                      "cached_tokens": 5
                    }
                  }
                }
                """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };
        using var provider = new OpenAiModelProvider("test-key", client);

        var response = await provider.ExecuteAsync(
            new ModelSpec { Id = "gpt-4o-mini", Provider = "openai", DisplayName = "gpt-4o-mini" },
            prompt: "hello",
            options: new ModelExecutionOptions(),
            ct: CancellationToken.None);

        Assert.Equal(11, response.Usage.InputTokens);
        Assert.Equal(7, response.Usage.OutputTokens);
        Assert.Equal(5, response.Usage.CacheReadTokens);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsFalse_OnUnauthorized()
    {
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.openai.com/v1/models", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };
        using var provider = new OpenAiModelProvider("test-key", client);

        var ok = await provider.ProbeAsync(CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsTrue_OnSuccess()
    {
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.openai.com/v1/models", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };
        using var provider = new OpenAiModelProvider("test-key", client);

        var ok = await provider.ProbeAsync(CancellationToken.None);

        Assert.True(ok);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handler(request, cancellationToken);
        }
    }
}
