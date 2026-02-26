namespace SwarmAssistant.Runtime.Langfuse;

public interface ILangfuseScoreWriter
{
    Task WriteReviewerVerdictAsync(string traceId, string observationId, bool approved, string? comment, CancellationToken ct);
    Task WriteGatekeeperFixCountAsync(string traceId, int fixCount, CancellationToken ct);
}
