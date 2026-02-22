using System.Collections.Concurrent;
using SwarmAssistant.Contracts.Messaging;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tasks;

public sealed class TaskRegistry
{
    private readonly ConcurrentDictionary<string, TaskSnapshot> _tasks = new(StringComparer.Ordinal);
    private readonly ITaskMemoryWriter _memoryWriter;
    private readonly ILogger<TaskRegistry> _logger;

    public TaskRegistry(ITaskMemoryWriter memoryWriter, ILogger<TaskRegistry> logger)
    {
        _memoryWriter = memoryWriter;
        _logger = logger;
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

    private void PersistBestEffort(TaskSnapshot snapshot)
    {
        _ = Task.Run(async () =>
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
        });
    }
}
