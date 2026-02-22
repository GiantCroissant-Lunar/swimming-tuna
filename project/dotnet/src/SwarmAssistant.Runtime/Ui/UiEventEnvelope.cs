namespace SwarmAssistant.Runtime.Ui;

public sealed record UiEventEnvelope(
    long Sequence,
    string Type,
    string? TaskId,
    DateTimeOffset At,
    object Payload
);
