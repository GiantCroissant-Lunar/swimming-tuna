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

// Coordinator → Supervisor: detailed role failure report for active supervision
internal sealed record RoleFailureReport(
    string TaskId,
    SwarmRole FailedRole,
    string Error,
    int RetryCount,
    DateTimeOffset At
);

// Supervisor → Coordinator: retry a specific role (optionally skipping an adapter)
internal sealed record RetryRole(
    string TaskId,
    SwarmRole Role,
    string? SkipAdapter,
    string Reason
);

// Supervisor → broadcast via EventStream: adapter circuit breaker state
internal sealed record AdapterCircuitOpen(
    string AdapterId,
    DateTimeOffset Until
);

internal sealed record AdapterCircuitClosed(
    string AdapterId
);

// Monitor → Workers: health check ping/pong
internal sealed record HealthCheckRequest(
    string RequestId,
    DateTimeOffset At
);

internal sealed record HealthCheckResponse(
    string RequestId,
    string ActorName,
    int ActiveTasks,
    DateTimeOffset At
);

// Formal escalation record for supervisor tracking
internal sealed record TaskEscalated(
    string TaskId,
    string Reason,
    int Level,
    DateTimeOffset At
);

// Monitor self-scheduling tick
internal sealed record MonitorTick;
