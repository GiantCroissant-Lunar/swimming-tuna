namespace SwarmAssistant.Runtime.Execution;

public interface IModelProvider
{
    string ProviderId { get; }
    Task<bool> ProbeAsync(CancellationToken ct);
    Task<ModelResponse> ExecuteAsync(
        ModelSpec model,
        string prompt,
        ModelExecutionOptions options,
        CancellationToken ct);
}
