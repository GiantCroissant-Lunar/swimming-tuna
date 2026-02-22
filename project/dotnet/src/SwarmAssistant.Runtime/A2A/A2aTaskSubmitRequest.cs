namespace SwarmAssistant.Runtime.A2A;

public sealed record A2aTaskSubmitRequest(
    string? TaskId,
    string Title,
    string? Description,
    Dictionary<string, object?>? Metadata
);
