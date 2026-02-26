using System.Text;

namespace SwarmAssistant.Runtime.Langfuse;

public sealed class LangfuseSimilarityQuery : ILangfuseSimilarityQuery
{
    private readonly ILangfuseApiClient _apiClient;
    private readonly ILogger<LangfuseSimilarityQuery> _logger;

    public LangfuseSimilarityQuery(
        ILangfuseApiClient apiClient,
        ILogger<LangfuseSimilarityQuery> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    public async Task<string?> GetSimilarTaskContextAsync(string taskDescription, CancellationToken ct)
    {
        try
        {
            var query = new LangfuseTraceQuery(Tags: "role:planner", Limit: 10);
            var traces = await _apiClient.GetTracesAsync(query, ct);

            if (traces.Data.Count == 0)
            {
                _logger.LogDebug("No planner traces found in Langfuse");
                return null;
            }

            var taskWords = SplitIntoWords(taskDescription);
            var similarTraces = traces.Data
                .Select(trace => new
                {
                    Trace = trace,
                    Similarity = CalculateJaccardSimilarity(taskWords, SplitIntoWords(trace.Name))
                })
                .Where(x => x.Similarity > 0.1)
                .OrderByDescending(x => x.Similarity)
                .ToList();

            if (similarTraces.Count == 0)
            {
                _logger.LogDebug("No similar traces found with similarity > 0.1");
                return null;
            }

            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine("--- Langfuse Learning Context ---");
            contextBuilder.AppendLine($"Based on {similarTraces.Count} similar past tasks:");

            foreach (var item in similarTraces.Take(5))
            {
                var safeName = Truncate(item.Trace.Name, 160);
                var safeOutcome = Truncate(ExtractOutcome(item.Trace), 80);
                contextBuilder.AppendLine($"  - \"{safeName}\" -> {safeOutcome}");
            }

            contextBuilder.AppendLine("--- End Langfuse Learning Context ---");

            return contextBuilder.ToString();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Langfuse similarity query timed out or was canceled");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query Langfuse for similar tasks");
            return null;
        }
    }

    private static HashSet<string> SplitIntoWords(string text)
    {
        return text
            .ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '-', '_' },
                StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();
    }

    private static double CalculateJaccardSimilarity(HashSet<string> set1, HashSet<string> set2)
    {
        var intersection = set1.Intersect(set2).Count();
        var union = set1.Union(set2).Count();

        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string ExtractOutcome(LangfuseTrace trace)
    {
        if (trace.Metadata.TryGetValue("outcome", out var outcome) && outcome != null)
        {
            return outcome.ToString() ?? "unknown";
        }

        return "completed";
    }
}
