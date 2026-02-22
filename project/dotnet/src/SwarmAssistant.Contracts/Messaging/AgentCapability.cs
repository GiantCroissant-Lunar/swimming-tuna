namespace SwarmAssistant.Contracts.Messaging;

public sealed record AgentCapabilityAdvertisement(
    string ActorPath,
    SwarmRole[] Capabilities,
    int CurrentLoad
);
