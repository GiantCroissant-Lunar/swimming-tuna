using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tasks;

public sealed record TaskSnapshot(
    string TaskId,
    string Title,
    string Description,
    TaskState Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PlanningOutput = null,
    string? BuildOutput = null,
    string? ReviewOutput = null,
    string? Summary = null,
    string? Error = null
);
