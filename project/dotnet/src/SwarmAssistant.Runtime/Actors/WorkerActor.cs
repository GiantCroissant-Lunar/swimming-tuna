using System.Diagnostics;
using Akka.Actor;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Tasks;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime.Actors;

/// <summary>
/// Worker actor that executes planning, building, and orchestration roles.
/// Includes autonomous quality evaluation: after receiving CLI output, it evaluates
/// quality locally, calculates confidence, and can raise QualityConcern for borderline cases.
/// </summary>
public sealed class WorkerActor : ReceiveActor
{
    private readonly RuntimeOptions _options;
    private readonly AgentFrameworkRoleEngine _agentFrameworkRoleEngine;
    private readonly RuntimeTelemetry _telemetry;
    private readonly RuntimeEventRecorder? _eventRecorder;
    private readonly ILogger _logger;

    public WorkerActor(
        RuntimeOptions options,
        ILoggerFactory loggerFactory,
        AgentFrameworkRoleEngine agentFrameworkRoleEngine,
        RuntimeTelemetry telemetry,
        RuntimeEventRecorder? eventRecorder = null)
    {
        _options = options;
        _agentFrameworkRoleEngine = agentFrameworkRoleEngine;
        _telemetry = telemetry;
        _eventRecorder = eventRecorder;
        _logger = loggerFactory.CreateLogger<WorkerActor>();

        ReceiveAsync<ExecuteRoleTask>(HandleAsync);
    }

