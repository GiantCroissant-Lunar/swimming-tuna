using System.Collections.Concurrent;

namespace SwarmAssistant.Runtime.Tasks;

public sealed record RunEntry(string RunId, string? Title, DateTimeOffset CreatedAt);

public sealed class RunRegistry
{
    private readonly ConcurrentDictionary<string, RunEntry> _runs = new(StringComparer.Ordinal);

    public RunEntry CreateRun(string? runId = null, string? title = null)
    {
        var id = string.IsNullOrWhiteSpace(runId) ? $"run-{Guid.NewGuid():N}" : runId.Trim();
        var entry = new RunEntry(id, title, DateTimeOffset.UtcNow);
        return _runs.TryAdd(id, entry) ? entry : _runs[id];
    }

    public RunEntry? GetRun(string runId)
    {
        _runs.TryGetValue(runId, out var entry);
        return entry;
    }
}
