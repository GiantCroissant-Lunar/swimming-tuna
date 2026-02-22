using System.Collections.Concurrent;
using System.Threading.Channels;
using SwarmAssistant.Contracts.Messaging;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tasks;

public sealed class TaskRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TaskSnapshot> _tasks = new(StringComparer.Ordinal);
    private readonly ITaskMemoryWriter _memoryWriter;
    private readonly ILogger<TaskRegistry> _logger;
    private readonly Channel<TaskSnapshot> _persistenceChannel;
    private readonly Task _drainTask;

    public TaskRegistry(ITaskMemoryWriter memoryWriter, ILogger<TaskRegistry> logger)
    {
        _memoryWriter = memoryWriter;
        _logger = logger;
        _persistenceChannel = Channel.CreateBounded<TaskSnapshot>(
            new BoundedChannelOptions(50)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });
        _drainTask = Task.Run(DrainPersistenceChannelAsync);
    }

    public TaskSnapshot Register(TaskAssigned message)
    {
        var snapshot = new TaskSnapshot(
            TaskId: message.TaskId,
            Title: message.Title,
            Description: message.Description,
            Status: TaskState.Queued,
            CreatedAt: message.AssignedAt,
            UpdatedAt: message.AssignedAt);

        if (_tasks.TryAdd(message.TaskId, snapshot))
        {
            PersistBestEffort(snapshot);
            return snapshot;
        }

        return _tasks[message.TaskId];
    }

    public TaskSnapshot? Transition(string taskId, TaskState status)
    {
        if (!_tasks.TryGetValue(taskId, out var previous))
        {
            return null;
        }

        var updated = previous with
        {
            Status = status,
            UpdatedAt = DateTimeOffset.UtcNow,
            Error = null
        };

        _tasks[taskId] = updated;
        PersistBestEffort(updated);
        return updated;
    }

    public TaskSnapshot? SetRoleOutput(string taskId, SwarmRole role, string output)
    {
        if (!_tasks.TryGetValue(taskId, out var previous))
        {
            return null;
        }

        var updated = role switch
        {
            SwarmRole.Planner => previous with
            {
                PlanningOutput = output,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            SwarmRole.Builder => previous with
            {
                BuildOutput = output,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            SwarmRole.Reviewer => previous with
            {
                ReviewOutput = output,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            _ => previous with { UpdatedAt = DateTimeOffset.UtcNow }
        };

        _tasks[taskId] = updated;
        PersistBestEffort(updated);
        return updated;
    }

    public TaskSnapshot? MarkFailed(string taskId, string error)
    {
        if (!_tasks.TryGetValue(taskId, out var previous))
        {
            return null;
        }

        var updated = previous with
        {
            Status = TaskState.Blocked,
            Error = error,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _tasks[taskId] = updated;
        PersistBestEffort(updated);
        return updated;
    }

    public TaskSnapshot? MarkDone(string taskId, string summary)
    {
        if (!_tasks.TryGetValue(taskId, out var previous))
        {
            return null;
        }

        var updated = previous with
        {
            Status = TaskState.Done,
            Summary = summary,
            Error = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _tasks[taskId] = updated;
        PersistBestEffort(updated);
        return updated;
    }

    public TaskSnapshot RegisterSubTask(string taskId, string title, string description, string parentTaskId)
    {
        var snapshot = new TaskSnapshot(
            TaskId: taskId,
            Title: title,
            Description: description,
            Status: TaskState.Queued,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ParentTaskId: parentTaskId);

        if (_tasks.TryAdd(taskId, snapshot))
        {
            if (_tasks.TryGetValue(parentTaskId, out var parent))
            {
                _tasks[parentTaskId] = parent with
                {
                    ChildTaskIds = [..(parent.ChildTaskIds ?? []), taskId],
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }

            PersistBestEffort(snapshot);
            return snapshot;
        }

        return _tasks[taskId];
    }

    public TaskSnapshot? GetTask(string taskId)
    {
        _tasks.TryGetValue(taskId, out var snapshot);
        return snapshot;
    }

    public int Count => _tasks.Count;

    public int ImportSnapshots(IEnumerable<TaskSnapshot> snapshots, bool overwrite = false)
    {
        var imported = 0;
        foreach (var snapshot in snapshots)
        {
            if (overwrite)
            {
                _tasks[snapshot.TaskId] = snapshot;
                imported++;
                continue;
            }

            if (_tasks.TryAdd(snapshot.TaskId, snapshot))
            {
                imported++;
            }
        }

        return imported;
    }

    public IReadOnlyList<TaskSnapshot> GetTasks(int limit = 50)
    {
        return _tasks.Values
            .OrderByDescending(task => task.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();
    }

    public async ValueTask DisposeAsync()
    {
        _persistenceChannel.Writer.TryComplete();
        await _drainTask.ConfigureAwait(false);
    }

    private async Task DrainPersistenceChannelAsync()
    {
        await foreach (var snapshot in _persistenceChannel.Reader.ReadAllAsync())
        {
            try
            {
                await _memoryWriter.WriteAsync(snapshot);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Task snapshot persistence failed taskId={TaskId}",
                    snapshot.TaskId);
            }
        }
    }

    private void PersistBestEffort(TaskSnapshot snapshot)
    {
        if (!_persistenceChannel.Writer.TryWrite(snapshot))
        {
            _logger.LogDebug(
                "Persistence channel full; dropping snapshot for taskId={TaskId}",
                snapshot.TaskId);
        }
    }
}
