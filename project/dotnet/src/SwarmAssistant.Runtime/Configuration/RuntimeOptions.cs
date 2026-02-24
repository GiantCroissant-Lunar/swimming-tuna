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
    /// Number of independent reviews required to reach consensus.
    /// Default: 1 (backward compatible single reviewer).
    /// Values are clamped to [1, 5].
    /// </summary>
    private int _reviewConsensusCount = 1;
    public int ReviewConsensusCount
    {
        get => _reviewConsensusCount;
        init => _reviewConsensusCount = Math.Clamp(value, 1, 5);
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
    /// Values are clamped to [1, 1440].
    /// </summary>
    // Default must be within [1, 1440]; the backing field bypasses the init setter.
    private int _strategyAdvisorCacheTtlMinutes = 5;
    public int StrategyAdvisorCacheTtlMinutes
    {
        get => _strategyAdvisorCacheTtlMinutes;
        init => _strategyAdvisorCacheTtlMinutes = Math.Clamp(value, 1, 1440);
    }

    /// <summary>
    /// Default maximum sub-task spawning depth for task coordinators.
    /// Values are clamped to [0, 10].
    /// </summary>
    private int _defaultMaxSubTaskDepth = 3;
    public int DefaultMaxSubTaskDepth
    {
        get => _defaultMaxSubTaskDepth;
        init => _defaultMaxSubTaskDepth = Math.Clamp(value, 0, 10);
    }

    public bool SimulateBuilderFailure { get; init; } = false;
    public bool SimulateReviewerFailure { get; init; } = false;

    /// <summary>
    /// Optional API key for protecting A2A and AG-UI action endpoints.
    /// When set, callers must supply the key via the <c>X-API-Key</c> header.
    /// Leave empty to disable (localhost-only deployments do not require a key).
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// When true, enables Human-in-the-Loop (HITL) intervention controls, including
    /// <c>pause_task</c>, <c>approve_task</c>, and <c>cancel_task</c> AG-UI actions.
    /// Defaults to <c>false</c> for safe rollout; enable explicitly in profiles that support it.
    /// </summary>
    public bool HitlEnabled { get; init; } = false;

    /// <summary>
    /// When true, publishes task-graph topology events (e.g. subtask spawned/completed)
    /// to the AG-UI event stream under the <c>agui.task.graph.*</c> event namespace.
    /// Defaults to <c>false</c> for safe rollout; enable explicitly in profiles that support it.
    /// </summary>
    public bool GraphTelemetryEnabled { get; init; } = false;

    /// <summary>
    /// When true, serves the OpenAPI JSON document at <c>/openapi/v1.json</c> and
    /// the Swagger UI at <c>/swagger</c>. Should be enabled only in non-production
    /// profiles (<c>Local</c>, <c>Development</c>). Set to <c>false</c> in
    /// <c>SecureLocal</c> and <c>CI</c> profiles to avoid exposing the API schema.
    /// </summary>
    public bool SwaggerEnabled { get; init; } = false;

    /// <summary>
    /// When true, enables the code index integration for codebase-aware agent prompts.
    /// Requires the code-index container to be running.
    /// </summary>
    public bool CodeIndexEnabled { get; init; } = false;

    /// <summary>
    /// URL of the code-index retrieval API.
    /// Default: http://localhost:8080
    /// </summary>
    public string CodeIndexUrl { get; init; } = "http://localhost:8080";

    /// <summary>
    /// Maximum number of code chunks to include in agent prompts.
    /// Values are clamped to [0, 50].
    /// </summary>
    private int _codeIndexMaxChunks = 10;
    public int CodeIndexMaxChunks
    {
        get => _codeIndexMaxChunks;
        init => _codeIndexMaxChunks = Math.Clamp(value, 0, 50);
    }

    /// <summary>
    /// When true, includes code context in planner prompts.
    /// </summary>
    public bool CodeIndexForPlanner { get; init; } = true;

    /// <summary>
    /// When true, includes code context in builder prompts.
    /// </summary>
    public bool CodeIndexForBuilder { get; init; } = true;

    /// <summary>
    /// When true, includes code context in reviewer prompts.
    /// </summary>
    public bool CodeIndexForReviewer { get; init; } = true;

    /// <summary>
    /// Languages to include in code index queries.
    /// When empty or null, no language filter is applied (all indexed languages are searched).
    /// Example: ["csharp", "javascript", "python"]
    /// </summary>
    public string[] CodeIndexLanguages { get; init; } = [];
}

public sealed class SandboxWrapperOptions
{
    public string Command { get; init; } = string.Empty;
    public string[] Args { get; init; } = [];
}
