using SwarmAssistant.Contracts.Messaging;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tasks;

/// <summary>
/// Represents the outcome of a completed task for learning and adaptation.
/// Records structured data about task execution to inform future planning decisions.
/// </summary>
public sealed record TaskOutcome
{
    /// <summary>
    /// Unique identifier for the task.
    /// </summary>
    public required string TaskId { get; init; }

    /// <summary>
    /// Title of the task.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Description of the task.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Final status of the task (Done or Blocked).
    /// </summary>
    public required TaskState FinalStatus { get; init; }

    /// <summary>
    /// When the task was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the task reached its final state.
    /// </summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Total elapsed time from creation to completion.
    /// </summary>
    public TimeSpan TotalDuration => CompletedAt - CreatedAt;

    /// <summary>
    /// Keywords extracted from the task title for pattern matching.
    /// </summary>
    public required IReadOnlyList<string> TitleKeywords { get; init; }

    /// <summary>
    /// Length of the task description in characters.
    /// </summary>
    public int DescriptionLength { get; init; }

    /// <summary>
    /// Number of sub-tasks spawned during execution.
    /// </summary>
    public int SubTaskCount { get; init; }

    /// <summary>
    /// Records of each role execution attempt.
    /// </summary>
    public required IReadOnlyList<RoleExecutionRecord> RoleExecutions { get; init; }

    /// <summary>
    /// Total number of retry attempts across all roles.
    /// </summary>
    public int TotalRetries => RoleExecutions.Sum(r => r.RetryCount);

    /// <summary>
    /// Which role failed, if any.
    /// </summary>
    public SwarmRole? FailedRole { get; init; }

    /// <summary>
    /// Reason for failure, if the task was blocked.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Summary or result of the task, if successful.
    /// </summary>
    public string? Summary { get; init; }
}

/// <summary>
/// Record of a single role execution within a task.
/// </summary>
public sealed record RoleExecutionRecord
{
    /// <summary>
    /// The task ID this execution belongs to.
    /// </summary>
    public required string TaskId { get; init; }

    /// <summary>
    /// The role that was executed.
    /// </summary>
    public required SwarmRole Role { get; init; }

    /// <summary>
    /// The adapter that was used for this role.
    /// </summary>
    public string? AdapterUsed { get; init; }

    /// <summary>
    /// Number of times this role was retried.
    /// </summary>
    public int RetryCount { get; init; }

    /// <summary>
    /// When this role started execution.
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// When this role completed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Duration of this role's execution.
    /// </summary>
    public TimeSpan? Duration => StartedAt.HasValue && CompletedAt.HasValue
        ? CompletedAt - StartedAt
        : null;

    /// <summary>
    /// Whether this role succeeded.
    /// </summary>
    public bool Succeeded { get; init; }

    /// <summary>
    /// Confidence score from the role execution (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; init; }
}

/// <summary>
/// Strategy advice generated from historical outcome analysis.
/// </summary>
public sealed record StrategyAdvice
{
    /// <summary>
    /// Task ID this advice was generated for.
    /// </summary>
    public required string TaskId { get; init; }

    /// <summary>
    /// Historical success rate for similar tasks (0.0 to 1.0).
    /// </summary>
    public double SimilarTaskSuccessRate { get; init; }

    /// <summary>
    /// Number of similar tasks found in history.
    /// </summary>
    public int SimilarTaskCount { get; init; }

    /// <summary>
    /// Recommended adapter preferences based on historical performance.
    /// Key: adapter name, Value: success rate (0.0 to 1.0).
    /// </summary>
    public IReadOnlyDictionary<string, double> AdapterSuccessRates { get; init; } =
        new Dictionary<string, double>();

    /// <summary>
    /// Recommended cost adjustments for GOAP actions.
    /// Key: action name, Value: cost adjustment factor (multiplier, lower = preferred).
    /// </summary>
    public IReadOnlyDictionary<string, double> RecommendedCostAdjustments { get; init; } =
        new Dictionary<string, double>();

    /// <summary>
    /// Human-readable insights and recommendations.
    /// </summary>
    public IReadOnlyList<string> Insights { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Review rejection rate for similar tasks.
    /// </summary>
    public double ReviewRejectionRate { get; init; }

    /// <summary>
    /// Average retry count for similar tasks.
    /// </summary>
    public double AverageRetryCount { get; init; }

    /// <summary>
    /// Common failure patterns to watch for.
    /// </summary>
    public IReadOnlyList<string> CommonFailurePatterns { get; init; } = Array.Empty<string>();
}
