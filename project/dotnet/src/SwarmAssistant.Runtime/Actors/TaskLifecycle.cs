using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Actors;

public static class TaskLifecycle
{
    public static TaskState Next(TaskState current, bool success)
    {
        if (!success)
        {
            return TaskState.Blocked;
        }

        return current switch
        {
            TaskState.Queued => TaskState.Planning,
            TaskState.Planning => TaskState.Building,
            TaskState.Building => TaskState.Reviewing,
            TaskState.Reviewing => TaskState.Done,
            TaskState.Done => TaskState.Done,
            TaskState.Blocked => TaskState.Blocked,
            _ => TaskState.Blocked
        };
    }
}
