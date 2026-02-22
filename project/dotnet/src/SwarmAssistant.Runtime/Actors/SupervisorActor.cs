using System.Diagnostics;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime.Actors;

/// <summary>
/// Tier 1 active supervisor that tracks task lifecycle, decides retry strategies,
/// and manages adapter circuit breaker state. Receives failure reports from
/// coordinators and makes active decisions about retries vs escalation.
/// </summary>
public sealed class SupervisorActor : ReceiveActor
{
    private const int MaxRetriesPerTask = 3;
    private const int AdapterCircuitThreshold = 3;
    private static readonly TimeSpan AdapterCircuitDuration = TimeSpan.FromMinutes(5);

    private readonly RuntimeTelemetry _telemetry;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<string> _trackedAdapterIds;
    private readonly IActorRef? _blackboardActor;

    // Counters for snapshot
    private int _started;
    private int _completed;
    private int _failed;
    private int _escalations;

    // Active supervision state
    private readonly Dictionary<string, IActorRef> _taskCoordinators = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _taskRetryCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startedTaskIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _adapterFailureCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _openCircuits = new(StringComparer.OrdinalIgnoreCase);

    public SupervisorActor(
        ILoggerFactory loggerFactory,
        RuntimeTelemetry telemetry,
        IReadOnlyList<string>? trackedAdapterIds = null,
        IActorRef? blackboardActor = null)
    {
        _telemetry = telemetry;
        _logger = loggerFactory.CreateLogger<SupervisorActor>();
        _trackedAdapterIds = trackedAdapterIds ?? ["copilot", "cline", "kimi"];
        _blackboardActor = blackboardActor;

        Receive<TaskStarted>(OnTaskStarted);
        Receive<TaskResult>(OnTaskResult);
        Receive<TaskFailed>(OnTaskFailed);
        Receive<EscalationRaised>(OnEscalationRaised);
        Receive<RoleFailureReport>(OnRoleFailureReport);
        Receive<GetSupervisorSnapshot>(OnGetSnapshot);
    }

    private void OnTaskStarted(TaskStarted message)
    {
        using var activity = _telemetry.StartActivity(
            "supervisor.task.started",
            taskId: message.TaskId,
            tags: new Dictionary<string, object?>
            {
                ["task.status"] = message.Status.ToString(),
                ["task.actor"] = message.ActorName,
            });

        // Track the coordinator ref for active retry decisions
        _taskCoordinators[message.TaskId] = Sender;

        // Count each unique task only once regardless of how many state-transition
        // TaskStarted messages the coordinator emits.
        if (_startedTaskIds.Add(message.TaskId))
        {
            _started += 1;
        }
        _logger.LogInformation(
            "Task started taskId={TaskId} status={Status} actor={ActorName}",
            message.TaskId,
            message.Status,
            message.ActorName);
    }

    private void OnTaskResult(TaskResult message)
    {
        using var activity = _telemetry.StartActivity(
            "supervisor.task.result",
            taskId: message.TaskId,
            tags: new Dictionary<string, object?>
            {
                ["task.status"] = message.Status.ToString(),
                ["task.actor"] = message.ActorName,
                ["result.length"] = message.Output.Length,
            });

        if (message.Status == Contracts.Tasks.TaskStatus.Done)
        {
            _completed += 1;
            // Clean up tracking state for completed tasks
            _taskCoordinators.Remove(message.TaskId);
            _taskRetryCounts.Remove(message.TaskId);
            _startedTaskIds.Remove(message.TaskId);
        }

        _logger.LogInformation(
            "Task result taskId={TaskId} status={Status} actor={ActorName}",
            message.TaskId,
            message.Status,
            message.ActorName);
    }

    private void OnTaskFailed(TaskFailed message)
    {
        using var activity = _telemetry.StartActivity(
            "supervisor.task.failed",
            taskId: message.TaskId,
            tags: new Dictionary<string, object?>
            {
                ["task.status"] = message.Status.ToString(),
                ["task.actor"] = message.ActorName,
                ["error.message"] = message.Error,
            });
        activity?.SetStatus(ActivityStatusCode.Error, message.Error);

        _failed += 1;
        // Clean up tracking state for permanently failed tasks
        _taskCoordinators.Remove(message.TaskId);
        _taskRetryCounts.Remove(message.TaskId);
        _startedTaskIds.Remove(message.TaskId);
        _logger.LogWarning(
            "Task failed taskId={TaskId} status={Status} actor={ActorName} error={Error}",
            message.TaskId,
            message.Status,
            message.ActorName,
            message.Error);
    }

