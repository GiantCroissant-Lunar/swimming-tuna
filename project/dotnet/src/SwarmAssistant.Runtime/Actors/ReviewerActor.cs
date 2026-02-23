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
            var output = await _agentFrameworkRoleEngine.ExecuteAsync(command);
            var confidence = EvaluateQuality(output, command.PreferredAdapter);

            activity?.SetTag("output.length", output.Length);
            activity?.SetTag("quality.confidence", confidence);
            activity?.SetStatus(ActivityStatusCode.Ok);

            _logger.LogInformation(
                "Reviewer completed taskId={TaskId} executionMode={ExecutionMode} confidence={Confidence}",
                command.TaskId,
                _options.AgentFrameworkExecutionMode,
                confidence);

            // Raise QualityConcern for borderline cases
            if (confidence < AgentQualityEvaluator.QualityConcernThreshold)
            {
                var concern = BuildQualityConcern(output, confidence);
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

            // Self-retry if confidence is very low (unless already retried)
            if (confidence < AgentQualityEvaluator.SelfRetryThreshold && command.MaxConfidence is null)
            {
                _logger.LogInformation(
                    "Reviewer self-retry triggered taskId={TaskId} confidence={Confidence}",
                    command.TaskId,
                    confidence);

                activity?.SetTag("quality.self_retry", true);

                // Re-execute with adjusted strategy (skip the adapter that produced low quality)
                var adjustedCommand = command with
                {
                    PreferredAdapter = AgentQualityEvaluator.GetAlternativeAdapter(command.PreferredAdapter),
                    MaxConfidence = confidence
                };

                output = await _agentFrameworkRoleEngine.ExecuteAsync(adjustedCommand);
                confidence = EvaluateQuality(output, adjustedCommand.PreferredAdapter);

                _logger.LogInformation(
                    "Reviewer self-retry completed taskId={TaskId} newConfidence={Confidence}",
                    command.TaskId,
                    confidence);

                activity?.SetTag("quality.confidence_after_retry", confidence);
            }

            replyTo.Tell(new RoleTaskSucceeded(
                command.TaskId,
                command.Role,
                output,
                DateTimeOffset.UtcNow,
                confidence));
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
    /// Confidence is derived from output length, keyword presence, and adapter type.
    /// </summary>
    private static double EvaluateQuality(string output, string? adapterId)
    {
        var scores = new List<double>();

        // Factor 1: Output length (normalized)
        var lengthScore = Math.Min(output.Length / 300.0, 1.0);
        scores.Add(lengthScore);

        // Factor 2: Review-specific keyword presence
        var keywordScore = EvaluateReviewerKeywords(output);
        scores.Add(keywordScore);

        // Factor 3: Adapter reliability bonus
        var adapterScore = AgentQualityEvaluator.GetAdapterReliabilityScore(adapterId);
        scores.Add(adapterScore);

        // Factor 4: Structural indicators (has code blocks, bullet points, etc.)
        var structureScore = AgentQualityEvaluator.EvaluateStructure(output);
        scores.Add(structureScore);

        // Factor 5: Review comprehensiveness (pass/fail indicators)
        var comprehensivenessScore = EvaluateReviewComprehensiveness(output);
        scores.Add(comprehensivenessScore);

        // Weighted average (can be tuned)
        var weights = new[] { 0.20, 0.25, 0.15, 0.20, 0.20 };
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

    private static string BuildQualityConcern(string output, double confidence)
    {
        var concerns = new List<string>();

        if (output.Length < 50)
            concerns.Add("review too short");

        if (output.Length > 5000)
            concerns.Add("review excessively long");

        var lowerOutput = output.ToLowerInvariant();
        if (!lowerOutput.Contains("pass") && !lowerOutput.Contains("fail"))
            concerns.Add("no clear verdict");

        if (!lowerOutput.Contains("should") && !lowerOutput.Contains("recommend"))
            concerns.Add("no actionable feedback");

        if (concerns.Count == 0)
            concerns.Add("low confidence score");

        return $"Review quality concern ({confidence:F2}): {string.Join(", ", concerns)}";
    }
}
