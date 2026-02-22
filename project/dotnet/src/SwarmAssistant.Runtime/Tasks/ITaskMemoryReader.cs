namespace SwarmAssistant.Runtime.Tasks;

public interface ITaskMemoryReader
{
    Task<IReadOnlyList<TaskSnapshot>> ListAsync(int limit = 50, CancellationToken cancellationToken = default);

    Task<TaskSnapshot?> GetAsync(string taskId, CancellationToken cancellationToken = default);
}
