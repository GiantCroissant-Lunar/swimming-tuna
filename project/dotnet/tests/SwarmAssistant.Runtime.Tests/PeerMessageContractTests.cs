using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Tests;

public sealed class PeerMessageContractTests
{
    [Fact]
    public void PeerMessage_RoundTripsAllProperties()
    {
        var timestamp = new DateTimeOffset(2026, 2, 25, 12, 0, 0, TimeSpan.Zero);
        var message = new PeerMessage(
            MessageId: "msg-123",
            FromAgentId: "agent-1",
            ToAgentId: "agent-2",
            Type: PeerMessageType.TaskRequest,
            Payload: "{\"task\":\"do-something\"}",
            ReplyTo: "msg-100",
            Timestamp: timestamp
        );

        Assert.Equal("msg-123", message.MessageId);
        Assert.Equal("agent-1", message.FromAgentId);
        Assert.Equal("agent-2", message.ToAgentId);
        Assert.Equal(PeerMessageType.TaskRequest, message.Type);
        Assert.Equal("{\"task\":\"do-something\"}", message.Payload);
        Assert.Equal("msg-100", message.ReplyTo);
        Assert.Equal(timestamp, message.Timestamp);
    }

    [Fact]
    public void PeerMessageAck_AcceptedCase_ReasonIsNull()
    {
        var ack = new PeerMessageAck(
            MessageId: "msg-456",
            Accepted: true
        );

        Assert.Equal("msg-456", ack.MessageId);
        Assert.True(ack.Accepted);
        Assert.Null(ack.Reason);
    }

    [Fact]
    public void PeerMessageAck_RejectedCase_WithReason()
    {
        var ack = new PeerMessageAck(
            MessageId: "msg-789",
            Accepted: false,
            Reason: "Agent is busy"
        );

        Assert.Equal("msg-789", ack.MessageId);
        Assert.False(ack.Accepted);
        Assert.Equal("Agent is busy", ack.Reason);
    }

    [Fact]
    public void PeerMessageType_AllValuesAreDefined()
    {
        var enumValues = Enum.GetValues<PeerMessageType>();

        Assert.Contains(PeerMessageType.TaskRequest, enumValues);
        Assert.Contains(PeerMessageType.TaskResponse, enumValues);
        Assert.Contains(PeerMessageType.HelpRequest, enumValues);
        Assert.Contains(PeerMessageType.HelpResponse, enumValues);
        Assert.Contains(PeerMessageType.Broadcast, enumValues);
        Assert.Equal(5, enumValues.Length);
    }
}
