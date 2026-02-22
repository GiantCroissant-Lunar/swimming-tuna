using Microsoft.Extensions.Options;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly RuntimeOptions _options;

    public Worker(ILogger<Worker> logger, IOptions<RuntimeOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.HealthHeartbeatSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Heartbeat profile={Profile} roleSystem={RoleSystem} sandbox={SandboxMode} langfuse={LangfuseBaseUrl}",
                _options.Profile,
                _options.RoleSystem,
                _options.SandboxMode,
                _options.LangfuseBaseUrl);

            await Task.Delay(interval, stoppingToken);
        }
    }
}
