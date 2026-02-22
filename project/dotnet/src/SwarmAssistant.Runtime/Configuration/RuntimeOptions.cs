namespace SwarmAssistant.Runtime.Configuration;

public sealed class RuntimeOptions
{
    public const string SectionName = "Runtime";

    public string Profile { get; init; } = "local";
    public string RoleSystem { get; init; } = "akka";
    public string AgentExecution { get; init; } = "microsoft-agent-framework";
    public string SandboxMode { get; init; } = "docker";
    public string LangfuseBaseUrl { get; init; } = "http://localhost:3000";
    public int HealthHeartbeatSeconds { get; init; } = 30;

    public bool AutoSubmitDemoTask { get; init; } = true;
    public string DemoTaskTitle { get; init; } = "Phase 2 swarm orchestration bootstrap";
    public string DemoTaskDescription { get; init; } = "Validate coordinator-worker-reviewer lifecycle and supervisor escalation path.";

    public bool SimulateBuilderFailure { get; init; } = false;
    public bool SimulateReviewerFailure { get; init; } = false;
}
