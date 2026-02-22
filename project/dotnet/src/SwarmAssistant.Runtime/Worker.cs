using Akka.Actor;
using Akka.Configuration;
using Akka.Pattern;
using Microsoft.Extensions.Options;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RuntimeOptions _options;

    private ActorSystem? _actorSystem;
    private IActorRef? _supervisor;

    public Worker(ILogger<Worker> logger, ILoggerFactory loggerFactory, IOptions<RuntimeOptions> options)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = ConfigurationFactory.ParseString(@"
            akka {
              loglevel = INFO
              stdout-loglevel = INFO
              actor { provider = local }
            }");

        _actorSystem = ActorSystem.Create("swarm-assistant-system", config);

        var supervisor = _actorSystem.ActorOf(Props.Create(() => new SupervisorActor(_loggerFactory)), "supervisor");
        var workerActor = _actorSystem.ActorOf(Props.Create(() => new WorkerActor(_options, _loggerFactory)), "worker");
        var reviewerActor = _actorSystem.ActorOf(Props.Create(() => new ReviewerActor(_options, _loggerFactory)), "reviewer");
        var coordinator = _actorSystem.ActorOf(
            Props.Create(() => new CoordinatorActor(workerActor, reviewerActor, supervisor, _loggerFactory)),
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
        }
    }

    private async Task LogSupervisorSnapshot(CancellationToken cancellationToken)
    {
        if (_supervisor is null)
        {
            return;
        }

        var snapshot = await _supervisor.Ask<SupervisorSnapshot>(new GetSupervisorSnapshot(), TimeSpan.FromSeconds(2), cancellationToken);
        _logger.LogInformation(
            "Swarm snapshot started={Started} completed={Completed} failed={Failed} escalations={Escalations}",
            snapshot.Started,
            snapshot.Completed,
            snapshot.Failed,
            snapshot.Escalations);
    }
}
