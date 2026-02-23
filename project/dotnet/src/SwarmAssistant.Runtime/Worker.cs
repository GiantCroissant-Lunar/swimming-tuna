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
    private static readonly SwarmRole[] AllRoles =
    [
        SwarmRole.Planner,
        SwarmRole.Builder,
        SwarmRole.Reviewer,
        SwarmRole.Orchestrator,
        SwarmRole.Researcher,
        SwarmRole.Debugger,
        SwarmRole.Tester
    ];

    private readonly ILogger<Worker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly RuntimeOptions _options;
    private readonly UiEventStream _uiEvents;
    private readonly RuntimeActorRegistry _actorRegistry;
    private readonly TaskRegistry _taskRegistry;
    private readonly StartupMemoryBootstrapper _startupMemoryBootstrapper;

    private ActorSystem? _actorSystem;
    private IActorRef? _supervisor;
    private IActorRef? _dispatcher;
    private RuntimeTelemetry? _telemetry;
    private int _dynamicAgentCount;
    private int _fixedPoolSize;

    public Worker(
        ILogger<Worker> logger,
        ILoggerFactory loggerFactory,
        IOptions<RuntimeOptions> options,
        UiEventStream uiEvents,
        RuntimeActorRegistry actorRegistry,
        TaskRegistry taskRegistry,
        StartupMemoryBootstrapper startupMemoryBootstrapper)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _options = options.Value;
        _uiEvents = uiEvents;
        _actorRegistry = actorRegistry;
        _taskRegistry = taskRegistry;
        _startupMemoryBootstrapper = startupMemoryBootstrapper;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _telemetry = new RuntimeTelemetry(_options, _loggerFactory);

        var workerPoolSize = Math.Clamp(_options.WorkerPoolSize, 1, 16);
        var reviewerPoolSize = Math.Clamp(_options.ReviewerPoolSize, 1, 16);
        var swarmAgentPoolSize = Math.Clamp(workerPoolSize + reviewerPoolSize, 1, 32);
        var maxCliConcurrency = Math.Clamp(_options.MaxCliConcurrency, 1, 32);
        _fixedPoolSize = swarmAgentPoolSize;
        _dynamicAgentCount = 0;

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
                ["swarm.agent_pool_size"] = swarmAgentPoolSize,
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

        // Create blackboard actor first so supervisor can write stigmergy signals to it
        var blackboardActor = _actorSystem.ActorOf(
            Props.Create(() => new BlackboardActor(_loggerFactory)),
            "blackboard");

        var supervisor = _actorSystem.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, _telemetry, _options.CliAdapterOrder, blackboardActor)),
            "supervisor");

        var capabilityRegistry = _actorSystem.ActorOf(
            Props.Create(() => new CapabilityRegistryActor(_loggerFactory)),
            "capability-registry");

        var capabilities = new[]
        {
            SwarmRole.Planner,
            SwarmRole.Builder,
            SwarmRole.Reviewer,
            SwarmRole.Orchestrator,
            SwarmRole.Researcher,
            SwarmRole.Debugger,
            SwarmRole.Tester
        };

        _ = _actorSystem.ActorOf(
            Props.Create(() => new SwarmAgentActor(
                    _options,
                    _loggerFactory,
                    agentFrameworkRoleEngine,
                    _telemetry,
                    capabilityRegistry,
                    capabilities,
                    default))
                .WithRouter(new SmallestMailboxPool(swarmAgentPoolSize)),
            "swarm-agent");

        _logger.LogInformation(
            "Actor pools created swarmAgentPoolSize={SwarmAgentPoolSize} maxCliConcurrency={MaxCliConcurrency}",
            swarmAgentPoolSize,
            maxCliConcurrency);

        var monitorTickSeconds = Math.Max(5, _options.HealthHeartbeatSeconds);
        _actorSystem.ActorOf(
            Props.Create(() => new MonitorActor(
                supervisor,
                _loggerFactory,
                _telemetry,
                monitorTickSeconds)),
            "monitor");
        var dispatcher = _actorSystem.ActorOf(
            Props.Create(() => new DispatcherActor(
                capabilityRegistry,
                capabilityRegistry,
                supervisor,
                blackboardActor,
                agentFrameworkRoleEngine,
                _loggerFactory,
                _telemetry,
                _uiEvents,
                _taskRegistry,
                _options)),
            "dispatcher");
        _actorRegistry.SetDispatcher(dispatcher);
        _dispatcher = dispatcher;

        _supervisor = supervisor;
        await _startupMemoryBootstrapper.RestoreAsync(
            _options.MemoryBootstrapEnabled,
            _options.MemoryBootstrapLimit,
            stoppingToken);

        if (StartupMemoryBootstrapper.ShouldAutoSubmitDemoTask(_options.AutoSubmitDemoTask, _taskRegistry.Count))
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
            _dispatcher = null;

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

        if (_options.AutoScaleEnabled && _dispatcher is not null)
        {
            var activeTasks = snapshot.Started - snapshot.Completed - snapshot.Failed;
            var totalAgents = _fixedPoolSize + _dynamicAgentCount;
            if (activeTasks > _options.ScaleUpThreshold && totalAgents < _options.MaxPoolSize)
            {
                _dynamicAgentCount++;
                _dispatcher.Tell(new SpawnAgent(AllRoles, TimeSpan.FromMinutes(5)));
                _logger.LogInformation(
                    "Auto-scale up: spawning dynamic agent activeTasks={ActiveTasks} threshold={Threshold} totalAgents={TotalAgents}",
                    activeTasks,
                    _options.ScaleUpThreshold,
                    totalAgents + 1);
            }
        }
    }
}
