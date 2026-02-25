namespace SwarmAssistant.Runtime.Tasks;

public interface ITaskMemoryReader
{
    Task<IReadOnlyList<TaskSnapshot>> ListAsync(int limit = 50, string orderBy = "updated", CancellationToken cancellationToken = default);

    Task<TaskSnapshot?> GetAsync(string taskId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskSnapshot>> ListByRunIdAsync(string runId, int limit = 50, CancellationToken cancellationToken = default);
}
