
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Execution;

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
    public string[] ApiProviderOrder { get; init; } = ["openai"];
    public string OpenAiApiKeyEnvVar { get; init; } = "OPENAI_API_KEY";
    public string OpenAiBaseUrl { get; init; } = "https://api.openai.com/v1";
    public int OpenAiRequestTimeoutSeconds { get; init; } = 120;
    public Dictionary<string, RoleModelPreference> RoleModelMapping { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public string SandboxMode { get; init; } = "docker";
    public SandboxWrapperOptions DockerSandboxWrapper { get; init; } = new();
    public SandboxWrapperOptions AppleContainerSandboxWrapper { get; init; } = new();
    public bool AgUiEnabled { get; init; } = true;
    public string AgUiBindUrl { get; init; } = "http://127.0.0.1:5080";
    public string AgUiProtocolVersion { get; init; } = "0.1";
    public bool A2AEnabled { get; init; } = false;
    public string A2AAgentCardPath { get; init; } = "/.well-known/agent-card.json";
    public bool AgentEndpointEnabled { get; init; }
    public string AgentEndpointPortRange { get; init; } = "8001-8032";
    public int AgentHeartbeatIntervalSeconds { get; init; } = 30;
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

    private int _memoryBootstrapSurfaceLimit = 50;
    public int MemoryBootstrapSurfaceLimit
    {
        get => _memoryBootstrapSurfaceLimit;
        init => _memoryBootstrapSurfaceLimit = Math.Clamp(value, 1, 200);
    }

    public string MemoryBootstrapOrderBy { get; init; } = "updated";
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
    /// Enables RFC-007 budget-aware lifecycle behavior for swarm agents.
    /// When disabled, agents advertise an unlimited budget envelope.
    /// </summary>
    public bool BudgetEnabled { get; init; } = false;

    /// <summary>
    /// Default budget type used by agents when budget lifecycle is enabled.
    /// </summary>
    public BudgetType BudgetType { get; init; } = BudgetType.TokenLimited;

    /// <summary>
    /// Total token budget advertised per agent when <see cref="BudgetEnabled"/> is true.
    /// Values are clamped to be non-negative.
    /// </summary>
    private long _budgetTotalTokens = 500_000;
    public long BudgetTotalTokens
    {
        get => _budgetTotalTokens;
        init => _budgetTotalTokens = Math.Max(0, value);
    }

    /// <summary>
    /// Budget warning threshold in [0.0, 1.0].
    /// Agents at or above this used fraction are treated as low-budget.
    /// </summary>
    private double _budgetWarningThreshold = 0.8;
    public double BudgetWarningThreshold
    {
        get => _budgetWarningThreshold;
        init => _budgetWarningThreshold = Math.Clamp(value, 0.0, _budgetHardLimit);
    }

    /// <summary>
    /// Budget hard limit in [0.0, 1.0].
    /// Agents at or above this used fraction are treated as exhausted.
    /// </summary>
    private double _budgetHardLimit = 1.0;
    public double BudgetHardLimit
    {
        get => _budgetHardLimit;
        init => _budgetHardLimit = Math.Clamp(value, _budgetWarningThreshold, 1.0);
    }

    /// <summary>
    /// Approximation ratio used to estimate token usage from text length
    /// for subscription CLI execution modes that do not expose token counts.
    /// </summary>
    private int _budgetCharsPerToken = 4;
    public int BudgetCharsPerToken
    {
        get => _budgetCharsPerToken;
        init => _budgetCharsPerToken = Math.Clamp(value, 1, 32);
    }

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

    /// <summary>
    /// When true, creates an isolated git branch (swarm/{taskId}) for each builder
    /// execution. Defaults to false; Task 8 will wire this into builder dispatch.
    /// </summary>
    public bool WorkspaceBranchEnabled { get; init; } = false;

    /// <summary>
    /// When true (and WorkspaceBranchEnabled is true), each task gets an isolated
    /// git worktree at .worktrees/swarm-{taskId} instead of sharing the main workspace.
    /// CLI adapters execute in the worktree directory, preventing concurrent tasks
    /// from interfering with each other's HEAD or file state.
    /// </summary>
    public bool WorktreeIsolationEnabled { get; init; } = false;

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

    // ── Memvid Run Memory ───────────────────────────────────────

    /// <summary>
    /// Enables memvid memory integration for run and task context storage.
    /// </summary>
    public bool MemvidEnabled { get; init; } = false;

    /// <summary>
    /// Path to the Python interpreter used to invoke the memvid CLI.
    /// </summary>
    public string MemvidPythonPath { get; init; } = ".venv/bin/python";

    /// <summary>
    /// Working directory containing the memvid-svc Python package
    /// (i.e. the directory with <c>src/__main__.py</c>).
    /// </summary>
    public string MemvidSvcDir { get; init; } = "project/infra/memvid-svc";

    /// <summary>
    /// Timeout in seconds for each memvid subprocess invocation.
    /// </summary>
    public int MemvidTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Maximum number of chunks to retrieve from sibling task memvid stores
    /// when enriching builder prompts.
    /// </summary>
    public int MemvidSiblingMaxChunks { get; init; } = 5;

    /// <summary>
    /// Search mode passed to <c>memvid find --mode</c>.
    /// Supported values: <c>auto</c>, <c>sem</c> (semantic), <c>lex</c> (keyword).
    /// </summary>
    public string MemvidSearchMode { get; init; } = "lex";

    /// <summary>
    /// Optional path to a project-level context file (e.g. AGENTS.md).
    /// When set, the file content is loaded at startup and injected into agent prompts
    /// as the 2nd context layer (project context).
    /// Can be set via <c>Runtime__ProjectContextPath</c> environment variable.
    /// </summary>
    public string? ProjectContextPath { get; init; }

    /// <summary>
    /// Repository root path, set at startup from the original CWD before
    /// ASP.NET changes it to the content root. Used for resolving memory paths.
    /// </summary>
    internal string? RepoRootPath { get; set; }

    /// <summary>
    /// Languages to include in code index queries.
    /// When empty or null, no language filter is applied (all indexed languages are searched).
    /// Example: ["csharp", "javascript", "python"]
    /// </summary>
    public string[] CodeIndexLanguages { get; init; } = [];

    /// <summary>
    /// Path to the solution file used by BuildVerifier for build+test verification.
    /// When empty, build verification is skipped.
    /// </summary>
    public string? VerifySolutionPath { get; init; }

    /// <summary>
    /// Maximum number of build verification retry attempts before marking the task as failed.
    /// </summary>
    public int VerifyMaxRetries { get; init; } = 3;

    /// <summary>
    /// Workspace root path for Level 1 (OsSandboxed) sandbox execution.
    /// When empty or null, uses the git repository root.
    /// </summary>
    public string? WorkspacePath { get; init; }

    /// <summary>
    /// Allowed network hosts for Level 1 (OsSandboxed) sandbox execution.
    /// Default includes common API endpoints used by CLI adapters.
    /// </summary>
    public string[] SandboxAllowedHosts { get; init; } =
    [
        "api.github.com",
        "copilot-proxy.githubusercontent.com",
        "api.openai.com",
        "api.moonshot.ai",
        "openrouter.ai",
        "api.z.ai"
    ];

    public SandboxLevel SandboxLevel => SandboxCommandBuilder.ParseLevel(SandboxMode);
}

public sealed class SandboxWrapperOptions
{
    public string Command { get; init; } = string.Empty;
    public string[] Args { get; init; } = [];
}
