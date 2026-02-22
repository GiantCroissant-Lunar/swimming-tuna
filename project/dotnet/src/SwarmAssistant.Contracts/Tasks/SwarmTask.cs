namespace SwarmAssistant.Contracts.Tasks;

public enum TaskStatus
{
    Queued,
    Planning,
    Building,
    Reviewing,
    Done,
    Blocked
}

public sealed record SwarmTask(
    string TaskId,
    string Title,
    string Description,
    TaskStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Error = null,
    string? ParentTaskId = null,
    IReadOnlyList<string>? ChildTaskIds = null
);
