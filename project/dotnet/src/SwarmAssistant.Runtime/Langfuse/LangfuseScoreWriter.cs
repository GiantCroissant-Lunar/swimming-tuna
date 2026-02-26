namespace SwarmAssistant.Runtime.Langfuse;

public sealed class LangfuseScoreWriter : ILangfuseScoreWriter
{
    private readonly ILangfuseApiClient _apiClient;
    private readonly ILogger<LangfuseScoreWriter> _logger;

    public LangfuseScoreWriter(ILangfuseApiClient apiClient, ILogger<LangfuseScoreWriter> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    public async Task WriteReviewerVerdictAsync(string traceId, string observationId, bool approved, string? comment, CancellationToken ct)
    {
        try
        {
            var score = new LangfuseScore(
                TraceId: traceId,
                Name: "reviewer_verdict",
                Value: approved ? 1.0 : 0.0,
                ObservationId: observationId,
                Comment: comment
            );

            await _apiClient.PostScoreAsync(score, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write reviewer verdict for trace {TraceId}, observation {ObservationId}", traceId, observationId);
        }
    }

    public async Task WriteGatekeeperFixCountAsync(string traceId, int fixCount, CancellationToken ct)
    {
        try
        {
            var score = new LangfuseScore(
                TraceId: traceId,
                Name: "gatekeeper_fixes",
                Value: fixCount
            );

            await _apiClient.PostScoreAsync(score, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write gatekeeper fix count for trace {TraceId}", traceId);
        }
    }
}
