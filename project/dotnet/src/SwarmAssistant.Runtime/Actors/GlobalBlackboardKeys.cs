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
}
