namespace SwarmAssistant.Runtime.Dto;

public sealed record TaskSummaryDto(
    string TaskId,
    string Title,
    string Status,
    DateTimeOffset UpdatedAt,
    string? Error
);
