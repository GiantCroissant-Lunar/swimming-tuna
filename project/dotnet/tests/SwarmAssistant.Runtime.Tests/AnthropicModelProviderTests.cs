using System.Net;
using System.Net.Http;
using System.Text;
using SwarmAssistant.Runtime.Execution;

namespace SwarmAssistant.Runtime.Tests;

public sealed class AnthropicModelProviderTests
{
    [Fact]
    public async Task ExecuteAsync_SendsCorrectHeaders()
    {
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri?.ToString());
            Assert.True(request.Headers.Contains("x-api-key"));
            Assert.Equal("test-key", request.Headers.GetValues("x-api-key").FirstOrDefault());
            Assert.True(request.Headers.Contains("anthropic-version"));
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").FirstOrDefault());

            var body = """
                {
                  "content": [
                    { "type": "text", "text": "ok" }
                  ],
                  "model": "claude-3-haiku-20240307",
                  "usage": {
                    "input_tokens": 10,
                    "output_tokens": 5
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
            BaseAddress = new Uri("https://api.anthropic.com/v1/")
        };
        using var provider = new AnthropicModelProvider("test-key", client);

        var response = await provider.ExecuteAsync(
            new ModelSpec { Id = "claude-3-haiku-20240307", Provider = "anthropic", DisplayName = "claude-3-haiku-20240307" },
            prompt: "hello",
            options: new ModelExecutionOptions(),
            ct: CancellationToken.None);

        Assert.Equal("ok", response.Output);
    }

    [Fact]
    public async Task ExecuteAsync_SendsCorrectRequestBodyShape()
    {
        using var handler = new StubHttpMessageHandler(async (request, _) =>
        {
            var requestContent = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            Assert.Contains("\"model\":\"claude-3-haiku-20240307\"", requestContent);
            Assert.Contains("\"max_tokens\":8192", requestContent);
            Assert.Contains("\"role\":\"user\"", requestContent);
            Assert.Contains("\"content\":\"hello\"", requestContent);

            var body = """
                {
                  "content": [
                    { "type": "text", "text": "response" }
                  ],
                  "model": "claude-3-haiku-20240307",
                  "usage": {
                    "input_tokens": 5,
                    "output_tokens": 3
                  }
                }
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.anthropic.com/v1/")
        };
        using var provider = new AnthropicModelProvider("test-key", client);

        await provider.ExecuteAsync(
            new ModelSpec { Id = "claude-3-haiku-20240307", Provider = "anthropic", DisplayName = "claude-3-haiku-20240307", Capabilities = new ModelCapabilities { MaxOutputTokens = 8192 } },
            prompt: "hello",
            options: new ModelExecutionOptions(),
            ct: CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_ParsesContentText()
    {
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            var body = """
                {
                  "content": [
                    { "type": "text", "text": "Hello" },
                    { "type": "text", "text": "World" }
                  ],
                  "model": "claude-3-haiku-20240307",
                  "usage": {
                    "input_tokens": 10,
                    "output_tokens": 8
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
            BaseAddress = new Uri("https://api.anthropic.com/v1/")
        };
        using var provider = new AnthropicModelProvider("test-key", client);

        var response = await provider.ExecuteAsync(
            new ModelSpec { Id = "claude-3-haiku-20240307", Provider = "anthropic", DisplayName = "claude-3-haiku-20240307" },
            prompt: "hello",
            options: new ModelExecutionOptions(),
            ct: CancellationToken.None);

        Assert.Equal("Hello\nWorld", response.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ParsesUsageIncludingCacheTokens()
    {
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            var body = """
                {
                  "content": [
                    { "type": "text", "text": "ok" }
                  ],
                  "model": "claude-3-haiku-20240307",
                  "usage": {
                    "input_tokens": 100,
                    "output_tokens": 50,
                    "cache_read_input_tokens": 30,
                    "cache_creation_input_tokens": 20
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
            BaseAddress = new Uri("https://api.anthropic.com/v1/")
        };
        using var provider = new AnthropicModelProvider("test-key", client);

        var response = await provider.ExecuteAsync(
            new ModelSpec { Id = "claude-3-haiku-20240307", Provider = "anthropic", DisplayName = "claude-3-haiku-20240307" },
            prompt: "hello",
            options: new ModelExecutionOptions(),
            ct: CancellationToken.None);

        Assert.Equal(100, response.Usage.InputTokens);
        Assert.Equal(50, response.Usage.OutputTokens);
        Assert.Equal(30, response.Usage.CacheReadTokens);
        Assert.Equal(20, response.Usage.CacheWriteTokens);
    }

    [Fact]
    public async Task ExecuteAsync_WithReasoning_AddsThinkingBlock()
    {
        using var handler = new StubHttpMessageHandler(async (request, _) =>
        {
            var requestContent = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            Assert.Contains("\"thinking\"", requestContent);
            Assert.Contains("\"type\":\"enabled\"", requestContent);
            Assert.Contains("\"budget_tokens\":4096", requestContent);
            Assert.Contains("\"max_tokens\":12288", requestContent);

            var body = """
                {
                  "content": [
                    { "type": "thinking", "thinking": "internal reasoning..." },
                    { "type": "text", "text": "final answer" }
                  ],
                  "model": "claude-3-haiku-20240307",
                  "usage": {
                    "input_tokens": 50,
                    "output_tokens": 100
                  }
                }
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.anthropic.com/v1/")
        };
        using var provider = new AnthropicModelProvider("test-key", client);

        var response = await provider.ExecuteAsync(
            new ModelSpec { Id = "claude-3-haiku-20240307", Provider = "anthropic", DisplayName = "claude-3-haiku-20240307", Capabilities = new ModelCapabilities { MaxOutputTokens = 8192 } },
            prompt: "complex task",
            options: new ModelExecutionOptions { Reasoning = "medium" },
            ct: CancellationToken.None);

        Assert.Equal("final answer", response.Output);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsTrue_OnSuccess()
    {
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.anthropic.com/v1/")
        };
        using var provider = new AnthropicModelProvider("test-key", client);

        var ok = await provider.ProbeAsync(CancellationToken.None);

        Assert.True(ok);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsFalse_OnUnauthorized()
    {
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });

        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.anthropic.com/v1/")
        };
        using var provider = new AnthropicModelProvider("test-key", client);

        var ok = await provider.ProbeAsync(CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyApiKey()
    {
        Assert.Throws<ArgumentException>(() => new AnthropicModelProvider("", "https://api.anthropic.com/v1/"));
        Assert.Throws<ArgumentException>(() => new AnthropicModelProvider("   ", "https://api.anthropic.com/v1/"));
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
