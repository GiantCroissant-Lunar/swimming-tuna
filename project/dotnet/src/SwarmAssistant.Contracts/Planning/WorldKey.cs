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
    SubTasksSpawned,
    SubTasksCompleted,
    AdapterAvailable,
    ConsensusReached,
    ConsensusDisputed,

    // Stigmergy WorldKeys for swarm coordination
    HighFailureRateDetected,
    SimilarTaskSucceeded,
}
