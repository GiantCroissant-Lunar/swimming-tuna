
using System.Text.Json.Serialization;
using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Agents;
public sealed record AgentCard
{
    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; }

    [JsonPropertyName("capabilities")]
    public required SwarmRole[] Capabilities { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("sandboxLevel")]
    public required int SandboxLevel { get; init; }

    [JsonPropertyName("sandboxRequirements")]
    public SandboxRequirements? SandboxRequirements { get; init; }

    [JsonPropertyName("endpointUrl")]
    public required string EndpointUrl { get; init; }
}
