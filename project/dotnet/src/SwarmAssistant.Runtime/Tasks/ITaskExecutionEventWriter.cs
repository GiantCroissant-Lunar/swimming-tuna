namespace SwarmAssistant.Runtime.Tasks;

public interface ITaskExecutionEventWriter
{
    /// <summary>
    /// Appends a <see cref="TaskExecutionEvent"/> to the append-only event store.
    /// </summary>
    /// <param name="evt">The event to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AppendAsync(TaskExecutionEvent evt, CancellationToken cancellationToken = default);
}
