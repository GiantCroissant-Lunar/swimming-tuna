namespace SwarmAssistant.Contracts.Messaging;

public enum PeerMessageType
{
    TaskRequest,
    TaskResponse,
    HelpRequest,
    HelpResponse,
    Broadcast
}

public sealed record PeerMessage(
    string MessageId,
    string FromAgentId,
    string ToAgentId,
    PeerMessageType Type,
    string Payload,
    string? ReplyTo = null,
    DateTimeOffset? Timestamp = null
);

public sealed record PeerMessageAck(
    string MessageId,
    bool Accepted,
    string? Reason = null
);
