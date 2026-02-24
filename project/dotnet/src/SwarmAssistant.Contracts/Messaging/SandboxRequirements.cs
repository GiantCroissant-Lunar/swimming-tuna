namespace SwarmAssistant.Contracts.Messaging;

public sealed record SandboxRequirements
{
    public bool NeedsOAuth { get; init; }
    public bool NeedsKeychain { get; init; }
    public string[] NeedsNetwork { get; init; } = [];
    public bool NeedsGpuAccess { get; init; }
}
