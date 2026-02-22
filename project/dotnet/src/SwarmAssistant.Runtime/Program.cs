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
builder.Services.AddSingleton<ITaskMemoryReader, ArcadeDbTaskMemoryReader>();
builder.Services.AddSingleton<TaskRegistry>();
builder.Services.AddSingleton<StartupMemoryBootstrapper>();
builder.Services.AddHostedService<Worker>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("DefaultCorsPolicy", policyBuilder =>
    {
        policyBuilder
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors("DefaultCorsPolicy");

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrap");
var options = app.Services.GetRequiredService<IOptions<RuntimeOptions>>().Value;
logger.LogInformation(
    "Starting SwarmAssistant.Runtime with profile={Profile}, orchestration={RoleSystem}, agentExecution={AgentExecution}, agentFrameworkMode={AgentFrameworkExecutionMode}, roleExecutionTimeoutSeconds={RoleExecutionTimeoutSeconds}, cliAdapterOrder={CliAdapterOrder}, sandbox={SandboxMode}, agUiEnabled={AgUiEnabled}, agUiBindUrl={AgUiBindUrl}, agUiProtocolVersion={AgUiProtocolVersion}, a2aEnabled={A2AEnabled}, arcadeDbEnabled={ArcadeDbEnabled}, arcadeDbDatabase={ArcadeDbDatabase}, memoryBootstrapEnabled={MemoryBootstrapEnabled}, memoryBootstrapLimit={MemoryBootstrapLimit}, langfuse={LangfuseBaseUrl}, langfuseTracingEnabled={LangfuseTracingEnabled}, apiKeyProtected={ApiKeyProtected}",
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
    options.MemoryBootstrapEnabled,
    options.MemoryBootstrapLimit,
    options.LangfuseBaseUrl,
    options.LangfuseTracingEnabled,
    !string.IsNullOrWhiteSpace(options.ApiKey));

// When Runtime__ApiKey is set, mutating endpoints require the X-API-Key header.
Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> requireApiKey =
    (ctx, next) =>
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return next(ctx);
        }

        if (ctx.HttpContext.Request.Headers.TryGetValue("X-API-Key", out var provided) &&
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(provided.ToString()),
                System.Text.Encoding.UTF8.GetBytes(options.ApiKey)))
        {
            return next(ctx);
        }

        return ValueTask.FromResult<object?>(Results.Unauthorized());
    };

app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

static object MapTaskSnapshot(TaskSnapshot snapshot)
{
    return new
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
    };
}

static object MapTaskSummary(TaskSnapshot snapshot)
{
    return new
    {
        taskId = snapshot.TaskId,
        title = snapshot.Title,
        status = snapshot.Status.ToString().ToLowerInvariant(),
        updatedAt = snapshot.UpdatedAt,
        error = snapshot.Error
    };
}

static IResult SubmitTask(
    string? requestedTaskId,
    string? title,
    string? description,
    Dictionary<string, object?>? metadata,
    RuntimeActorRegistry actorRegistry,
    TaskRegistry taskRegistry,
    UiEventStream uiEvents,
    string eventType)
{
    if (string.IsNullOrWhiteSpace(title))
    {
        return Results.BadRequest(new { error = "title is required." });
    }

    if (!actorRegistry.TryGetCoordinator(out var coordinator) || coordinator is null)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var taskId = string.IsNullOrWhiteSpace(requestedTaskId)
        ? $"task-{Guid.NewGuid():N}"
        : requestedTaskId;
    var task = new TaskAssigned(
        taskId,
        title.Trim(),
        description?.Trim() ?? string.Empty,
        DateTimeOffset.UtcNow);

    coordinator.Tell(task, ActorRefs.NoSender);
    taskRegistry.Register(task);

    uiEvents.Publish(
        type: eventType,
        taskId: taskId,
        payload: new
        {
            taskId,
            task.Title,
            task.Description,
            metadata
        });

    return Results.Accepted($"/a2a/tasks/{taskId}", new
    {
        taskId,
        status = "accepted",
        statusUrl = $"/a2a/tasks/{taskId}"
    });
}

