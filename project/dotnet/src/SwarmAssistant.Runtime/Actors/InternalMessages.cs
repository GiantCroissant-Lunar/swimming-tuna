using SwarmAssistant.Contracts.Messaging;

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
