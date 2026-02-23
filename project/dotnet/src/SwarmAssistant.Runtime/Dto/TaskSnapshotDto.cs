namespace SwarmAssistant.Runtime.Dto;

public sealed record TaskSnapshotDto(
    string TaskId,
    string Title,
    string Description,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PlanningOutput,
    string? BuildOutput,
    string? ReviewOutput,
    string? Summary,
    string? Error,
    string? ParentTaskId,
    IReadOnlyList<string>? ChildTaskIds,
    string? RunId
);
