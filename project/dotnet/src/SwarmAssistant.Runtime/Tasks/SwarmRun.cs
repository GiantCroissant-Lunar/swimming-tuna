namespace SwarmAssistant.Runtime.Tasks;

public sealed record SwarmRun(
    string RunId,
    string TaskId,
    string Role,
    string? Adapter,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Output = null,
    string? Error = null
);
