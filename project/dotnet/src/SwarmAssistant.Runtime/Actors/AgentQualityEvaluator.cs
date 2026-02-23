namespace SwarmAssistant.Runtime.Actors;

/// <summary>
/// Shared quality evaluation utilities used by both <see cref="WorkerActor"/> and <see cref="ReviewerActor"/>.
/// Centralises thresholds, adapter reliability scores, structural analysis, and adapter selection.
/// </summary>
internal static class AgentQualityEvaluator
{
    /// <summary>Confidence score below which a QualityConcern is raised.</summary>
    public const double QualityConcernThreshold = 0.5;

    /// <summary>Confidence score below which an automatic self-retry is triggered.</summary>
    public const double SelfRetryThreshold = 0.3;

    // Adapter reliability scores
    private const double CopilotReliability   = 0.85;
    private const double KimiReliability      = 0.80;
    private const double ClineReliability     = 0.75;
    private const double LocalEchoReliability = 0.50;
    private const double DefaultReliability   = 0.60;
    private const double UnknownReliability   = 0.50;

    // Structural scoring weights: present = full credit, absent = partial credit
    private const double StructurePresent = 1.0;
    private const double StructureAbsent  = 0.5;

    private static readonly string[] Adapters = ["copilot", "kimi", "cline", "local-echo"];

    /// <summary>
    /// Returns a reliability score for the given adapter identifier.
    /// </summary>
    public static double GetAdapterReliabilityScore(string? adapterId)
    {
        if (string.IsNullOrWhiteSpace(adapterId)) return UnknownReliability;

        return adapterId.ToLowerInvariant() switch
        {
            "copilot"    => CopilotReliability,
            "kimi"       => KimiReliability,
            "cline"      => ClineReliability,
            "local-echo" => LocalEchoReliability,
            _            => DefaultReliability
        };
    }

    /// <summary>
    /// Evaluates structural quality of the output (code blocks, lists, headers).
    /// </summary>
    public static double EvaluateStructure(string output)
    {
        var scores = new List<double>();

        // Has code blocks
        scores.Add(output.Contains("```") ? StructurePresent : StructureAbsent);

        // Has bullet points or numbered lists
        scores.Add(output.Contains("- ") || output.Contains("1. ") ? StructurePresent : StructureAbsent);

        // Has sections (headers)
        scores.Add(output.Contains("# ") || output.Contains("## ") ? StructurePresent : StructureAbsent);

        return scores.Average();
    }

    /// <summary>
    /// Returns an alternative adapter to use on self-retry, picking the first adapter
    /// that is different from the one that produced the low-confidence output.
    /// </summary>
    public static string? GetAlternativeAdapter(string? currentAdapter)
    {
        return Adapters.FirstOrDefault(a => !a.Equals(currentAdapter, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds a human-readable quality concern summary based on output heuristics.
    /// </summary>
    public static string BuildQualityConcern(string output, double confidence, string prefix = "Quality concern")
    {
        var concerns = new List<string>();

        if (output.Length < 100)
        {
            concerns.Add("output too short");
        }

        if (output.Length > 10000)
        {
            concerns.Add("output excessively long");
        }

        if (!output.Contains("```") && !output.Contains("- ") && !output.Contains("1. "))
        {
            concerns.Add("lacks structure");
        }

        if (concerns.Count == 0)
        {
            concerns.Add("low confidence score");
        }

        return $"{prefix} ({confidence:F2}): {string.Join(", ", concerns)}";
    }
}
