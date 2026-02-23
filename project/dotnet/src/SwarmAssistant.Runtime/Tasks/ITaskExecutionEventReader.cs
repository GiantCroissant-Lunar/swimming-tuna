namespace SwarmAssistant.Runtime.Tasks;

public interface ITaskExecutionEventReader
{
    /// <summary>
    /// Returns events for the given task, ordered by ascending <c>taskSequence</c>.
    /// </summary>
    /// <param name="taskId">The task whose events to retrieve.</param>
    /// <param name="limit">Maximum number of events to return (default 200, clamped to [1, 1000]).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An ordered, read-only list of events; empty when none exist or persistence is disabled.</returns>
    Task<IReadOnlyList<TaskExecutionEvent>> ListByTaskAsync(
        string taskId,
        int limit = 200,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns events for the given run, ordered by ascending <c>runSequence</c>.
    /// </summary>
    /// <param name="runId">The run whose events to retrieve.</param>
    /// <param name="limit">Maximum number of events to return (default 200, clamped to [1, 1000]).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An ordered, read-only list of events; empty when none exist or persistence is disabled.</returns>
    Task<IReadOnlyList<TaskExecutionEvent>> ListByRunAsync(
        string runId,
        int limit = 200,
        CancellationToken cancellationToken = default);
}
