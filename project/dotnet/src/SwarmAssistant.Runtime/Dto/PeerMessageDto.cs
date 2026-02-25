namespace SwarmAssistant.Runtime.Dto;

public sealed record PeerMessageSubmitDto(
    string? MessageId,
    string FromAgentId,
    string ToAgentId,
    string Type,
    string Payload,
    string? ReplyTo);

public sealed record PeerMessageAckDto(
    string MessageId,
    bool Accepted,
    string? Reason);
