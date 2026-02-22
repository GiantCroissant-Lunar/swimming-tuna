using System.Text.Json;
using Microsoft.Extensions.Options;
using SwarmAssistant.Runtime;
using SwarmAssistant.Runtime.Configuration;
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
builder.Services.AddHostedService<Worker>();

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrap");
var options = app.Services.GetRequiredService<IOptions<RuntimeOptions>>().Value;
logger.LogInformation(
    "Starting SwarmAssistant.Runtime with profile={Profile}, orchestration={RoleSystem}, agentExecution={AgentExecution}, agentFrameworkMode={AgentFrameworkExecutionMode}, roleExecutionTimeoutSeconds={RoleExecutionTimeoutSeconds}, cliAdapterOrder={CliAdapterOrder}, sandbox={SandboxMode}, agUiEnabled={AgUiEnabled}, agUiBindUrl={AgUiBindUrl}, agUiProtocolVersion={AgUiProtocolVersion}, a2aEnabled={A2AEnabled}, arcadeDbEnabled={ArcadeDbEnabled}, langfuse={LangfuseBaseUrl}, langfuseTracingEnabled={LangfuseTracingEnabled}",
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
            version = "phase-6",
            protocol = "a2a",
            capabilities = new[] { "task-routing", "status-updates" },
            endpoints = new
            {
                agUiEvents = "/ag-ui/events",
                agUiActions = "/ag-ui/actions"
            }
        });
    });
}

app.Run();
