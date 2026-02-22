using System.Diagnostics;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Configuration;
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
    private readonly ILogger _logger;

    public WorkerActor(
        RuntimeOptions options,
        ILoggerFactory loggerFactory,
        AgentFrameworkRoleEngine agentFrameworkRoleEngine,
        RuntimeTelemetry telemetry)
    {
        _options = options;
        _agentFrameworkRoleEngine = agentFrameworkRoleEngine;
        _telemetry = telemetry;
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
            tags: new Dictionary<string, object?>
            {
                ["actor.name"] = Self.Path.Name,
                ["engine"] = "microsoft-agent-framework",
            });

        if (command.Role is not SwarmRole.Planner and not SwarmRole.Builder and not SwarmRole.Orchestrator)
        {
            var error = $"WorkerActor does not process role {command.Role}";
            activity?.SetStatus(ActivityStatusCode.Error, error);
            replyTo.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                error,
                DateTimeOffset.UtcNow));
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
                DateTimeOffset.UtcNow));
            return;
        }

        try
        {
            var result = await _agentFrameworkRoleEngine.ExecuteAsync(command);
            var output = result.Output;
            var adapterId = result.AdapterId;
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

            // Self-retry if confidence is very low (unless already retried); publish concern after
            // retry so the concern reflects the final confidence, not the pre-retry value.
            if (confidence < QualityEvaluator.SelfRetryThreshold && command.MaxConfidence is null)
            {
                _logger.LogInformation(
                    "Worker self-retry triggered taskId={TaskId} role={Role} confidence={Confidence}",
                    command.TaskId,
                    command.Role,
                    confidence);

                activity?.SetTag("quality.self_retry", true);

                var adjustedCommand = command with
                {
                    PreferredAdapter = QualityEvaluator.GetAlternativeAdapter(adapterId),
                    MaxConfidence = confidence
                };

                var retryResult = await _agentFrameworkRoleEngine.ExecuteAsync(adjustedCommand);
                output = retryResult.Output;
                adapterId = retryResult.AdapterId;
                confidence = EvaluateQuality(output, command.Role, adapterId);

                _logger.LogInformation(
                    "Worker self-retry completed taskId={TaskId} role={Role} adapter={AdapterId} newConfidence={Confidence}",
                    command.TaskId,
                    command.Role,
                    adapterId,
                    confidence);

                activity?.SetTag("quality.confidence_after_retry", confidence);
            }

            // Raise QualityConcern for borderline cases (after any self-retry, so confidence is final)
            if (confidence < QualityEvaluator.QualityConcernThreshold)
            {
                var concern = BuildQualityConcern(output, confidence);
                _logger.LogWarning(
                    "Worker quality concern taskId={TaskId} role={Role} adapter={AdapterId} confidence={Confidence} concern={Concern}",
                    command.TaskId,
                    command.Role,
                    adapterId,
                    confidence,
                    concern);

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
                adapterId));
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
                DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// Evaluates output quality and returns a confidence score between 0.0 and 1.0.
    /// Confidence is derived from output length, keyword presence, and adapter type.
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

        // Factor 3: Adapter reliability bonus (uses actual adapter ID from execution)
        var adapterScore = QualityEvaluator.GetAdapterReliabilityScore(adapterId);
        scores.Add(adapterScore);

        // Factor 4: Structural indicators — skipped (neutralised) for Orchestrator
        var structureScore = QualityEvaluator.EvaluateStructure(output, role);
        scores.Add(structureScore);

        // Weighted average (can be tuned)
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

        if (keywords.Length == 0) return 0.5;

        var matches = keywords.Count(k => lowerOutput.Contains(k));
        return (double)matches / keywords.Length;
    }

    private static string BuildQualityConcern(string output, double confidence)
    {
        var concerns = new List<string>();

        if (output.Length < 100)
            concerns.Add("output too short");

        if (output.Length > 10000)
            concerns.Add("output excessively long");

        if (!output.Contains("```") && !output.Contains("- ") && !output.Contains("1. "))
            concerns.Add("lacks structure");

        if (concerns.Count == 0)
            concerns.Add("low confidence score");

        return $"Quality concern ({confidence:F2}): {string.Join(", ", concerns)}";
    }
}

