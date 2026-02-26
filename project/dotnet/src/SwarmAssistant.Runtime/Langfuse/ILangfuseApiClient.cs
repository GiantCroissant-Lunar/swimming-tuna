namespace SwarmAssistant.Runtime.Langfuse;

public interface ILangfuseApiClient
{
    Task PostScoreAsync(LangfuseScore score, CancellationToken ct);
    Task<LangfuseTraceList> GetTracesAsync(LangfuseTraceQuery query, CancellationToken ct);
    Task PostCommentAsync(LangfuseComment comment, CancellationToken ct);
}

public sealed record LangfuseScore(
    string TraceId,
    string Name,
    double Value,
    string? ObservationId = null,
    string? Comment = null,
    string DataType = "NUMERIC"
);

public sealed record LangfuseTraceQuery(
    string? Tags = null,
    int Limit = 10
);

public sealed record LangfuseTraceList(
    IReadOnlyList<LangfuseTrace> Data
);

public sealed record LangfuseTrace(
    string Id,
    string Name,
    Dictionary<string, object?> Metadata,
    DateTime CreatedAt
);

public sealed record LangfuseComment(
    string TraceId,
    string? ObservationId,
    string Content
);
