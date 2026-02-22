using Microsoft.Extensions.Options;
using SwarmAssistant.Runtime;
using SwarmAssistant.Runtime.Configuration;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<RuntimeOptions>()
    .Bind(builder.Configuration.GetSection(RuntimeOptions.SectionName));

builder.Services.AddHostedService<Worker>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrap");
var options = host.Services.GetRequiredService<IOptions<RuntimeOptions>>().Value;
logger.LogInformation(
    "Starting SwarmAssistant.Runtime with profile={Profile}, orchestration={RoleSystem}, agentExecution={AgentExecution}, agentFrameworkMode={AgentFrameworkExecutionMode}, roleExecutionTimeoutSeconds={RoleExecutionTimeoutSeconds}, cliAdapterOrder={CliAdapterOrder}, sandbox={SandboxMode}, langfuse={LangfuseBaseUrl}, langfuseTracingEnabled={LangfuseTracingEnabled}",
    options.Profile,
    options.RoleSystem,
    options.AgentExecution,
    options.AgentFrameworkExecutionMode,
    options.RoleExecutionTimeoutSeconds,
    string.Join(",", options.CliAdapterOrder),
    options.SandboxMode,
    options.LangfuseBaseUrl,
    options.LangfuseTracingEnabled);

host.Run();
