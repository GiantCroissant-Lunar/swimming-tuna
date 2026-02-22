using Akka.Actor;
using Akka.Configuration;
using Akka.Pattern;
using Akka.Routing;
using Microsoft.Extensions.Options;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Tasks;
using SwarmAssistant.Runtime.Telemetry;
using SwarmAssistant.Runtime.Ui;

namespace SwarmAssistant.Runtime;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RuntimeOptions _options;
    private readonly UiEventStream _uiEvents;
    private readonly RuntimeActorRegistry _actorRegistry;
    private readonly TaskRegistry _taskRegistry;
    private readonly ITaskMemoryReader _taskMemoryReader;

    private ActorSystem? _actorSystem;
    private IActorRef? _supervisor;
    private RuntimeTelemetry? _telemetry;

    public Worker(
        ILogger<Worker> logger,
        ILoggerFactory loggerFactory,
        IOptions<RuntimeOptions> options,
        UiEventStream uiEvents,
        RuntimeActorRegistry actorRegistry,
        TaskRegistry taskRegistry,
        ITaskMemoryReader taskMemoryReader)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _options = options.Value;
        _uiEvents = uiEvents;
        _actorRegistry = actorRegistry;
        _taskRegistry = taskRegistry;
        _taskMemoryReader = taskMemoryReader;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _telemetry = new RuntimeTelemetry(_options, _loggerFactory);

        var workerPoolSize = Math.Clamp(_options.WorkerPoolSize, 1, 16);
        var reviewerPoolSize = Math.Clamp(_options.ReviewerPoolSize, 1, 16);
        var maxCliConcurrency = Math.Clamp(_options.MaxCliConcurrency, 1, 32);

        using var startupActivity = _telemetry.StartActivity(
            "runtime.startup",
            taskId: null,
            role: null,
            tags: new Dictionary<string, object?>
            {
                ["swarm.orchestration"] = _options.RoleSystem,
                ["swarm.agent_execution"] = _options.AgentExecution,
                ["swarm.agent_framework.mode"] = _options.AgentFrameworkExecutionMode,
                ["swarm.sandbox"] = _options.SandboxMode,
                ["swarm.worker_pool_size"] = workerPoolSize,
                ["swarm.reviewer_pool_size"] = reviewerPoolSize,
                ["swarm.max_cli_concurrency"] = maxCliConcurrency,
            });

        var config = ConfigurationFactory.ParseString(@"
            akka {
              loglevel = INFO
              stdout-loglevel = INFO
              actor { provider = local }
            }");

        _actorSystem = ActorSystem.Create("swarm-assistant-system", config);

        _uiEvents.Publish(
            type: "agui.runtime.started",
            taskId: null,
            payload: new
            {
                _options.Profile,
                _options.AgentFrameworkExecutionMode,
                _options.SandboxMode,
                _options.AgUiProtocolVersion
            });

        var agentFrameworkRoleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, _telemetry);
        var supervisor = _actorSystem.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, _telemetry, _options.CliAdapterOrder)),
            "supervisor");

        var workerActor = _actorSystem.ActorOf(
            Props.Create(() => new WorkerActor(_options, _loggerFactory, agentFrameworkRoleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(workerPoolSize)),
            "worker");
        var reviewerActor = _actorSystem.ActorOf(
            Props.Create(() => new ReviewerActor(_options, _loggerFactory, agentFrameworkRoleEngine, _telemetry))
                .WithRouter(new SmallestMailboxPool(reviewerPoolSize)),
            "reviewer");

        _logger.LogInformation(
            "Actor pools created workerPoolSize={WorkerPoolSize} reviewerPoolSize={ReviewerPoolSize} maxCliConcurrency={MaxCliConcurrency}",
            workerPoolSize,
            reviewerPoolSize,
            maxCliConcurrency);

        var monitorTickSeconds = Math.Max(5, _options.HealthHeartbeatSeconds);
        _actorSystem.ActorOf(
            Props.Create(() => new MonitorActor(
                supervisor,
                _loggerFactory,
                _telemetry,
                monitorTickSeconds)),
            "monitor");

        var blackboardActor = _actorSystem.ActorOf(
            Props.Create(() => new BlackboardActor(_loggerFactory)),
            "blackboard");
        var dispatcher = _actorSystem.ActorOf(
            Props.Create(() => new DispatcherActor(
                workerActor,
                reviewerActor,
                supervisor,
                blackboardActor,
                agentFrameworkRoleEngine,
                _loggerFactory,
                _telemetry,
                _uiEvents,
                _taskRegistry)),
            "dispatcher");
        _actorRegistry.SetDispatcher(dispatcher);

        _supervisor = supervisor;
        await RestoreTaskMemorySnapshots(stoppingToken);

        if (_options.AutoSubmitDemoTask && _taskRegistry.Count == 0)
        {
            var task = new TaskAssigned(
                $"task-{Guid.NewGuid():N}",
                _options.DemoTaskTitle,
                _options.DemoTaskDescription,
                DateTimeOffset.UtcNow);
            dispatcher.Tell(task);

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
            _actorRegistry.ClearDispatcher();

            if (_actorSystem is not null)
            {
                await _actorSystem.Terminate();
                await _actorSystem.WhenTerminated;
            }

            _telemetry?.Dispose();
            _telemetry = null;
        }
    }

    private async Task RestoreTaskMemorySnapshots(CancellationToken cancellationToken)
    {
        if (!_options.MemoryBootstrapEnabled)
        {
            return;
        }

        try
        {
            var limit = Math.Clamp(_options.MemoryBootstrapLimit, 1, 1000);
            var memorySnapshots = await _taskMemoryReader.ListAsync(limit, cancellationToken);
            if (memorySnapshots.Count == 0)
            {
                return;
            }

            var importedCount = _taskRegistry.ImportSnapshots(memorySnapshots);
            if (importedCount == 0)
            {
                return;
            }

            var statusCounts = memorySnapshots
                .GroupBy(task => task.Status.ToString().ToLowerInvariant())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            var summaryItems = memorySnapshots
                .Select(snapshot => new
                {
                    taskId = snapshot.TaskId,
                    title = snapshot.Title,
                    status = snapshot.Status.ToString().ToLowerInvariant(),
                    updatedAt = snapshot.UpdatedAt,
                    error = snapshot.Error
                })
                .ToList();

            _logger.LogInformation(
                "Restored task snapshots from memory imported={ImportedCount} fetched={FetchedCount}",
                importedCount,
                memorySnapshots.Count);

            _uiEvents.Publish(
                type: "agui.memory.bootstrap",
                taskId: null,
                payload: new
                {
                    source = "arcadedb",
                    importedCount,
                    fetchedCount = memorySnapshots.Count,
                    statusCounts
                });

            _uiEvents.Publish(
                type: "agui.memory.tasks",
                taskId: null,
                payload: new
                {
                    source = "arcadedb",
                    count = summaryItems.Count,
                    items = summaryItems
                });

            foreach (var snapshot in memorySnapshots.Take(3))
            {
                _uiEvents.Publish(
                    type: "agui.ui.surface",
                    taskId: snapshot.TaskId,
                    payload: new
                    {
                        source = "memory-bootstrap",
                        a2ui = A2UiPayloadFactory.CreateSurface(
                            snapshot.TaskId,
                            snapshot.Title,
                            snapshot.Description,
                            snapshot.Status)
                    });

                _uiEvents.Publish(
                    type: "agui.ui.patch",
                    taskId: snapshot.TaskId,
                    payload: new
                    {
                        source = "memory-bootstrap",
                        a2ui = A2UiPayloadFactory.UpdateStatus(
                            snapshot.TaskId,
                            snapshot.Status,
                            snapshot.Error ?? snapshot.Summary)
                    });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Memory snapshot restore canceled.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to restore memory snapshots at startup.");
            _uiEvents.Publish(
                type: "agui.memory.bootstrap.failed",
                taskId: null,
                payload: new
                {
                    source = "arcadedb",
                    error = exception.Message
                });
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

        _uiEvents.Publish(
            type: "agui.runtime.snapshot",
            taskId: null,
            payload: new
            {
                snapshot.Started,
                snapshot.Completed,
                snapshot.Failed,
                snapshot.Escalations
            });
    }
}
