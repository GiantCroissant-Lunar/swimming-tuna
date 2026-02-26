namespace SwarmAssistant.Runtime.Langfuse;

public interface ILangfuseSimilarityQuery
{
    Task<string?> GetSimilarTaskContextAsync(string taskDescription, CancellationToken ct);
}
