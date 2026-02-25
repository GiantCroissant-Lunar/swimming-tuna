namespace SwarmAssistant.Runtime.Dto;

public sealed record TaskArtifactDto(
    string ArtifactId,
    string RunId,
    string TaskId,
    string AgentId,
    string Type,
    string? Path,
    string ContentHash,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string>? Metadata
);
