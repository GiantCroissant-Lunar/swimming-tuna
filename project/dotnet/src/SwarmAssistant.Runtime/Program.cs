using System.Text.Json;
using Akka.Actor;
using Microsoft.Extensions.Options;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.A2A;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Tasks;
using SwarmAssistant.Runtime.Ui;

var builder = WebApplication.CreateBuilder(args);

var bootstrapOptions = builder.Configuration
    .GetSection(RuntimeOptions.SectionName)
    .Get<RuntimeOptions>() ?? new RuntimeOptions();

if (bootstrapOptions.AgUiEnabled && !string.IsNullOrWhiteSpace(bootstrapOptions.AgUiBindUrl))
{
    builder.WebHost.UseUrls(bootstrapOptions.AgUiBindUrl);
}

builder.Services.AddOptions<RuntimeOptions>()
    .Bind(builder.Configuration.GetSection(RuntimeOptions.SectionName));
builder.Services.AddSingleton<UiEventStream>();
builder.Services.AddSingleton<RuntimeActorRegistry>();
builder.Services.AddHttpClient("arcadedb");
builder.Services.AddSingleton<ITaskMemoryWriter, ArcadeDbTaskMemoryWriter>();
builder.Services.AddSingleton<TaskRegistry>();
builder.Services.AddHostedService<Worker>();

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrap");
var options = app.Services.GetRequiredService<IOptions<RuntimeOptions>>().Value;
logger.LogInformation(
    "Starting SwarmAssistant.Runtime with profile={Profile}, orchestration={RoleSystem}, agentExecution={AgentExecution}, agentFrameworkMode={AgentFrameworkExecutionMode}, roleExecutionTimeoutSeconds={RoleExecutionTimeoutSeconds}, cliAdapterOrder={CliAdapterOrder}, sandbox={SandboxMode}, agUiEnabled={AgUiEnabled}, agUiBindUrl={AgUiBindUrl}, agUiProtocolVersion={AgUiProtocolVersion}, a2aEnabled={A2AEnabled}, arcadeDbEnabled={ArcadeDbEnabled}, arcadeDbDatabase={ArcadeDbDatabase}, langfuse={LangfuseBaseUrl}, langfuseTracingEnabled={LangfuseTracingEnabled}",
    options.Profile,
    options.RoleSystem,
    options.AgentExecution,
    options.AgentFrameworkExecutionMode,
    options.RoleExecutionTimeoutSeconds,
    string.Join(",", options.CliAdapterOrder),
    options.SandboxMode,
    options.AgUiEnabled,
    options.AgUiBindUrl,
    options.AgUiProtocolVersion,
    options.A2AEnabled,
    options.ArcadeDbEnabled,
    options.ArcadeDbDatabase,
    options.LangfuseBaseUrl,
    options.LangfuseTracingEnabled);

app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

if (options.AgUiEnabled)
{
    app.MapGet("/ag-ui/recent", (int? count, UiEventStream stream) =>
    {
        return Results.Ok(stream.GetRecent(count ?? 50));
    });

    app.MapPost("/ag-ui/actions", (UiActionRequest action, UiEventStream stream) =>
    {
        if (string.IsNullOrWhiteSpace(action.ActionId))
        {
            return Results.BadRequest(new { error = "actionId is required." });
        }

        stream.Publish(
            type: "agui.action.received",
            taskId: action.TaskId,
            payload: new
            {
                action.ActionId,
                action.Payload
            });

        return Results.Accepted();
    });

    app.MapGet("/ag-ui/events", async (HttpContext context, UiEventStream stream, CancellationToken cancellationToken) =>
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        await context.Response.WriteAsync(": ag-ui event stream\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);

        await foreach (var message in stream.Subscribe(cancellationToken))
        {
            var payload = JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await context.Response.WriteAsync($"event: {message.Type}\n", cancellationToken);
            await context.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
    });
}

if (options.A2AEnabled)
{
    var agentCardPath = string.IsNullOrWhiteSpace(options.A2AAgentCardPath)
        ? "/.well-known/agent-card.json"
        : options.A2AAgentCardPath;

    app.MapGet(agentCardPath, () =>
    {
        return Results.Ok(new
        {
            name = "swarm-assistant",
            version = "phase-7",
            protocol = "a2a",
            capabilities = new[] { "task-routing", "status-updates", "ag-ui-events", "arcadedb-memory" },
            endpoints = new
            {
                agUiEvents = "/ag-ui/events",
                agUiActions = "/ag-ui/actions",
                submitTask = "/a2a/tasks",
                getTask = "/a2a/tasks/{taskId}",
                listTasks = "/a2a/tasks"
            }
        });
    });

    app.MapPost("/a2a/tasks", (
        A2aTaskSubmitRequest request,
        RuntimeActorRegistry actorRegistry,
        TaskRegistry taskRegistry,
        UiEventStream uiEvents) =>
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Results.BadRequest(new { error = "title is required." });
        }

        if (!actorRegistry.TryGetCoordinator(out var coordinator) || coordinator is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var taskId = string.IsNullOrWhiteSpace(request.TaskId)
            ? $"task-{Guid.NewGuid():N}"
            : request.TaskId;
        var task = new TaskAssigned(
            taskId,
            request.Title.Trim(),
            request.Description?.Trim() ?? string.Empty,
            DateTimeOffset.UtcNow);

        coordinator.Tell(task, ActorRefs.NoSender);
        taskRegistry.Register(task);

        uiEvents.Publish(
            type: "a2a.task.submitted",
            taskId: taskId,
            payload: new
            {
                taskId,
                task.Title,
                task.Description,
                metadata = request.Metadata
            });

        return Results.Accepted($"/a2a/tasks/{taskId}", new
        {
            taskId,
            status = "accepted",
            statusUrl = $"/a2a/tasks/{taskId}"
        });
    });

    app.MapGet("/a2a/tasks/{taskId}", (string taskId, TaskRegistry taskRegistry) =>
    {
        var snapshot = taskRegistry.GetTask(taskId);
        if (snapshot is null)
        {
            return Results.NotFound(new { error = "task not found", taskId });
        }

        return Results.Ok(new
        {
            taskId = snapshot.TaskId,
            title = snapshot.Title,
            description = snapshot.Description,
            status = snapshot.Status.ToString().ToLowerInvariant(),
            createdAt = snapshot.CreatedAt,
            updatedAt = snapshot.UpdatedAt,
            planningOutput = snapshot.PlanningOutput,
            buildOutput = snapshot.BuildOutput,
            reviewOutput = snapshot.ReviewOutput,
            summary = snapshot.Summary,
            error = snapshot.Error
        });
    });

    app.MapGet("/a2a/tasks", (int? limit, TaskRegistry taskRegistry) =>
    {
        var snapshots = taskRegistry.GetTasks(limit ?? 50);
        var items = snapshots.Select(snapshot => new
        {
            taskId = snapshot.TaskId,
            title = snapshot.Title,
            status = snapshot.Status.ToString().ToLowerInvariant(),
            updatedAt = snapshot.UpdatedAt,
            error = snapshot.Error
        });
        return Results.Ok(items);
    });
}

app.Run();
