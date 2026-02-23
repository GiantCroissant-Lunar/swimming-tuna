namespace SwarmAssistant.Runtime.Configuration;

public sealed class RuntimeOptions
{
    public const string SectionName = "Runtime";

    public string Profile { get; init; } = "local";
    public string RoleSystem { get; init; } = "akka";
    public string AgentExecution { get; init; } = "microsoft-agent-framework";
    public string AgentFrameworkExecutionMode { get; init; } = "in-process-workflow";
    public int RoleExecutionTimeoutSeconds { get; init; } = 120;
    public string[] CliAdapterOrder { get; init; } = [];
    public string SandboxMode { get; init; } = "docker";
    public SandboxWrapperOptions DockerSandboxWrapper { get; init; } = new();
    public SandboxWrapperOptions AppleContainerSandboxWrapper { get; init; } = new();
    public bool AgUiEnabled { get; init; } = true;
    public string AgUiBindUrl { get; init; } = "http://127.0.0.1:5080";
    public string AgUiProtocolVersion { get; init; } = "0.1";
    public bool A2AEnabled { get; init; } = false;
    public string A2AAgentCardPath { get; init; } = "/.well-known/agent-card.json";
    public bool ArcadeDbEnabled { get; init; } = false;
    public string ArcadeDbHttpUrl { get; init; } = "http://127.0.0.1:2480";
    public string ArcadeDbDatabase { get; init; } = "swarm_assistant";
    /// <summary>
    /// ArcadeDB username. Leave empty for anonymous access or set to a restricted
    /// application-scoped account. Avoid using the root admin account in production.
    /// </summary>
    public string ArcadeDbUser { get; init; } = string.Empty;
    public string ArcadeDbPassword { get; init; } = string.Empty;
    public bool ArcadeDbAutoCreateSchema { get; init; } = true;
    public bool MemoryBootstrapEnabled { get; init; } = true;
    public int MemoryBootstrapLimit { get; init; } = 200;
    public string LangfuseBaseUrl { get; init; } = "http://localhost:3000";
    public int HealthHeartbeatSeconds { get; init; } = 30;

    public bool LangfuseTracingEnabled { get; init; } = false;
    public string? LangfusePublicKey { get; init; }
    public string? LangfuseSecretKey { get; init; }
    public string? LangfuseOtlpEndpoint { get; init; }

    /// <summary>
    /// When true, submits a demo task at startup. This is a development convenience
    /// flag and must be explicitly opted in; it defaults to false to avoid
    /// unintentional task submission in staging and production environments.
    /// </summary>
    public bool AutoSubmitDemoTask { get; init; } = false;
    public string DemoTaskTitle { get; init; } = "Phase 3 agent framework execution";
    public string DemoTaskDescription { get; init; } = "Validate coordinator-worker-reviewer lifecycle through Microsoft Agent Framework workflows.";

    /// <summary>
    /// Number of worker actor instances in the SmallestMailbox pool.
    /// Each instance can handle one role execution (Plan/Build) at a time.
    /// Values are clamped to [1, 16].
    /// </summary>
    // Default must be within [1, 16]; the backing field bypasses the init setter.
    private int _workerPoolSize = 3;
    public int WorkerPoolSize
    {
        get => _workerPoolSize;
        init => _workerPoolSize = Math.Clamp(value, 1, 16);
    }

    /// <summary>
    /// Number of reviewer actor instances in the SmallestMailbox pool.
    /// Values are clamped to [1, 16].
    /// </summary>
    // Default must be within [1, 16]; the backing field bypasses the init setter.
    private int _reviewerPoolSize = 2;
    public int ReviewerPoolSize
    {
        get => _reviewerPoolSize;
        init => _reviewerPoolSize = Math.Clamp(value, 1, 16);
    }

    /// <summary>
    /// Maximum number of concurrent CLI subprocess executions across all actors.
    /// Prevents resource exhaustion when multiple pool instances run simultaneously.
    /// Values are clamped to [1, 32].
    /// </summary>
    // Default must be within [1, 32]; the backing field bypasses the init setter.
    private int _maxCliConcurrency = 4;
    public int MaxCliConcurrency
    {
        get => _maxCliConcurrency;
        init => _maxCliConcurrency = Math.Clamp(value, 1, 32);
    }

    /// <summary>
    /// When true, enables dynamic agent spawning based on observed task load.
    /// </summary>
    public bool AutoScaleEnabled { get; init; } = false;

    /// <summary>
    /// Minimum number of swarm agent actors kept alive when auto-scaling is enabled.
    /// </summary>
    public int MinPoolSize { get; init; } = 1;

    /// <summary>
    /// Maximum total number of swarm agent actors (fixed pool + dynamic) when auto-scaling is enabled.
    /// </summary>
    public int MaxPoolSize { get; init; } = 16;

    /// <summary>
    /// Active task count above which a new dynamic agent is spawned.
    /// </summary>
    public int ScaleUpThreshold { get; init; } = 5;

    /// <summary>
    /// Active task count below which idle dynamic agents are allowed to retire.
    /// </summary>
    public int ScaleDownThreshold { get; init; } = 1;

    /// <summary>
    /// TTL in minutes for the <see cref="StrategyAdvisorActor"/> advice cache.
    /// </summary>
    public int StrategyAdvisorCacheTtlMinutes { get; init; } = 5;

    public bool SimulateBuilderFailure { get; init; } = false;
    public bool SimulateReviewerFailure { get; init; } = false;

    /// <summary>
    /// Optional API key for protecting A2A and AG-UI action endpoints.
    /// When set, callers must supply the key via the <c>X-API-Key</c> header.
    /// Leave empty to disable (localhost-only deployments do not require a key).
    /// </summary>
    public string? ApiKey { get; init; }
}

public sealed class SandboxWrapperOptions
{
    public string Command { get; init; } = string.Empty;
    public string[] Args { get; init; } = [];
}
