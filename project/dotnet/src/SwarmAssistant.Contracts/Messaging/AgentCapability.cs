using System.Collections.ObjectModel;

namespace SwarmAssistant.Contracts.Messaging;

public sealed record AgentCapabilityAdvertisement(
    string ActorPath,
    IReadOnlyList<SwarmRole> Capabilities,
    int CurrentLoad,
    string? AgentId = null,
    string? EndpointUrl = null
)
{
    public ProviderInfo? Provider { get; init; }
    public SandboxLevel SandboxLevel { get; init; } = SandboxLevel.BareCli;
    public BudgetEnvelope? Budget { get; init; }

    public AgentCapabilityAdvertisement(
        string actorPath,
        SwarmRole[] capabilities,
        int currentLoad,
        string? agentId = null,
        string? endpointUrl = null)
        : this(
            actorPath,
            new ReadOnlyCollection<SwarmRole>((SwarmRole[])capabilities.Clone()),
            currentLoad,
            agentId,
            endpointUrl)
    {
    }
}
