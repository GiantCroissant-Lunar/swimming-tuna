namespace SwarmAssistant.Runtime.Tasks;

public interface ISwarmRunReader
{
    Task<IReadOnlyList<SwarmRun>> ListAsync(int limit = 50, CancellationToken cancellationToken = default);

    Task<SwarmRun?> GetAsync(string runId, CancellationToken cancellationToken = default);
}
