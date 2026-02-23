using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tasks;

/// <summary>
/// Service that tracks task outcomes for learning and adaptation.
/// Listens to task lifecycle events and records structured outcome data.
/// </summary>
public sealed class OutcomeTracker : IAsyncDisposable
{
    private static readonly Regex KeywordRegex = new(
        @"[a-zA-Z]{4,}",
        RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "that", "this", "with", "from", "have", "been", "were", "they", "their",
        "would", "could", "should", "what", "when", "where", "which", "while",
        "about", "after", "before", "between", "under", "other", "more", "some",
        "such", "only", "also", "than", "then", "very", "just", "into", "over"
    };

    private readonly TaskRegistry _taskRegistry;
    private readonly IOutcomeWriter _outcomeWriter;
    private readonly ILogger<OutcomeTracker> _logger;
    private readonly List<RoleExecutionRecord> _currentExecutions = new();
    private readonly Dictionary<string, int> _retryCounts = new();
    private readonly Dictionary<string, DateTimeOffset> _roleStartTimes = new();
    private readonly Dictionary<string, string?> _adapterAssignments = new();
    private readonly Dictionary<string, double> _confidenceScores = new();

    public OutcomeTracker(
        TaskRegistry taskRegistry,
        IOutcomeWriter outcomeWriter,
        ILogger<OutcomeTracker> logger)
    {
        _taskRegistry = taskRegistry;
        _outcomeWriter = outcomeWriter;
        _logger = logger;
    }

    /// <summary>
    /// Records the start of a role execution.
    /// Called by TaskCoordinatorActor when dispatching a role.
    /// </summary>
    public void RecordRoleStart(string taskId, SwarmRole role, string? adapterUsed)
    {
        var key = $"{taskId}:{role}";
        _roleStartTimes[key] = DateTimeOffset.UtcNow;
        _adapterAssignments[key] = adapterUsed;

        _logger.LogDebug(
            "Role start recorded taskId={TaskId} role={Role} adapter={Adapter}",
            taskId, role, adapterUsed ?? "(default)");
    }

    /// <summary>
    /// Records the completion of a role execution.
    /// Called by TaskCoordinatorActor when a role succeeds or fails.
    /// </summary>
    public void RecordRoleCompletion(
        string taskId,
        SwarmRole role,
        bool succeeded,
        double confidence = 1.0)
    {
        var key = $"{taskId}:{role}";
        var startedAt = _roleStartTimes.TryGetValue(key, out var start) ? start : (DateTimeOffset?)null;
        var adapterUsed = _adapterAssignments.TryGetValue(key, out var adapter) ? adapter : null;

        // Track retries
        var retryKey = $"{taskId}:{role}";
        if (!succeeded)
        {
            _retryCounts.TryGetValue(retryKey, out var currentRetries);
            _retryCounts[retryKey] = currentRetries + 1;
        }

        // Track confidence
        _confidenceScores[key] = confidence;

        // Record the execution
        var record = new RoleExecutionRecord
        {
            Role = role,
            AdapterUsed = adapterUsed,
            RetryCount = _retryCounts.TryGetValue(retryKey, out var retries) ? retries : 0,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Succeeded = succeeded,
            Confidence = confidence
        };

        _currentExecutions.Add(record);

        _logger.LogDebug(
            "Role completion recorded taskId={TaskId} role={Role} succeeded={Succeeded} confidence={Confidence:F2}",
            taskId, role, succeeded, confidence);
    }

