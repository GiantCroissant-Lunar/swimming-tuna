using System.Collections.Concurrent;

namespace SwarmAssistant.Runtime.Tasks;

public sealed record RunEntry(string RunId, string? Title, DateTimeOffset CreatedAt);

public sealed class RunRegistry
{
    private readonly ConcurrentDictionary<string, RunEntry> _runs = new(StringComparer.Ordinal);

    public RunEntry CreateRun(string? runId = null, string? title = null)
    {
        var id = string.IsNullOrWhiteSpace(runId) ? $"run-{Guid.NewGuid():N}" : runId.Trim();
        return _runs.GetOrAdd(id, _ => new RunEntry(id, title, DateTimeOffset.UtcNow));
    }

    public RunEntry? GetRun(string runId)
    {
        _runs.TryGetValue(runId, out var entry);
        return entry;
    }
}