    private async Task HandleAsync(ExecuteRoleTask command)
    {
        var replyTo = Sender;

        using var activity = _telemetry.StartActivity(
            "worker.role.execute",
            taskId: command.TaskId,
            role: command.Role.ToString().ToLowerInvariant(),
            runId: command.RunId,
            tags: new Dictionary<string, object?>
            {
                ["actor.name"] = Self.Path.Name,
                ["engine"] = "microsoft-agent-framework",
            });
        var traceId = activity?.TraceId.ToHexString();
        var spanId = activity?.SpanId.ToHexString();

        if (command.Role is not SwarmRole.Planner and not SwarmRole.Builder and not SwarmRole.Orchestrator)
        {
            var error = $"WorkerActor does not process role {command.Role}";
            activity?.SetStatus(ActivityStatusCode.Error, error);
            replyTo.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                error,
                DateTimeOffset.UtcNow,
                ActorName: Self.Path.Name,
                TraceId: traceId,
                SpanId: spanId));
            return;
        }

        if (_options.SimulateBuilderFailure && command.Role == SwarmRole.Builder)
        {
            const string error = "Simulated builder failure for phase testing.";
            activity?.SetStatus(ActivityStatusCode.Error, error);
            replyTo.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                error,
                DateTimeOffset.UtcNow,
                ActorName: Self.Path.Name,
                TraceId: traceId,
                SpanId: spanId));
            return;
        }

        try
        {
            Context.System.EventStream.Publish(new RoleLifecycleEvent(
                command.TaskId,
                command.Role,
                "started",
                DateTimeOffset.UtcNow,
                Self.Path.Name));

            var result = await _agentFrameworkRoleEngine.ExecuteAsync(command);
            var output = result.Output;
            var adapterId = result.AdapterId;

            // TODO: CliRoleExecutionResult does not carry process exit code.
            // Propagate exit code from SubscriptionCliRoleExecutor in a follow-up.
            _ = _eventRecorder?.RecordDiagnosticAdapterAsync(
                command.TaskId,
                command.RunId ?? "",
                result.AdapterId ?? "unknown",
                result.Output?.Length ?? 0,
                command.Role.ToString().ToLowerInvariant(),
                exitCode: 0);

            var confidence = EvaluateQuality(output, command.Role, adapterId);

            activity?.SetTag("output.length", output.Length);
            activity?.SetTag("quality.confidence", confidence);
            activity?.SetTag("agent.framework.cli.adapter", adapterId);
            activity?.SetStatus(ActivityStatusCode.Ok);

            _logger.LogInformation(
                "Worker role={Role} completed taskId={TaskId} executionMode={ExecutionMode} adapter={AdapterId} confidence={Confidence}",
                command.Role,
                command.TaskId,
                _options.AgentFrameworkExecutionMode,
                adapterId,
                confidence);

            // Self-retry if confidence is very low (unless already retried).
            // Retry BEFORE publishing the concern so the supervisor sees the
            // final confidence rather than a stale pre-retry value.
            if (confidence < QualityEvaluator.SelfRetryThreshold && command.PreviousConfidence is null)
            {
                _logger.LogInformation(
                    "Worker self-retry triggered taskId={TaskId} role={Role} confidence={Confidence}",
                    command.TaskId,
                    command.Role,
                    confidence);

                activity?.SetTag("quality.self_retry", true);

                // Re-execute with adjusted strategy (skip the adapter that produced low quality)
                var adjustedCommand = command with
                {
                    PreferredAdapter = QualityEvaluator.GetAlternativeAdapter(adapterId),
                    PreviousConfidence = confidence
                };

                var retryResult = await _agentFrameworkRoleEngine.ExecuteAsync(adjustedCommand);
                var retryConfidence = EvaluateQuality(retryResult.Output, command.Role, retryResult.AdapterId);

                _logger.LogInformation(
                    "Worker self-retry completed taskId={TaskId} role={Role} oldConfidence={OldConfidence} newConfidence={NewConfidence}",
                    command.TaskId,
                    command.Role,
                    confidence,
                    retryConfidence);

                // Keep the retry if it's better, otherwise fallback to the original
                if (retryConfidence > confidence)
                {
                    output = retryResult.Output;
                    adapterId = retryResult.AdapterId;
                    confidence = retryConfidence;
                }
            }

            // Raise QualityConcern for borderline cases
            if (confidence < QualityEvaluator.QualityConcernThreshold)
            {
                var concern = QualityEvaluator.BuildQualityConcern(output, confidence, $"Worker ({command.Role}) quality concern");

                _logger.LogWarning(
                    "Worker quality concern taskId={TaskId} role={Role} confidence={Confidence} concern={Concern}",
                    command.TaskId,
                    command.Role,
                    confidence,
                    concern);

                // Notify supervisor of quality concern
                Context.System.EventStream.Publish(new QualityConcern(
                    command.TaskId,
                    command.Role,
                    concern,
                    confidence,
                    adapterId,
                    DateTimeOffset.UtcNow));

                activity?.SetTag("quality.concern", concern);
            }

            replyTo.Tell(new RoleTaskSucceeded(
                command.TaskId,
                command.Role,
                output,
                DateTimeOffset.UtcNow,
                confidence,
                adapterId,
                Self.Path.Name,
                traceId,
                spanId));
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Worker role={Role} failed taskId={TaskId} executionMode={ExecutionMode}",
                command.Role,
                command.TaskId,
                _options.AgentFrameworkExecutionMode);

            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            replyTo.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                $"Agent Framework execution failed: {exception.Message}",
                DateTimeOffset.UtcNow,
                ActorName: Self.Path.Name,
                TraceId: traceId,
                SpanId: spanId));
        }
    }

    /// <summary>
    /// Evaluates output quality and returns a confidence score between 0.0 and 1.0.
    /// Uses shared helpers from <see cref="QualityEvaluator"/> plus role-specific keyword scoring.
    /// </summary>
    private static double EvaluateQuality(string output, SwarmRole role, string? adapterId)
    {
        var scores = new List<double>();

        // Factor 1: Output length (normalized)
        var lengthScore = Math.Min(output.Length / 500.0, 1.0);
        scores.Add(lengthScore);

        // Factor 2: Role-specific keyword presence
        var keywordScore = EvaluateRoleKeywords(output, role);
        scores.Add(keywordScore);

        // Factor 3: Adapter reliability bonus (shared)
        var adapterScore = QualityEvaluator.GetAdapterReliabilityScore(adapterId);
        scores.Add(adapterScore);

        // Factor 4: Structural indicators (shared)
        var structureScore = QualityEvaluator.EvaluateStructure(output, role);
        scores.Add(structureScore);

        // Weighted average
        var weights = new[] { 0.25, 0.35, 0.15, 0.25 };
        var confidence = scores.Zip(weights, (s, w) => s * w).Sum();

        return Math.Clamp(confidence, 0.0, 1.0);
    }

    private static double EvaluateRoleKeywords(string output, SwarmRole role)
    {
        var lowerOutput = output.ToLowerInvariant();
        var keywords = role switch
        {
            SwarmRole.Planner => new[] { "plan", "step", "goal", "action", "task", "requirement" },
            SwarmRole.Builder => new[] { "implement", "code", "test", "function", "class", "method" },
            SwarmRole.Orchestrator => new[] { "action", "decision", "next", "recommend", "proceed" },
            _ => Array.Empty<string>()
        };

        if (keywords.Length == 0)
            return 0.5;

        var matches = keywords.Count(k => lowerOutput.Contains(k));
        return (double)matches / keywords.Length;
    }
}
