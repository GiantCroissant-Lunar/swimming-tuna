using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Tasks;

namespace SwarmAssistant.Runtime.Tests;

public sealed class ArcadeDbTaskMemoryReaderArtifactTests
{
    [Fact]
    public async Task GetAsync_WithArtifacts_ParsesArtifactsFromJson()
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var artifactsJson = """
        [
          {
            "artifactId": "art-abc123",
            "runId": "run-1",
            "taskId": "task-1",
            "agentId": "builder-01",
            "type": "file",
            "path": "src/Foo.cs",
            "contentHash": "sha256:abc123",
            "createdAt": "2026-02-24T10:30:00Z",
            "metadata": { "language": "csharp", "linesAdded": "12", "linesRemoved": "0" }
          }
        ]
        """;
        var responseBody = $$"""
        {
          "result": [
            {
              "taskId": "task-1",
              "title": "Artifact task",
              "description": "captures artifacts",
              "status": "done",
              "createdAt": "{{createdAt.ToString("O")}}",
              "updatedAt": "{{createdAt.ToString("O")}}",
              "runId": "run-1",
              "artifacts": {{System.Text.Json.JsonSerializer.Serialize(artifactsJson)}}
            }
          ]
        }
        """;

        var handler = new FixedResponseHandler(responseBody);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var reader = CreateReader(factory.Object);
        var snapshot = await reader.GetAsync("task-1");

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.Artifacts);
        var artifact = Assert.Single(snapshot.Artifacts!);
        Assert.Equal("art-abc123", artifact.ArtifactId);
        Assert.Equal("file", artifact.Type);
        Assert.Equal("src/Foo.cs", artifact.Path);
        Assert.Equal("csharp", artifact.Metadata!["language"]);
    }

    [Fact]
    public async Task GetAsync_WithInvalidArtifactsJson_DoesNotFailSnapshotParsing()
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var responseBody = $$"""
        {
          "result": [
            {
              "taskId": "task-2",
              "title": "Artifact task",
              "description": "captures artifacts",
              "status": "done",
              "createdAt": "{{createdAt.ToString("O")}}",
              "updatedAt": "{{createdAt.ToString("O")}}",
              "runId": "run-2",
              "artifacts": "{not-json}"
            }
          ]
        }
        """;

        var handler = new FixedResponseHandler(responseBody);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var reader = CreateReader(factory.Object);
        var snapshot = await reader.GetAsync("task-2");

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Artifacts);
    }

    private static ArcadeDbTaskMemoryReader CreateReader(IHttpClientFactory factory)
    {
        var options = Options.Create(new RuntimeOptions
        {
            ArcadeDbEnabled = true,
            ArcadeDbHttpUrl = "http://arcadedb.test:2480",
            ArcadeDbDatabase = "swarm_assistant",
            ArcadeDbAutoCreateSchema = false
        });

        return new ArcadeDbTaskMemoryReader(
            options,
            factory,
            NullLogger<ArcadeDbTaskMemoryReader>.Instance);
    }

    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public FixedResponseHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
