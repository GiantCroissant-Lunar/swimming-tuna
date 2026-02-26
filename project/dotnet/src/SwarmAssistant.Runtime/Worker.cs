using Akka.Actor;
using Akka.Configuration;
using Akka.Routing;
using Microsoft.Extensions.Options;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Agents;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Execution;
using SwarmAssistant.Runtime.Langfuse;
using SwarmAssistant.Runtime.Memvid;
using SwarmAssistant.Runtime.Skills;
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
    private readonly IOptions<RuntimeOptions> _optionsInstance;
    private readonly RuntimeOptions _options;
    private readonly UiEventStream _uiEvents;
    private readonly RuntimeActorRegistry _actorRegistry;
    private readonly TaskRegistry _taskRegistry;
    private readonly StartupMemoryBootstrapper _startupMemoryBootstrapper;
    private readonly OutcomeTracker _outcomeTracker;
    private readonly IOutcomeReader _outcomeReader;
    private readonly ITaskExecutionEventWriter? _eventWriter;
    private readonly ILangfuseScoreWriter? _langfuseScoreWriter;
    private readonly ILangfuseSimilarityQuery? _langfuseSimilarityQuery;
    private readonly MemvidClient? _memvidClient;

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
        StartupMemoryBootstrapper startupMemoryBootstrapper,
        OutcomeTracker outcomeTracker,
        IOutcomeReader outcomeReader,
        ITaskExecutionEventWriter? eventWriter = null,
        ILangfuseScoreWriter? langfuseScoreWriter = null,
        ILangfuseSimilarityQuery? langfuseSimilarityQuery = null,
        MemvidClient? memvidClient = null)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _optionsInstance = options;
        _options = options.Value;
        _uiEvents = uiEvents;
        _actorRegistry = actorRegistry;
        _taskRegistry = taskRegistry;
        _startupMemoryBootstrapper = startupMemoryBootstrapper;
        _outcomeTracker = outcomeTracker;
        _outcomeReader = outcomeReader;
        _eventWriter = eventWriter;
        _langfuseScoreWriter = langfuseScoreWriter;
        _langfuseSimilarityQuery = langfuseSimilarityQuery;
        _memvidClient = memvidClient;
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

        // Subscribe to AgentRetired events to keep the auto-scale budget counter accurate
        var retiredListener = _actorSystem.ActorOf(
            Props.Create(() => new AgentRetiredListener(this)),
            "agent-scale-tracker");
        _actorSystem.EventStream.Subscribe(retiredListener, typeof(AgentRetired));

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
            Props.Create(() => new AgentRegistryActor(_loggerFactory, blackboardActor, _uiEvents, _options.AgentHeartbeatIntervalSeconds)),
            "capability-registry");
        _actorRegistry.SetAgentRegistry(capabilityRegistry);

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

        PortAllocator? portAllocator = null;
        if (_options.AgentEndpointEnabled)
        {
            portAllocator = new PortAllocator(_options.AgentEndpointPortRange);
            _logger.LogInformation("Agent endpoints enabled, port range: {Range}",
                _options.AgentEndpointPortRange);
        }

        if (_options.AgentEndpointEnabled && portAllocator is not null)
        {
            if (portAllocator.AvailableCount < swarmAgentPoolSize)
            {
                throw new InvalidOperationException(
                    $"Port range {_options.AgentEndpointPortRange} provides {portAllocator.AvailableCount} ports but {swarmAgentPoolSize} agents require {swarmAgentPoolSize} ports");
            }

            // Create individual agents with identity and allocated ports
            var agents = new List<IActorRef>();
            for (int i = 0; i < swarmAgentPoolSize; i++)
            {
                var agentId = $"agent-{i:D2}";
                var port = portAllocator.Allocate();
                var agent = _actorSystem.ActorOf(
                    Props.Create(() => new SwarmAgentActor(
                        _options,
                        _loggerFactory,
                        agentFrameworkRoleEngine,
                        _telemetry,
                        capabilityRegistry,
                        capabilities,
                        default(TimeSpan),
                        agentId,
                        port)),
                    $"swarm-agent-{agentId}");
                agents.Add(agent);
            }

            // Create a round-robin router over the individual agents
            _ = _actorSystem.ActorOf(
                Props.Empty.WithRouter(new RoundRobinGroup(
                    agents.Select(a => a.Path.ToString()))),
                "swarm-agent");
        }
        else
        {
            // Keep existing pool router (backward compatible)
            _ = _actorSystem.ActorOf(
                Props.Create(() => new SwarmAgentActor(
                        _options,
                        _loggerFactory,
                        agentFrameworkRoleEngine,
                        _telemetry,
                        capabilityRegistry,
                        capabilities,
                        default(TimeSpan),
                        null,
                        (int?)null))
                    .WithRouter(new SmallestMailboxPool(swarmAgentPoolSize)),
                "swarm-agent");
        }

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
        var consensusActor = _actorSystem.ActorOf(
            Props.Create(() => new ConsensusActor(_loggerFactory.CreateLogger<ConsensusActor>())),
            "consensus");

        IActorRef? strategyAdvisorActor = null;
        if (_options.ArcadeDbEnabled)
        {
            var reader = _outcomeReader;
            var opts = _optionsInstance;
            var logFactory = _loggerFactory;
            strategyAdvisorActor = _actorSystem.ActorOf(
                Props.Create(() => new StrategyAdvisorActor(
                    reader,
                    logFactory,
                    opts)),
                "strategy-advisor");
        }

        var tracker = _options.ArcadeDbEnabled ? _outcomeTracker : null;
        var eventRecorder = _eventWriter is not null
            ? new RuntimeEventRecorder(_eventWriter, _loggerFactory.CreateLogger<RuntimeEventRecorder>())
            : null;

        // Create CodeIndexActor if code index is enabled
        IActorRef? codeIndexActor = null;
        if (_options.CodeIndexEnabled)
        {
            var codeIndexHttpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            codeIndexActor = _actorSystem.ActorOf(
                Props.Create(() => new CodeIndexActor(
                    codeIndexHttpClient,
                    _loggerFactory.CreateLogger<CodeIndexActor>(),
                    _optionsInstance)),
                "code-index");
        }

        // Load project context file (e.g. AGENTS.md) if configured
        string? projectContext = null;
        if (!string.IsNullOrWhiteSpace(_options.ProjectContextPath))
        {
            if (File.Exists(_options.ProjectContextPath))
            {
                try
                {
                    projectContext = await File.ReadAllTextAsync(_options.ProjectContextPath, stoppingToken);
                    _logger.LogInformation(
                        "Project context loaded path={Path} chars={CharCount}",
                        _options.ProjectContextPath, projectContext.Length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to read project context file path={Path}; continuing without context",
                        _options.ProjectContextPath);
                }
            }
            else
            {
                _logger.LogWarning(
                    "Project context file not found path={Path}", _options.ProjectContextPath);
            }
        }

        var workspaceBranchManager = new WorkspaceBranchManager(
            _options.WorkspaceBranchEnabled,
            _loggerFactory.CreateLogger<WorkspaceBranchManager>(),
            _options.WorktreeIsolationEnabled);

        SkillMatcher? skillMatcher = null;
        var skillBasePath = Path.Combine(_options.RepoRootPath ?? Directory.GetCurrentDirectory(), ".agent", "skills");
        if (Directory.Exists(skillBasePath))
        {
            try
            {
                var skillIndexBuilder = new SkillIndexBuilder(
                    _loggerFactory.CreateLogger<SkillIndexBuilder>(),
                    _loggerFactory);
                skillIndexBuilder.BuildIndex(skillBasePath);
                var indexedSkills = skillIndexBuilder.GetAllSkills().Values.ToArray();
                if (indexedSkills.Length > 0)
                {
                    skillMatcher = new SkillMatcher(indexedSkills);
                    _logger.LogInformation(
                        "Skill matcher initialized basePath={SkillBasePath} skillCount={SkillCount}",
                        skillBasePath,
                        indexedSkills.Length);
                }
                else
                {
                    _logger.LogInformation("Skill matcher not initialized: no skill definitions found at {SkillBasePath}", skillBasePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize skill matcher from {SkillBasePath}; continuing without skill matching.", skillBasePath);
            }
        }
        else
        {
            _logger.LogDebug("Skill base path not found {SkillBasePath}; continuing without skill matching.", skillBasePath);
        }

        var sandboxEnforcer = new SandboxLevelEnforcer(
            containerAvailable: string.Equals(_options.SandboxMode, "docker", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(_options.SandboxMode, "apple-container", StringComparison.OrdinalIgnoreCase));

        BuildVerifier? buildVerifier = null;
        if (!string.IsNullOrWhiteSpace(_options.VerifySolutionPath))
        {
            buildVerifier = new BuildVerifier(
                _options.VerifySolutionPath,
                _loggerFactory.CreateLogger<BuildVerifier>());
            _logger.LogInformation("BuildVerifier enabled solutionPath={SolutionPath}", _options.VerifySolutionPath);
        }

        var dispatcher = _actorSystem.ActorOf(
            Props.Create(() => new DispatcherActor(
                capabilityRegistry,
                capabilityRegistry,
                supervisor,
                blackboardActor,
                consensusActor,
                agentFrameworkRoleEngine,
                _loggerFactory,
                _telemetry,
                _uiEvents,
                _taskRegistry,
                Microsoft.Extensions.Options.Options.Create(_options),
                tracker,
                strategyAdvisorActor,
                eventRecorder,
                codeIndexActor,
                projectContext,
                workspaceBranchManager,
                buildVerifier,
                sandboxEnforcer,
                _langfuseScoreWriter,
                _memvidClient,
                _langfuseSimilarityQuery,
                skillMatcher)),
            "dispatcher");
        _actorRegistry.SetDispatcher(dispatcher);
        _dispatcher = dispatcher;
        _supervisor = supervisor;
        await _startupMemoryBootstrapper.RestoreAsync(
            _options.MemoryBootstrapEnabled,
            _options.MemoryBootstrapLimit,
            _options.MemoryBootstrapSurfaceLimit,
            _options.MemoryBootstrapOrderBy,
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
            var activeTasks = Math.Max(0, snapshot.Started - snapshot.Completed - snapshot.Failed);
            var totalAgents = _fixedPoolSize + _dynamicAgentCount;
            if (activeTasks > _options.ScaleUpThreshold && totalAgents < _options.MaxPoolSize)
            {
                var shouldSpawn = true;
                if (_options.BudgetEnabled &&
                    _actorRegistry.TryGetAgentRegistry(out var registry) &&
                    registry is not null)
                {
                    try
                    {
                        var agents = await registry.Ask<QueryAgentsResult>(
                            new QueryAgents(AllRoles, null),
                            TimeSpan.FromSeconds(2),
                            cancellationToken);
                        var nonExhausted = agents.Agents
                            .Where(a => a.Budget?.IsExhausted != true)
                            .ToArray();
                        var allLowBudget = nonExhausted.Length > 0 &&
                                           nonExhausted.All(a => a.Budget?.IsLowBudget == true);
                        shouldSpawn = nonExhausted.Length == 0 || allLowBudget;
                        _logger.LogDebug(
                            "Auto-scale budget gate activeTasks={ActiveTasks} nonExhausted={NonExhausted} allLowBudget={AllLowBudget} shouldSpawn={ShouldSpawn}",
                            activeTasks,
                            nonExhausted.Length,
                            allLowBudget,
                            shouldSpawn);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Auto-scale budget gate failed; falling back to default scale-up behavior.");
                    }
                }

                if (shouldSpawn)
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

    /// <summary>
    /// Minimal actor that decrements the auto-scale budget counter each time a dynamic agent retires.
    /// </summary>
    private sealed class AgentRetiredListener : ReceiveActor
    {
        public AgentRetiredListener(Worker owner)
        {
            Receive<AgentRetired>(_ => Interlocked.Decrement(ref owner._dynamicAgentCount));
        }
    }
}
