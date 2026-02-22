using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Contracts.Planning;

namespace SwarmAssistant.Runtime.Actors;

internal sealed record ExecuteRoleTask(
    string TaskId,
    SwarmRole Role,
    string Title,
    string Description,
    string? PlanningOutput,
    string? BuildOutput
);

internal sealed record RoleTaskSucceeded(
    string TaskId,
    SwarmRole Role,
    string Output,
    DateTimeOffset CompletedAt
);

internal sealed record RoleTaskFailed(
    string TaskId,
    SwarmRole Role,
    string Error,
    DateTimeOffset FailedAt
);

internal sealed record GetSupervisorSnapshot();

internal sealed record SupervisorSnapshot(
    int Started,
    int Completed,
    int Failed,
    int Escalations
);

// Orchestrator decision from CLI agent
internal sealed record OrchestratorDecision(
    string TaskId,
    string ChosenAction,
    string Reasoning,
    DateTimeOffset DecidedAt
);

// Blackboard messages
internal sealed record UpdateBlackboard(
    string TaskId,
    string Key,
    string Value
);

internal sealed record GetBlackboardContext(
    string TaskId
);

internal sealed record BlackboardContext(
    string TaskId,
    IReadOnlyDictionary<string, string> Entries
);

// World state snapshot for telemetry/UI
internal sealed record WorldStateUpdated(
    string TaskId,
    WorldKey Key,
    bool Value
);
