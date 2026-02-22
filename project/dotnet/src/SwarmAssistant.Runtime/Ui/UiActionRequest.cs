namespace SwarmAssistant.Runtime.Ui;

public sealed record UiActionRequest(
    string? TaskId,
    string ActionId,
    Dictionary<string, object?>? Payload
);
