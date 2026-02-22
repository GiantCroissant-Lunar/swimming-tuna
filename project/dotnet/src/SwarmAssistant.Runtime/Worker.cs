using Akka.Actor;
using Akka.Configuration;
using Akka.Pattern;
using Microsoft.Extensions.Options;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RuntimeOptions _options;

    private ActorSystem? _actorSystem;
    private IActorRef? _supervisor;
    private RuntimeTelemetry? _telemetry;

    public Worker(ILogger<Worker> logger, ILoggerFactory loggerFactory, IOptions<RuntimeOptions> options)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _telemetry = new RuntimeTelemetry(_options, _loggerFactory);

        using var startupActivity = _telemetry.StartActivity(
            "runtime.startup",
            taskId: null,
            role: null,
            tags: new Dictionary<string, object?>
            {
                ["swarm.orchestration"] = _options.RoleSystem,
                ["swarm.agent_execution"] = _options.AgentExecution,
                ["swarm.sandbox"] = _options.SandboxMode,
            });

        var config = ConfigurationFactory.ParseString(@"
            akka {
              loglevel = INFO
              stdout-loglevel = INFO
              actor { provider = local }
            }");

        _actorSystem = ActorSystem.Create("swarm-assistant-system", config);

        var agentFrameworkRoleEngine = new AgentFrameworkRoleEngine(_loggerFactory, _telemetry);
        var supervisor = _actorSystem.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, _telemetry)),
            "supervisor");
        var workerActor = _actorSystem.ActorOf(
            Props.Create(() => new WorkerActor(_options, _loggerFactory, agentFrameworkRoleEngine, _telemetry)),
            "worker");
        var reviewerActor = _actorSystem.ActorOf(
            Props.Create(() => new ReviewerActor(_options, _loggerFactory, agentFrameworkRoleEngine, _telemetry)),
            "reviewer");
        var coordinator = _actorSystem.ActorOf(
            Props.Create(() => new CoordinatorActor(workerActor, reviewerActor, supervisor, _loggerFactory, _telemetry)),
            "coordinator");

        _supervisor = supervisor;

        if (_options.AutoSubmitDemoTask)
        {
            var task = new TaskAssigned(
                $"task-{Guid.NewGuid():N}",
                _options.DemoTaskTitle,
                _options.DemoTaskDescription,
                DateTimeOffset.UtcNow);
            coordinator.Tell(task);

            _logger.LogInformation("Demo task submitted taskId={TaskId}", task.TaskId);
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.HealthHeartbeatSeconds));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(interval, stoppingToken);
                await LogSupervisorSnapshot(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Cancellation requested, stopping runtime worker.");
        }
        finally
        {
            if (_actorSystem is not null)
            {
                await _actorSystem.Terminate();
                await _actorSystem.WhenTerminated;
            }

            _telemetry?.Dispose();
            _telemetry = null;
        }
    }

    private async Task LogSupervisorSnapshot(CancellationToken cancellationToken)
    {
        if (_supervisor is null)
        {
            return;
        }

        using var snapshotActivity = _telemetry?.StartActivity("runtime.supervisor.snapshot");

        var snapshot = await _supervisor.Ask<SupervisorSnapshot>(new GetSupervisorSnapshot(), TimeSpan.FromSeconds(2), cancellationToken);

        snapshotActivity?.SetTag("swarm.tasks.started", snapshot.Started);
        snapshotActivity?.SetTag("swarm.tasks.completed", snapshot.Completed);
        snapshotActivity?.SetTag("swarm.tasks.failed", snapshot.Failed);
        snapshotActivity?.SetTag("swarm.tasks.escalations", snapshot.Escalations);

        _logger.LogInformation(
            "Swarm snapshot started={Started} completed={Completed} failed={Failed} escalations={Escalations}",
            snapshot.Started,
            snapshot.Completed,
            snapshot.Failed,
            snapshot.Escalations);
    }
}
