
using System.Text.Json.Serialization;

namespace SwarmAssistant.Contracts.Messaging;
public sealed record SandboxRequirements
{
    [JsonPropertyName("needsOAuth")]
    public bool NeedsOAuth { get; init; }

    [JsonPropertyName("needsKeychain")]
    public bool NeedsKeychain { get; init; }

    [JsonPropertyName("needsNetwork")]
    public string[] NeedsNetwork { get; init; } = [];

    [JsonPropertyName("needsGpuAccess")]
    public bool NeedsGpuAccess { get; init; }
}
