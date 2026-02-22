namespace SwarmAssistant.Runtime.Tasks;

public interface ITaskMemoryWriter
{
    Task WriteAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default);
}
