namespace SwarmAssistant.Runtime.Actors;

/// <summary>
/// Centralized constants for global blackboard key prefixes used in stigmergy signals.
/// Using constants here prevents magic-string drift between producers (SupervisorActor)
/// and consumers (TaskCoordinatorActor).
/// </summary>
internal static class GlobalBlackboardKeys
{
    // Circuit breaker: single key per adapter, value encodes current state
    internal const string AdapterCircuitPrefix = "adapter_circuit:";

    // Task outcome signals
    internal const string TaskSucceededPrefix = "task_succeeded:";
    internal const string TaskBlockedPrefix = "task_blocked:";

    // State values embedded in circuit-breaker entries
    internal const string CircuitStateOpen = "state=open";
    internal const string CircuitStateClosed = "state=closed";

    internal static string AdapterCircuit(string adapterId) =>
        $"{AdapterCircuitPrefix}{adapterId}";

    internal static string TaskSucceeded(string taskId) =>
        $"{TaskSucceededPrefix}{taskId}";

    internal static string TaskBlocked(string taskId) =>
        $"{TaskBlockedPrefix}{taskId}";

    internal const string AgentJoinedPrefix = "agent_joined:";
    internal const string AgentLeftPrefix = "agent_left:";

    internal static string AgentJoined(string agentId) => $"{AgentJoinedPrefix}{agentId}";
    internal static string AgentLeft(string agentId) => $"{AgentLeftPrefix}{agentId}";

    // RFC-005 peer coordination signal keys
    // Note: Uses dot notation (e.g., task.available) per RFC-005 spec,
    // distinct from legacy underscore notation (e.g., agent_joined)
    internal const string TaskAvailablePrefix = "task.available:";
    internal const string TaskClaimedPrefix = "task.claimed:";
    internal const string TaskCompletePrefix = "task.complete:";
    internal const string ArtifactProducedPrefix = "artifact.produced:";
    internal const string HelpNeededPrefix = "help.needed:";

    internal static string TaskAvailable(string taskId) => $"{TaskAvailablePrefix}{taskId}";
    internal static string TaskClaimed(string taskId) => $"{TaskClaimedPrefix}{taskId}";
    internal static string TaskComplete(string taskId) => $"{TaskCompletePrefix}{taskId}";
    internal static string ArtifactProduced(string taskId) => $"{ArtifactProducedPrefix}{taskId}";
    internal static string HelpNeeded(string agentId) => $"{HelpNeededPrefix}{agentId}";
}
