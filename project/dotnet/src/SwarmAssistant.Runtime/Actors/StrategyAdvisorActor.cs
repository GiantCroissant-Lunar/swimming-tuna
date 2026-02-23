using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Runtime.Tasks;

namespace SwarmAssistant.Runtime.Actors;

/// <summary>
/// Actor that provides strategy advice based on historical task outcomes.
/// Queries ArcadeDB for similar tasks and generates recommendations for task planning.
/// </summary>
public sealed class StrategyAdvisorActor : ReceiveActor
{
    private readonly IOutcomeReader _outcomeReader;
    private readonly ILogger _logger;

    // Cache recent advice to reduce database queries
    private readonly Dictionary<string, CachedAdvice> _adviceCache = new();
    private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);

    public StrategyAdvisorActor(
        IOutcomeReader outcomeReader,
        ILoggerFactory loggerFactory)
    {
        _outcomeReader = outcomeReader;
        _logger = loggerFactory.CreateLogger<StrategyAdvisorActor>();

        Receive<StrategyAdviceRequest>(OnStrategyAdviceRequest);
        Receive<ClearAdviceCache>(OnClearAdviceCache);
    }

    private void OnStrategyAdviceRequest(StrategyAdviceRequest request)
    {
        var sender = Sender;

        // Check cache first
        var cacheKey = BuildCacheKey(request);
        if (_adviceCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.Timestamp < _cacheTtl)
        {
            _logger.LogDebug(
                "Returning cached advice taskId={TaskId}",
                request.TaskId);
            sender.Tell(cached.Advice);
            return;
        }

        // Generate new advice asynchronously
        GenerateAdviceAsync(request)
            .PipeTo(
                recipient: sender,
                success: advice =>
                {
                    // Cache the result
                    _adviceCache[cacheKey] = new CachedAdvice(advice, DateTimeOffset.UtcNow);
                    return advice;
                },
                failure: exception =>
                {
                    _logger.LogWarning(
                        exception,
                        "Failed to generate strategy advice taskId={TaskId}",
                        request.TaskId);
                    return new StrategyAdvice
                    {
                        TaskId = request.TaskId,
                        SimilarTaskSuccessRate = 0.5,
                        SimilarTaskCount = 0
                    };
                });
    }

    private void OnClearAdviceCache(ClearAdviceCache _)
    {
        _adviceCache.Clear();
        _logger.LogDebug("Advice cache cleared");
    }

    private async Task<StrategyAdvice> GenerateAdviceAsync(StrategyAdviceRequest request)
    {
        var keywords = OutcomeTracker.ExtractKeywords(request.Title);
        var similarOutcomes = await _outcomeReader.FindSimilarAsync(keywords, limit: 20);

        if (similarOutcomes.Count == 0)
        {
            _logger.LogDebug(
                "No similar tasks found for taskId={TaskId} keywords={Keywords}",
                request.TaskId, string.Join(", ", keywords));

            return new StrategyAdvice
            {
                TaskId = request.TaskId,
                SimilarTaskSuccessRate = 0.5,
                SimilarTaskCount = 0
            };
        }

        var insights = new List<string>();
        var adapterStats = new Dictionary<string, List<(bool Succeeded, double Confidence)>>();

        // Calculate success rate for similar tasks
        var successCount = 0;
        var totalRetries = 0;
        var reviewRejections = 0;

        foreach (var outcome in similarOutcomes)
        {
            if (outcome.FinalStatus == Contracts.Tasks.TaskStatus.Done)
            {
                successCount++;
            }

            totalRetries += outcome.TotalRetries;

            // Track adapter performance
            foreach (var roleExec in outcome.RoleExecutions)
            {
                if (roleExec.AdapterUsed is { } adapter)
                {
                    if (!adapterStats.ContainsKey(adapter))
                    {
                        adapterStats[adapter] = new();
                    }
                    adapterStats[adapter].Add((roleExec.Succeeded, roleExec.Confidence));
                }
            }

            // Track review rejections
            var reviewerExecution = outcome.RoleExecutions
                .FirstOrDefault(r => r.Role == Contracts.Messaging.SwarmRole.Reviewer);
            if (reviewerExecution is { Succeeded: false })
            {
                reviewRejections++;
            }
        }

        var successRate = (double)successCount / similarOutcomes.Count;
        var avgRetries = (double)totalRetries / similarOutcomes.Count;
        var reviewRejectionRate = similarOutcomes.Count > 0
            ? (double)reviewRejections / similarOutcomes.Count
            : 0;

        // Generate insights
        if (successRate >= 0.8)
        {
            insights.Add($"Tasks with similar keywords have high success rate ({successRate:P0}).");
        }
        else if (successRate < 0.5)
        {
            insights.Add($"Tasks with similar keywords have low success rate ({successRate:P0}). Consider additional planning.");
        }

        // Adapter recommendations
        var adapterSuccessRates = new Dictionary<string, double>();
        foreach (var (adapter, stats) in adapterStats)
        {
            var adapterSuccessCount = stats.Count(s => s.Succeeded);
            var rate = (double)adapterSuccessCount / stats.Count;
            adapterSuccessRates[adapter] = rate;

            if (stats.Count >= 3 && rate >= 0.7)
            {
                insights.Add($"Adapter '{adapter}' has {rate:P0} success rate for similar tasks.");
            }
        }

        // Review rejection insight
        if (reviewRejectionRate >= 0.3)
        {
            insights.Add($"Review rejection rate for similar tasks is {reviewRejectionRate:P0}. Plan more carefully.");
        }

        // Retry insight
        if (avgRetries >= 1.5)
        {
            insights.Add($"Similar tasks average {avgRetries:F1} retries. Build robustness.");
        }

        // Common failure patterns
        var failurePatterns = similarOutcomes
            .Where(o => o.FailureReason is not null)
            .GroupBy(o => ExtractFailurePattern(o.FailureReason!))
            .Where(g => g.Count() >= 2)
            .Select(g => g.Key)
            .Take(3)
            .ToList();

        // Generate cost adjustments based on historical data
        var costAdjustments = GenerateCostAdjustments(
            successRate,
            reviewRejectionRate,
            avgRetries);

        _logger.LogInformation(
            "Generated strategy advice taskId={TaskId} similarTasks={Count} successRate={Rate:P0}",
            request.TaskId, similarOutcomes.Count, successRate);

        return new StrategyAdvice
        {
            TaskId = request.TaskId,
            SimilarTaskSuccessRate = successRate,
            SimilarTaskCount = similarOutcomes.Count,
            AdapterSuccessRates = adapterSuccessRates,
            RecommendedCostAdjustments = costAdjustments,
            Insights = insights,
            ReviewRejectionRate = reviewRejectionRate,
            AverageRetryCount = avgRetries,
            CommonFailurePatterns = failurePatterns
        };
    }

    private static Dictionary<string, double> GenerateCostAdjustments(
        double successRate,
        double reviewRejectionRate,
        double avgRetries)
    {
        var adjustments = new Dictionary<string, double>();

        // If success rate is low, increase cost of direct execution paths
        if (successRate < 0.5)
        {
            adjustments["Build"] = 1.5; // Make Build more expensive, encouraging more planning
            adjustments["Plan"] = 0.8;  // Encourage planning
        }

        // If review rejection rate is high, make Review more expensive (to encourage better build quality)
        if (reviewRejectionRate >= 0.3)
        {
            adjustments["Review"] = 1.3;
            adjustments["Build"] = 0.9; // Encourage better build preparation
        }

        // If retries are common, add cost to quick execution
        if (avgRetries >= 1.5)
        {
            adjustments["Rework"] = 1.2;
        }

        return adjustments;
    }

    private static string ExtractFailurePattern(string failureReason)
    {
        // Extract key words from failure reason for pattern matching
        var words = failureReason
            .Split(new[] { ' ', '\n', '\r', ':', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 4)
            .Take(5)
            .ToArray();

        return string.Join(" ", words);
    }

    private static string BuildCacheKey(StrategyAdviceRequest request)
    {
        var keywords = OutcomeTracker.ExtractKeywords(request.Title);
        return string.Join("|", keywords);
    }

    private sealed record CachedAdvice(StrategyAdvice Advice, DateTimeOffset Timestamp);
}

/// <summary>
/// Request for strategy advice based on historical outcomes.
/// </summary>
public sealed record StrategyAdviceRequest
{
    public required string TaskId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// Message to clear the advice cache.
/// </summary>
public sealed record ClearAdviceCache;

/// <summary>
/// Interface for reading historical task outcomes.
/// </summary>
public interface IOutcomeReader
{
    /// <summary>
    /// Finds similar task outcomes based on keywords.
    /// </summary>
    Task<IReadOnlyList<TaskOutcome>> FindSimilarAsync(
        IReadOnlyList<string> keywords,
        int limit = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific outcome by task ID.
    /// </summary>
    Task<TaskOutcome?> GetAsync(string taskId, CancellationToken cancellationToken = default);
}