    private void OnEscalationRaised(EscalationRaised message)
    {
        using var activity = _telemetry.StartActivity(
            "supervisor.escalation.raised",
            taskId: message.TaskId,
            tags: new Dictionary<string, object?>
            {
                ["escalation.level"] = message.Level,
                ["escalation.from"] = message.FromActor,
                ["error.message"] = message.Reason,
            });
        activity?.SetStatus(ActivityStatusCode.Error, message.Reason);

        _escalations += 1;
        _logger.LogError(
            "Escalation raised taskId={TaskId} level={Level} from={FromActor} reason={Reason}",
            message.TaskId,
            message.Level,
            message.FromActor,
            message.Reason);
    }

    private void OnRoleFailureReport(RoleFailureReport report)
    {
        using var activity = _telemetry.StartActivity(
            "supervisor.role.failure",
            taskId: report.TaskId,
            role: report.FailedRole.ToString().ToLowerInvariant(),
            tags: new Dictionary<string, object?>
            {
                ["error.message"] = report.Error,
                ["retry.count"] = report.RetryCount,
            });

        // Track adapter failures if the error message mentions an adapter name
        TrackAdapterFailures(report.Error);

        // Decide: retry the role or accept the failure
        _taskRetryCounts.TryGetValue(report.TaskId, out var totalRetries);

        if (totalRetries < MaxRetriesPerTask && IsRetriable(report.Error))
        {
            _taskRetryCounts[report.TaskId] = totalRetries + 1;

            if (_taskCoordinators.TryGetValue(report.TaskId, out var coordinator))
            {
                var reason = $"Supervisor retry #{totalRetries + 1}: {report.Error}";
                coordinator.Tell(new RetryRole(
                    report.TaskId,
                    report.FailedRole,
                    SkipAdapter: null,
                    reason));

                _logger.LogInformation(
                    "Supervisor initiated retry taskId={TaskId} role={Role} attempt={Attempt}",
                    report.TaskId,
                    report.FailedRole,
                    totalRetries + 1);

                activity?.SetTag("supervisor.decision", "retry");
                return;
            }
        }

        activity?.SetTag("supervisor.decision", "accept_failure");
        _taskCoordinators.Remove(report.TaskId);
        _taskRetryCounts.Remove(report.TaskId);
        _logger.LogWarning(
            "Supervisor accepted failure taskId={TaskId} role={Role} totalRetries={TotalRetries}",
            report.TaskId,
            report.FailedRole,
            totalRetries);
    }

    private void OnGetSnapshot(GetSupervisorSnapshot _)
    {
        Sender.Tell(new SupervisorSnapshot(_started, _completed, _failed, _escalations));
    }

    private void TrackAdapterFailures(string error)
    {
        // Extract adapter names mentioned in failure messages.
        // SubscriptionCliRoleExecutor errors follow the pattern: "adapterId: reason"
        foreach (var adapterId in _trackedAdapterIds)
        {
            if (!error.Contains(adapterId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Check if an existing circuit has expired and reset it
            if (_openCircuits.TryGetValue(adapterId, out var existingExpiry)
                && DateTimeOffset.UtcNow >= existingExpiry)
            {
                _openCircuits.Remove(adapterId);
                _adapterFailureCounts.Remove(adapterId);
                _logger.LogInformation(
                    "Adapter circuit closed (expired) adapterId={AdapterId}", adapterId);
                Context.System.EventStream.Publish(new AdapterCircuitClosed(adapterId));

                // Stigmergy: signal circuit recovery to global blackboard
                _blackboardActor?.Tell(new UpdateGlobalBlackboard(
                    $"adapter_circuit_closed:{adapterId}",
                    DateTimeOffset.UtcNow.ToString("O")));
            }

            _adapterFailureCounts.TryGetValue(adapterId, out var count);
            _adapterFailureCounts[adapterId] = count + 1;

            if (count + 1 >= AdapterCircuitThreshold && !_openCircuits.ContainsKey(adapterId))
            {
                var until = DateTimeOffset.UtcNow.Add(AdapterCircuitDuration);
                _openCircuits[adapterId] = until;

                // Publish circuit open event via Akka EventStream
                Context.System.EventStream.Publish(new AdapterCircuitOpen(adapterId, until));

                // Stigmergy: signal circuit breaker state to global blackboard
                _blackboardActor?.Tell(new UpdateGlobalBlackboard(
                    $"adapter_circuit_open:{adapterId}",
                    $"failures={count + 1}|until={until:O}"));

                _logger.LogWarning(
                    "Adapter circuit opened adapterId={AdapterId} failureCount={FailureCount} until={Until}",
                    adapterId,
                    count + 1,
                    until);
            }
        }
    }

    private static bool IsRetriable(string error)
    {
        // Permanent failures that should not be retried
        var lower = error.ToLowerInvariant();
        if (lower.Contains("unsupported role"))
        {
            return false;
        }

        if (lower.Contains("simulated"))
        {
            return false;
        }

        // Adapter/execution failures are retriable
        return true;
    }
}
