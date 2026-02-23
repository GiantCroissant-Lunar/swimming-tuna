using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Tasks;

namespace SwarmAssistant.Runtime.Tests;

public sealed class ArcadeDbSwarmRunRepositoryTests
{
    [Fact]
    public async Task UpsertAsync_AutoCreateSchema_EnsuresSchemaOnlyOnce()
    {
        var recorder = new CommandRecorderHandler();
        using var client = new HttpClient(recorder);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var repository = CreateRepository(factory.Object, autoCreateSchema: true);
        var run = new SwarmRun(
            RunId: "run-1",
            TaskId: "task-1",
            Role: "planner",
            Adapter: "copilot",
            Status: "done",
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt: DateTimeOffset.UtcNow,
            Output: "ok");

        await repository.UpsertAsync(run);
        await repository.UpsertAsync(run with { UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(10) });

        Assert.Equal(1, recorder.CountCommand("CREATE DOCUMENT TYPE SwarmRun IF NOT EXISTS"));
        Assert.Equal(2, recorder.CountCommand("UPDATE SwarmRun SET runId = :runId, taskId = :taskId, role = :role, adapter = :adapter, status = :status, createdAt = :createdAt, updatedAt = :updatedAt, output = :output, runError = :runError UPSERT WHERE runId = :runId"));
    }

    [Fact]
    public async Task ListAndGetAsync_ReturnPersistedRuns()
    {
        var updatedAt = DateTimeOffset.UtcNow;
        var responseBody = $$"""
        {
          "result": [
            {
              "runId": "run-123",
              "taskId": "task-abc",
              "role": "builder",
              "adapter": "copilot",
              "status": "running",
              "createdAt": "{{updatedAt.AddMinutes(-3).ToString("O")}}",
              "updatedAt": "{{updatedAt.ToString("O")}}",
              "output": "building",
              "runError": null
            }
          ]
        }
        """;

        var handler = new FixedResponseHandler(responseBody);
        using var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("arcadedb")).Returns(client);

        var repository = CreateRepository(factory.Object, autoCreateSchema: false);

        var list = await repository.ListAsync();
        var run = await repository.GetAsync("run-123");

        Assert.Single(list);
        Assert.Equal("run-123", list[0].RunId);
        Assert.Equal("task-abc", list[0].TaskId);
        Assert.NotNull(run);
        Assert.Equal("builder", run!.Role);
        Assert.Equal("running", run.Status);
    }

    private static ArcadeDbSwarmRunRepository CreateRepository(IHttpClientFactory factory, bool autoCreateSchema)
    {
        var options = Options.Create(new RuntimeOptions
        {
            ArcadeDbEnabled = true,
            ArcadeDbHttpUrl = "http://arcadedb.test:2480",
            ArcadeDbDatabase = "swarm_assistant",
            ArcadeDbAutoCreateSchema = autoCreateSchema
        });

        return new ArcadeDbSwarmRunRepository(
            options,
            factory,
            NullLogger<ArcadeDbSwarmRunRepository>.Instance);
    }

    private sealed class CommandRecorderHandler : HttpMessageHandler
    {
        private readonly List<string> _commands = [];

        public int CountCommand(string command)
        {
            return _commands.Count(saved => string.Equals(saved, command, StringComparison.Ordinal));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var command = document.RootElement.GetProperty("command").GetString();
            if (!string.IsNullOrWhiteSpace(command))
            {
                _commands.Add(command);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":[]}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public FixedResponseHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
