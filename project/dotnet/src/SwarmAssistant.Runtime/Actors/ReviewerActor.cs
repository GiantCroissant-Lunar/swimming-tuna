using System.Diagnostics;
using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime.Actors;

/// <summary>
/// Reviewer actor that executes review tasks.
/// Includes autonomous quality evaluation: after receiving CLI output, it evaluates
/// quality locally, calculates confidence, and can raise QualityConcern for borderline cases.
/// </summary>
public sealed class ReviewerActor : ReceiveActor
{
    private readonly RuntimeOptions _options;
    private readonly AgentFrameworkRoleEngine _agentFrameworkRoleEngine;
    private readonly RuntimeTelemetry _telemetry;
    private readonly ILogger _logger;

    public ReviewerActor(
        RuntimeOptions options,
        ILoggerFactory loggerFactory,
        AgentFrameworkRoleEngine agentFrameworkRoleEngine,
        RuntimeTelemetry telemetry)
    {
        _options = options;
        _agentFrameworkRoleEngine = agentFrameworkRoleEngine;
        _telemetry = telemetry;
        _logger = loggerFactory.CreateLogger<ReviewerActor>();

        ReceiveAsync<ExecuteRoleTask>(HandleAsync);
    }

    private async Task HandleAsync(ExecuteRoleTask command)
    {
        var replyTo = Sender;

        using var activity = _telemetry.StartActivity(
            "reviewer.role.execute",
            taskId: command.TaskId,
            role: command.Role.ToString().ToLowerInvariant(),
            tags: new Dictionary<string, object?>
            {
                ["actor.name"] = Self.Path.Name,
                ["engine"] = "microsoft-agent-framework",
            });

        if (command.Role != SwarmRole.Reviewer)
        {
            var error = $"ReviewerActor does not process role {command.Role}";
            activity?.SetStatus(ActivityStatusCode.Error, error);
            replyTo.Tell(new RoleTaskFailed(
                command.TaskId,
                command.Role,
                error,
                DateTimeOffset.UtcNow));
            return;
        }

        if (_options.SimulateReviewerFailure)
        {
            const string error = "Simulated reviewer failure for escalation path testing.";
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
            var confidence = EvaluateQuality(output, adapterId);

            activity?.SetTag("output.length", output.Length);
            activity?.SetTag("quality.confidence", confidence);
            activity?.SetTag("agent.framework.cli.adapter", adapterId);
            activity?.SetStatus(ActivityStatusCode.Ok);

            _logger.LogInformation(
                "Reviewer completed taskId={TaskId} executionMode={ExecutionMode} adapter={AdapterId} confidence={Confidence}",
                command.TaskId,
                _options.AgentFrameworkExecutionMode,
                adapterId,
                confidence);

            // Self-retry if confidence is very low (unless already retried).
            // Retry BEFORE publishing the concern so the supervisor sees the
            // final confidence rather than a stale pre-retry value.
            if (confidence < AgentQualityEvaluator.SelfRetryThreshold && command.PreviousConfidence is null)
            {
                _logger.LogInformation(
                    "Reviewer self-retry triggered taskId={TaskId} confidence={Confidence}",
                    command.TaskId,
                    confidence);

                activity?.SetTag("quality.self_retry", true);

                // Re-execute with adjusted strategy (skip the adapter that produced low quality)
                var adjustedCommand = command with
                {
                    PreferredAdapter = AgentQualityEvaluator.GetAlternativeAdapter(adapterId),
                    PreviousConfidence = confidence
                };

                var retryResult = await _agentFrameworkRoleEngine.ExecuteAsync(adjustedCommand);
                var retryConfidence = EvaluateQuality(retryResult.Output, retryResult.AdapterId);

                _logger.LogInformation(
                    "Reviewer self-retry completed taskId={TaskId} oldConfidence={OldConfidence} newConfidence={NewConfidence}",
                    command.TaskId,
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
            if (confidence < AgentQualityEvaluator.QualityConcernThreshold)
            {
                var concern = AgentQualityEvaluator.BuildQualityConcern(output, confidence, "Review quality concern");

                _logger.LogWarning(
                    "Reviewer quality concern taskId={TaskId} confidence={Confidence} concern={Concern}",
                    command.TaskId,
                    confidence,
                    concern);

                // Notify supervisor of quality concern
                Context.System.EventStream.Publish(new QualityConcern(
                    command.TaskId,
                    command.Role,
                    concern,
                    confidence,
                    DateTimeOffset.UtcNow));

                activity?.SetTag("quality.concern", concern);
            }

            replyTo.Tell(new RoleTaskSucceeded(
                command.TaskId,
                command.Role,
                output,
                DateTimeOffset.UtcNow,
                Self.Path.Name));
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Reviewer failed taskId={TaskId} executionMode={ExecutionMode}",
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
    /// Uses shared helpers from <see cref="AgentQualityEvaluator"/> plus reviewer-specific keyword scoring.
    /// </summary>
    private static double EvaluateQuality(string output, string? adapterId)
    {
        var scores = new List<double>();

        // Factor 1: Output length (normalized — reviewers are typically shorter)
        var lengthScore = Math.Min(output.Length / 300.0, 1.0);
        scores.Add(lengthScore);

        // Factor 2: Review-specific keyword presence
        var keywordScore = EvaluateReviewerKeywords(output);
        scores.Add(keywordScore);

        // Factor 3: Adapter reliability bonus (shared)
        var adapterScore = AgentQualityEvaluator.GetAdapterReliabilityScore(adapterId);
        scores.Add(adapterScore);

        // Factor 4: Structural indicators (shared)
        var structureScore = AgentQualityEvaluator.EvaluateStructure(output);
        scores.Add(structureScore);

        // Weighted average
        var weights = new[] { 0.25, 0.35, 0.15, 0.25 };
        var confidence = scores.Zip(weights, (s, w) => s * w).Sum();

        return Math.Clamp(confidence, 0.0, 1.0);
    }

    private static double EvaluateReviewerKeywords(string output)
    {
        var lowerOutput = output.ToLowerInvariant();
        var keywords = new[]
        {
            "review", "check", "test", "pass", "fail",
            "issue", "concern", "suggestion", "improvement",
            "correct", "incorrect", "valid", "invalid"
        };

        var matches = keywords.Count(k => lowerOutput.Contains(k));
        return (double)matches / keywords.Length;
    }

    private static double EvaluateReviewComprehensiveness(string output)
    {
        var lowerOutput = output.ToLowerInvariant();
        var scores = new List<double>();

        // Has explicit pass/fail verdict
        if (lowerOutput.Contains("pass") || lowerOutput.Contains("fail") ||
            lowerOutput.Contains("approve") || lowerOutput.Contains("reject"))
            scores.Add(1.0);
        else
            scores.Add(0.3);

        // Has actionable feedback
        if (lowerOutput.Contains("should") || lowerOutput.Contains("recommend") ||
            lowerOutput.Contains("suggest") || lowerOutput.Contains("consider"))
            scores.Add(1.0);
        else
            scores.Add(0.5);

        return scores.Average();
    }
}