if (options.AgUiEnabled)
{
    app.MapGet("/memory/tasks", async (
        int? limit,
        ITaskMemoryReader memoryReader,
        TaskRegistry taskRegistry,
        CancellationToken cancellationToken) =>
    {
        var requestedLimit = Math.Clamp(limit ?? 50, 1, 500);
        var memoryTasks = await memoryReader.ListAsync(requestedLimit, cancellationToken);
        var source = memoryTasks.Count > 0 ? "arcadedb" : "registry";
        var snapshots = memoryTasks.Count > 0
            ? memoryTasks
            : taskRegistry.GetTasks(requestedLimit);
        var items = snapshots.Select(MapTaskSnapshot);
        return Results.Ok(new
        {
            source,
            items
        });
    }).AddEndpointFilter(requireApiKey);

    app.MapGet("/memory/tasks/{taskId}", async (
        string taskId,
        ITaskMemoryReader memoryReader,
        TaskRegistry taskRegistry,
        CancellationToken cancellationToken) =>
    {
        var snapshot = await memoryReader.GetAsync(taskId, cancellationToken)
            ?? taskRegistry.GetTask(taskId);
        if (snapshot is null)
        {
            return Results.NotFound(new { error = "task not found", taskId });
        }

        return Results.Ok(MapTaskSnapshot(snapshot));
    }).AddEndpointFilter(requireApiKey);

    app.MapGet("/ag-ui/recent", (int? count, UiEventStream stream) =>
    {
        return Results.Ok(stream.GetRecent(count ?? 50));
    }).AddEndpointFilter(requireApiKey);

    app.MapPost("/ag-ui/actions", async (
        UiActionRequest action,
        UiEventStream stream,
        TaskRegistry taskRegistry,
        RuntimeActorRegistry actorRegistry,
        ITaskMemoryReader memoryReader,
        CancellationToken cancellationToken) =>
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

        switch (action.ActionId.Trim().ToLowerInvariant())
        {
            case "request_snapshot":
                if (!string.IsNullOrWhiteSpace(action.TaskId))
                {
                    var task = taskRegistry.GetTask(action.TaskId);
                    if (task is null)
                    {
                        return Results.NotFound(new { error = "task not found", taskId = action.TaskId });
                    }

                    stream.Publish(
                        type: "agui.task.snapshot",
                        taskId: task.TaskId,
                        payload: new
                        {
                            task = MapTaskSnapshot(task),
                            a2ui = A2UiPayloadFactory.UpdateStatus(task.TaskId, task.Status, task.Error ?? task.Summary)
                        });

                    return Results.Accepted();
                }

                var tasks = taskRegistry.GetTasks(500);
                var statusCounts = tasks
                    .GroupBy(task => task.Status.ToString().ToLowerInvariant())
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

                stream.Publish(
                    type: "agui.runtime.snapshot",
                    taskId: null,
                    payload: new
                    {
                        totalTasks = tasks.Count,
                        statusCounts
                    });
                return Results.Accepted();

            case "load_memory":
                var requestedLimit = UiActionPayload.GetInt(action.Payload, "limit") ?? 50;
                var limit = Math.Clamp(requestedLimit, 1, 500);
                var memoryTasks = await memoryReader.ListAsync(limit, cancellationToken);
                var source = memoryTasks.Count > 0 ? "arcadedb" : "registry";
                var snapshots = memoryTasks.Count > 0
                    ? memoryTasks
                    : taskRegistry.GetTasks(limit);
                var items = snapshots.Select(MapTaskSummary).ToList();

                stream.Publish(
                    type: "agui.memory.tasks",
                    taskId: action.TaskId,
                    payload: new
                    {
                        source,
                        count = items.Count,
                        items
                    });

                return Results.Ok(new
                {
                    source,
                    count = items.Count
                });

            case "refresh_surface":
                if (string.IsNullOrWhiteSpace(action.TaskId))
                {
                    return Results.BadRequest(new { error = "taskId is required for refresh_surface." });
                }

                var targetTask = taskRegistry.GetTask(action.TaskId);
                if (targetTask is null)
                {
                    return Results.NotFound(new { error = "task not found", taskId = action.TaskId });
                }

                stream.Publish(
                    type: "agui.ui.surface",
                    taskId: targetTask.TaskId,
                    payload: new
                    {
                        source = "ag-ui-action",
                        a2ui = A2UiPayloadFactory.CreateSurface(
                            targetTask.TaskId,
                            targetTask.Title,
                            targetTask.Description,
                            targetTask.Status)
                    });
                return Results.Accepted();

            case "submit_task":
                return SubmitTask(
                    requestedTaskId: UiActionPayload.GetString(action.Payload, "taskId"),
                    title: UiActionPayload.GetString(action.Payload, "title"),
                    description: UiActionPayload.GetString(action.Payload, "description"),
                    metadata: action.Payload,
                    actorRegistry,
                    taskRegistry,
                    stream,
                    eventType: "agui.action.task.submitted");

            default:
                return Results.BadRequest(new
                {
                    error = "Unsupported actionId.",
                    actionId = action.ActionId,
                    supported = new[] { "request_snapshot", "refresh_surface", "submit_task", "load_memory" }
                });
        }
    }).AddEndpointFilter(requireApiKey);

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
    }).AddEndpointFilter(requireApiKey);
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
            version = "phase-10",
            protocol = "a2a",
            capabilities = new[] { "task-routing", "status-updates", "ag-ui-events", "ag-ui-actions", "arcadedb-memory" },
            endpoints = new
            {
                agUiEvents = "/ag-ui/events",
                agUiActions = "/ag-ui/actions",
                memoryTasks = "/memory/tasks",
                getMemoryTask = "/memory/tasks/{taskId}",
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
        return SubmitTask(
            requestedTaskId: request.TaskId,
            title: request.Title,
            description: request.Description,
            metadata: request.Metadata,
            actorRegistry,
            taskRegistry,
            uiEvents,
            eventType: "a2a.task.submitted");
    }).AddEndpointFilter(requireApiKey);

    app.MapGet("/a2a/tasks/{taskId}", (string taskId, TaskRegistry taskRegistry) =>
    {
        var snapshot = taskRegistry.GetTask(taskId);
        if (snapshot is null)
        {
            return Results.NotFound(new { error = "task not found", taskId });
        }

        return Results.Ok(MapTaskSnapshot(snapshot));
    }).AddEndpointFilter(requireApiKey);

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
    }).AddEndpointFilter(requireApiKey);
}

app.Run();
