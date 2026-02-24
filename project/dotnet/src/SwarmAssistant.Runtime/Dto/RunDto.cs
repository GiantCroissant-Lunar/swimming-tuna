namespace SwarmAssistant.Runtime.Dto;

public sealed record RunDto(
    string RunId,
    string? Title,
    DateTimeOffset CreatedAt
);
