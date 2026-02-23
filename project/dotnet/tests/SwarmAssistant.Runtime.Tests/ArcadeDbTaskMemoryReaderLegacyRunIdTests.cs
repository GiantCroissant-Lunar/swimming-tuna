using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Tasks;

namespace SwarmAssistant.Runtime.Tests;

/// <summary>
/// Tests that the reader layer applies deterministic synthetic run IDs for legacy
/// records that have no runId stored in ArcadeDB.
/// </summary>
public sealed class ArcadeDbTaskMemoryReaderLegacyRunIdTests
{
    [Fact]
    public async Task ListAsync_SnapshotWithNullRunId_SynthesisesLegacyRunId()
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var responseBody = $$"""
        {
          "result": [
            {
              "taskId": "task-legacy-001",
              "title": "Old task",
              "description": "predates runId",
              "status": "done",
              "createdAt": "{{createdAt.ToString("O")}}",
              "updatedAt": "{{createdAt.ToString("O")}}",
              "runId": null
            }
          ]
        }
        """;

        var handler = new FixedResponseHandler(responseBody);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var reader = CreateReader(factory.Object);
        var snapshots = await reader.ListAsync();

        Assert.Single(snapshots);
        Assert.Equal("legacy-task-legacy-001", snapshots[0].RunId);
    }

    [Fact]
    public async Task GetAsync_SnapshotWithEmptyRunId_SynthesisesLegacyRunId()
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var responseBody = $$"""
        {
          "result": [
            {
              "taskId": "task-legacy-002",
              "title": "Another old task",
              "description": "predates runId",
              "status": "running",
              "createdAt": "{{createdAt.ToString("O")}}",
              "updatedAt": "{{createdAt.ToString("O")}}",
              "runId": ""
            }
          ]
        }
        """;

        var handler = new FixedResponseHandler(responseBody);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var reader = CreateReader(factory.Object);
        var snapshot = await reader.GetAsync("task-legacy-002");

        Assert.NotNull(snapshot);
        Assert.Equal("legacy-task-legacy-002", snapshot!.RunId);
    }

    [Fact]
    public async Task ListAsync_SnapshotWithExplicitRunId_ReturnsRunIdUnchanged()
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var responseBody = $$"""
        {
          "result": [
            {
              "taskId": "task-modern-001",
              "title": "Modern task",
              "description": "has explicit runId",
              "status": "done",
              "createdAt": "{{createdAt.ToString("O")}}",
              "updatedAt": "{{createdAt.ToString("O")}}",
              "runId": "run-explicit-99"
            }
          ]
        }
        """;

        var handler = new FixedResponseHandler(responseBody);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var reader = CreateReader(factory.Object);
        var snapshots = await reader.ListAsync();

        Assert.Single(snapshots);
        Assert.Equal("run-explicit-99", snapshots[0].RunId);
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
