namespace SwarmAssistant.Runtime.Tasks;

public interface ISwarmRunWriter
{
    Task UpsertAsync(SwarmRun run, CancellationToken cancellationToken = default);
}
