using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Actors;

/// <summary>
/// Shared quality-evaluation helpers used by <see cref="WorkerActor"/> and <see cref="ReviewerActor"/>.
/// Centralises thresholds, adapter reliability scores, structural heuristics, and the
/// round-robin alternative-adapter selection so the two actors stay in sync.
/// </summary>
internal static class QualityEvaluator
{
    /// <summary>Confidence below this triggers a QualityConcern raised to the supervisor.</summary>
    internal const double QualityConcernThreshold = 0.5;

    /// <summary>Confidence below this triggers an actor-level self-retry.</summary>
    internal const double SelfRetryThreshold = 0.3;

    private static readonly string[] AdapterSequence = ["copilot", "kimi", "cline", "local-echo"];

    /// <summary>
    /// Returns a reliability bonus score [0.0-1.0] for the given adapter ID.
    /// </summary>
    internal static double GetAdapterReliabilityScore(string? adapterId)
    {
        if (string.IsNullOrWhiteSpace(adapterId)) return 0.5;

        return adapterId.ToLowerInvariant() switch
        {
            "copilot" => 0.85,
            "kimi" => 0.80,
            "cline" => 0.75,
            "local-echo" => 0.50,
            _ => 0.60
        };
    }

    /// <summary>
    /// Evaluates structural indicators (code blocks, bullet points, headers) in <paramref name="output"/>.
    /// Returns 0.5 (neutral) for <see cref="SwarmRole.Orchestrator"/> because its output is intentionally
    /// terse and would otherwise be penalised by structural heuristics.
    /// </summary>
    internal static double EvaluateStructure(string output, SwarmRole role)
    {
        // Orchestrator output is intentionally short (ACTION / REASON lines) — structural
        // indicators don't apply and would unfairly lower the confidence score.
        if (role == SwarmRole.Orchestrator)
        {
            return 0.5;
        }

        var scores = new List<double>();

        scores.Add(output.Contains("```") ? 1.0 : 0.5);
        scores.Add(output.Contains("- ") || output.Contains("1. ") ? 1.0 : 0.5);
        scores.Add(output.Contains("# ") || output.Contains("## ") ? 1.0 : 0.5);

        return scores.Average();
    }

    /// <summary>
    /// Returns the next adapter in the round-robin sequence after <paramref name="currentAdapter"/>.
    /// Wraps around so every configured adapter is exercised during retries.
    /// If <paramref name="currentAdapter"/> is unknown, starts from the first adapter in the sequence.
    /// </summary>
    internal static string? GetAlternativeAdapter(string? currentAdapter)
    {
        var index = Array.FindIndex(
            AdapterSequence,
            a => a.Equals(currentAdapter, StringComparison.OrdinalIgnoreCase));

        // When the current adapter is not found (index == -1) start from index 0;
        // otherwise advance to the next position in the round-robin.
        var nextIndex = index < 0 ? 0 : (index + 1) % AdapterSequence.Length;
        return AdapterSequence[nextIndex];
    }
}
