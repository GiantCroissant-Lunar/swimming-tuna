namespace SwarmAssistant.Runtime.Dto;

public sealed record TaskExecutionEventDto(
    string EventId,
    string RunId,
    string TaskId,
    string EventType,
    string? Payload,
    DateTimeOffset OccurredAt,
    long TaskSequence,
    long RunSequence
);
