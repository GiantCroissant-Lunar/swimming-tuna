namespace SwarmAssistant.Contracts.Messaging;

public sealed record ProviderInfo
{
    public string Adapter { get; init; } = "unknown";
    public string Type { get; init; } = "subscription";
    public string? Plan { get; init; }
    public string? Model { get; init; }
    public string? Reasoning { get; init; }
}
