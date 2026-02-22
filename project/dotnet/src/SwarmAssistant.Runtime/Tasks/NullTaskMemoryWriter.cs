namespace SwarmAssistant.Runtime.Tasks;

public sealed class NullTaskMemoryWriter : ITaskMemoryWriter
{
    public Task WriteAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
