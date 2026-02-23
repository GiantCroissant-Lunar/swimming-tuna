namespace SwarmAssistant.Runtime.Tasks;

/// <summary>
/// Represents an immutable, append-only event emitted during task execution.
/// Stored in ArcadeDB for audit trails, replay, and observability.
/// </summary>
public sealed record TaskExecutionEvent(
    string EventId,
    string RunId,
    string TaskId,
    string EventType,
    string? Payload,
    DateTimeOffset OccurredAt,
    long TaskSequence,
    long RunSequence
);
