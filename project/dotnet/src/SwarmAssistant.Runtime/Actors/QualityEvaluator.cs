using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Actors;

/// <summary>
/// Shared quality evaluation utilities used by WorkerActor and ReviewerActor.
/// Centralizes confidence scoring, threshold constants, adapter reliability,
/// and structural analysis to avoid duplication across actor types.
/// </summary>
internal static class QualityEvaluator
{
    // ── Shared thresholds ───────────────────────────────────────────────
    /// <summary>
    /// Confidence below this value triggers a QualityConcern publication.
    /// Also used by TaskCoordinatorActor to gate review-pass decisions.
    /// </summary>
    public const double QualityConcernThreshold = 0.5;

    /// <summary>
    /// Confidence below this value triggers an in-actor self-retry.
    /// Also referenced by TaskCoordinatorActor for high-failure-rate detection.
    /// </summary>
    public const double SelfRetryThreshold = 0.3;

    // ── Adapter reliability scores ──────────────────────────────────────
    public static double GetAdapterReliabilityScore(string? adapterId)
    {
        if (string.IsNullOrWhiteSpace(adapterId)) return 0.5;

        return adapterId.ToLowerInvariant() switch
        {
            "copilot" => 0.85,
            "kimi" => 0.80,
            "kilo" => 0.80,
            "cline" => 0.75,
            "local-echo" => 0.50,
            _ => 0.60
        };
    }

    // ── Structural evaluation ───────────────────────────────────────────
    public static double EvaluateStructure(string output, SwarmRole role)
    {
        // Orchestrator output is intentionally short (ACTION / REASON lines) — structural
        // indicators don't apply and would unfairly lower the confidence score.
        if (role == SwarmRole.Orchestrator)
        {
            return 0.5;
        }

        var scores = new List<double>();

        // Has code blocks
        scores.Add(output.Contains("```") ? 1.0 : 0.5);

        // Has bullet points or numbered lists
        scores.Add(output.Contains("- ") || output.Contains("1. ") ? 1.0 : 0.5);

        // Has sections (headers)
        scores.Add(output.Contains("# ") || output.Contains("## ") ? 1.0 : 0.5);

        return scores.Average();
    }

    // ── Concern description builder ─────────────────────────────────────
    public static string BuildQualityConcern(string output, double confidence, string prefix = "Quality concern")
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

        return $"{prefix} ({confidence:F2}): {string.Join(", ", concerns)}";
    }

    // ── Alternative adapter selection ───────────────────────────────────
    private static readonly string[] DefaultAdapters = ["copilot", "kimi", "kilo", "cline", "local-echo"];

    public static string? GetAlternativeAdapter(string? currentAdapter, IReadOnlyList<string>? configuredAdapters = null)
    {
        var adapters = GetAdapterRotation(configuredAdapters);
        var index = Array.FindIndex(
            adapters,
            a => a.Equals(currentAdapter, StringComparison.OrdinalIgnoreCase));

        // When the current adapter is not found (index == -1) start from index 0;
        // otherwise advance to the next position in the round-robin.
        var nextIndex = index < 0 ? 0 : (index + 1) % adapters.Length;
        return adapters[nextIndex];
    }

    private static string[] GetAdapterRotation(IReadOnlyList<string>? configuredAdapters)
    {
        if (configuredAdapters is null || configuredAdapters.Count == 0)
        {
            return DefaultAdapters;
        }

        var adapters = configuredAdapters
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return adapters.Length > 0 ? adapters : DefaultAdapters;
    }
}
