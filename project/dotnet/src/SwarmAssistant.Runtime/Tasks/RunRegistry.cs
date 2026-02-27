using System.Collections.Concurrent;

namespace SwarmAssistant.Runtime.Tasks;

public sealed record RunEntry(
    string RunId,
    string? Title,
    DateTimeOffset CreatedAt,
    string? Document = null,
    string? BaseBranch = null,
    string? BranchPrefix = null,
    string? FeatureBranch = null,
    string? Status = null);

public sealed class RunRegistry
{
    private readonly ConcurrentDictionary<string, RunEntry> _runs = new(StringComparer.Ordinal);

    public RunEntry CreateRun(
        string? runId = null,
        string? title = null,
        string? document = null,
        string? baseBranch = null,
        string? branchPrefix = null)
    {
        var id = string.IsNullOrWhiteSpace(runId) ? $"run-{Guid.NewGuid():N}" : runId.Trim();
        return _runs.GetOrAdd(id, _ => new RunEntry(
            id, title, DateTimeOffset.UtcNow,
            Document: document,
            BaseBranch: baseBranch ?? "main",
            BranchPrefix: branchPrefix ?? "feat",
            Status: "accepted"));
    }

    public RunEntry? GetRun(string runId)
    {
        _runs.TryGetValue(runId, out var entry);
        return entry;
    }

    public IReadOnlyList<RunEntry> ListRuns()
    {
        return _runs.Values.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public bool MarkDone(string runId)
    {
        while (true)
        {
            if (!_runs.TryGetValue(runId, out var entry))
            {
                return false;
            }

            if (_runs.TryUpdate(runId, entry with { Status = "done" }, entry))
            {
                return true;
            }
        }
    }

    public void UpdateFeatureBranch(string runId, string featureBranch)
    {
        while (true)
        {
            if (!_runs.TryGetValue(runId, out var entry))
            {
                return;
            }

            if (_runs.TryUpdate(runId, entry with { FeatureBranch = featureBranch }, entry))
            {
                return;
            }
        }
    }
}
