namespace SwarmAssistant.Contracts.Planning;

public enum WorldKey
{
    TaskExists,
    PlanExists,
    BuildExists,
    BuildCompiles,
    ReviewPassed,
    ReviewRejected,
    ReworkAttempted,
    RetryLimitReached,
    TaskCompleted,
    TaskBlocked,
    AdapterAvailable,
    SubTasksSpawned,
    SubTasksCompleted,
}
