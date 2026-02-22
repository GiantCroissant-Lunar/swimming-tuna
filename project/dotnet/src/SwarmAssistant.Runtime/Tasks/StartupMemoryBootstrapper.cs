using SwarmAssistant.Runtime.Ui;

namespace SwarmAssistant.Runtime.Tasks;

public sealed class StartupMemoryBootstrapper
{
    private const int MaxBootstrapUiEvents = 3;

    private readonly ITaskMemoryReader _taskMemoryReader;
    private readonly TaskRegistry _taskRegistry;
    private readonly UiEventStream _uiEvents;
    private readonly ILogger<StartupMemoryBootstrapper> _logger;

    public StartupMemoryBootstrapper(
        ITaskMemoryReader taskMemoryReader,
        TaskRegistry taskRegistry,
        UiEventStream uiEvents,
        ILogger<StartupMemoryBootstrapper> logger)
    {
        _taskMemoryReader = taskMemoryReader;
        _taskRegistry = taskRegistry;
        _uiEvents = uiEvents;
        _logger = logger;
    }

    public async Task<int> RestoreAsync(bool enabled, int limit, CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            return 0;
        }

        try
        {
            var clampedLimit = Math.Clamp(limit, 1, 1000);
            var memorySnapshots = await _taskMemoryReader.ListAsync(clampedLimit, cancellationToken);
            if (memorySnapshots.Count == 0)
            {
                return 0;
            }

            var importedCount = _taskRegistry.ImportSnapshots(memorySnapshots);
            if (importedCount == 0)
            {
                return 0;
            }

            var statusCounts = memorySnapshots
                .GroupBy(task => task.Status.ToString().ToLowerInvariant())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            var summaryItems = memorySnapshots
                .Select(snapshot => new
                {
                    taskId = snapshot.TaskId,
                    title = snapshot.Title,
                    status = snapshot.Status.ToString().ToLowerInvariant(),
                    updatedAt = snapshot.UpdatedAt,
                    error = snapshot.Error
                })
                .ToList();

            _logger.LogInformation(
                "Restored task snapshots from memory imported={ImportedCount} fetched={FetchedCount}",
                importedCount,
                memorySnapshots.Count);

            _uiEvents.Publish(
                type: "agui.memory.bootstrap",
                taskId: null,
                payload: new
                {
                    source = "arcadedb",
                    importedCount,
                    fetchedCount = memorySnapshots.Count,
                    statusCounts
                });

            _uiEvents.Publish(
                type: "agui.memory.tasks",
                taskId: null,
                payload: new
                {
                    source = "arcadedb",
                    count = summaryItems.Count,
                    items = summaryItems
                });

            foreach (var snapshot in memorySnapshots.Take(MaxBootstrapUiEvents))
            {
                _uiEvents.Publish(
                    type: "agui.ui.surface",
                    taskId: snapshot.TaskId,
                    payload: new
                    {
                        source = "memory-bootstrap",
                        a2ui = A2UiPayloadFactory.CreateSurface(
                            snapshot.TaskId,
                            snapshot.Title,
                            snapshot.Description,
                            snapshot.Status)
                    });

                _uiEvents.Publish(
                    type: "agui.ui.patch",
                    taskId: snapshot.TaskId,
                    payload: new
                    {
                        source = "memory-bootstrap",
                        a2ui = A2UiPayloadFactory.UpdateStatus(
                            snapshot.TaskId,
                            snapshot.Status,
                            snapshot.Error ?? snapshot.Summary)
                    });
            }

            return importedCount;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Memory snapshot restore canceled.");
            return 0;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to restore memory snapshots at startup.");
            _uiEvents.Publish(
                type: "agui.memory.bootstrap.failed",
                taskId: null,
                payload: new
                {
                    source = "arcadedb",
                    error = exception.Message
                });
            return 0;
        }
    }

    public static bool ShouldAutoSubmitDemoTask(bool autoSubmitEnabled, int currentTaskCount)
    {
        return autoSubmitEnabled && currentTaskCount == 0;
    }
}