    /// <summary>
    /// Finalizes and persists the outcome when a task reaches a terminal state.
    /// </summary>
    public async Task FinalizeOutcomeAsync(
        string taskId,
        TaskState finalStatus,
        string? failureReason = null,
        string? summary = null)
    {
        var snapshot = _taskRegistry.GetTask(taskId);
        if (snapshot is null)
        {
            _logger.LogWarning("Cannot finalize outcome: task not found taskId={TaskId}", taskId);
            return;
        }

        var keywords = ExtractKeywords(snapshot.Title);
        var roleExecutions = _currentExecutions
            .Where(r => snapshot.TaskId == taskId ||
                        (snapshot.ChildTaskIds?.Contains(snapshot.TaskId) ?? false))
            .ToList();

        // If no explicit role executions were recorded, infer from snapshot
        if (roleExecutions.Count == 0)
        {
            roleExecutions = InferRoleExecutions(snapshot);
        }

        var outcome = new TaskOutcome
        {
            TaskId = taskId,
            Title = snapshot.Title,
            Description = snapshot.Description,
            FinalStatus = finalStatus,
            CreatedAt = snapshot.CreatedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            TitleKeywords = keywords,
            DescriptionLength = snapshot.Description?.Length ?? 0,
            SubTaskCount = snapshot.ChildTaskIds?.Count ?? 0,
            RoleExecutions = roleExecutions,
            FailedRole = finalStatus == TaskState.Blocked
                ? roleExecutions.LastOrDefault(r => !r.Succeeded)?.Role
                : null,
            FailureReason = failureReason,
            Summary = summary
        };

        try
        {
            await _outcomeWriter.WriteAsync(outcome);
            _logger.LogInformation(
                "Outcome finalized taskId={TaskId} status={Status} retries={Retries} keywords={Keywords}",
                taskId, finalStatus, outcome.TotalRetries, string.Join(", ", keywords));
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to persist outcome taskId={TaskId}",
                taskId);
        }

        // Clear tracking state for this task
        ClearTaskTracking(taskId);
    }

    /// <summary>
    /// Extracts meaningful keywords from a task title.
    /// </summary>
    public static IReadOnlyList<string> ExtractKeywords(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Array.Empty<string>();
        }

        var matches = KeywordRegex.Matches(title);
        var keywords = matches
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => !StopWords.Contains(w))
            .Distinct()
            .Take(10)
            .ToList();

        return keywords;
    }

    /// <summary>
    /// Clears internal tracking state for a completed task.
    /// </summary>
    private void ClearTaskTracking(string taskId)
    {
        var prefix = $"{taskId}:";
        var keysToRemove = _roleStartTimes.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

        foreach (var key in keysToRemove)
        {
            _roleStartTimes.Remove(key);
            _adapterAssignments.Remove(key);
            _confidenceScores.Remove(key);
        }

        var retryKeysToRemove = _retryCounts.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

        foreach (var key in retryKeysToRemove)
        {
            _retryCounts.Remove(key);
        }

        _currentExecutions.RemoveAll(r =>
            r.Role.ToString().StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Infers role executions from a task snapshot when explicit tracking is unavailable.
    /// </summary>
    private static List<RoleExecutionRecord> InferRoleExecutions(TaskSnapshot snapshot)
    {
        var records = new List<RoleExecutionRecord>();

        if (!string.IsNullOrEmpty(snapshot.PlanningOutput))
        {
            records.Add(new RoleExecutionRecord
            {
                Role = SwarmRole.Planner,
                Succeeded = true,
                Confidence = 1.0
            });
        }

        if (!string.IsNullOrEmpty(snapshot.BuildOutput))
        {
            records.Add(new RoleExecutionRecord
            {
                Role = SwarmRole.Builder,
                Succeeded = true,
                Confidence = 1.0
            });
        }

        if (!string.IsNullOrEmpty(snapshot.ReviewOutput))
        {
            records.Add(new RoleExecutionRecord
            {
                Role = SwarmRole.Reviewer,
                Succeeded = string.IsNullOrEmpty(snapshot.Error),
                Confidence = 1.0
            });
        }

        return records;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Interface for writing task outcomes to persistent storage.
/// </summary>
public interface IOutcomeWriter
{
    Task WriteAsync(TaskOutcome outcome, CancellationToken cancellationToken = default);
}
