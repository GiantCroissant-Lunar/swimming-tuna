using System.Collections.ObjectModel;

namespace SwarmAssistant.Contracts.Messaging;

public sealed record AgentCapabilityAdvertisement(
    string ActorPath,
    IReadOnlyList<SwarmRole> Capabilities,
    int CurrentLoad
)
{
    public AgentCapabilityAdvertisement(
        string actorPath,
        SwarmRole[] capabilities,
        int currentLoad)
        : this(
            actorPath,
            new ReadOnlyCollection<SwarmRole>((SwarmRole[])capabilities.Clone()),
            currentLoad)
    {
    }
}
