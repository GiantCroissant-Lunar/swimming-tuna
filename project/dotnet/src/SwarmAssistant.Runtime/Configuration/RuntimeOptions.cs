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
    public string ArcadeDbUser { get; init; } = "root";
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

    public bool AutoSubmitDemoTask { get; init; } = true;
    public string DemoTaskTitle { get; init; } = "Phase 3 agent framework execution";
    public string DemoTaskDescription { get; init; } = "Validate coordinator-worker-reviewer lifecycle through Microsoft Agent Framework workflows.";

    public bool SimulateBuilderFailure { get; init; } = false;
    public bool SimulateReviewerFailure { get; init; } = false;
}

public sealed class SandboxWrapperOptions
{
    public string Command { get; init; } = string.Empty;
    public string[] Args { get; init; } = [];
}
