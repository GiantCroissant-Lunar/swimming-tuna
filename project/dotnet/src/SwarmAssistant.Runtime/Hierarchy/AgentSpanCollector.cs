using System.Collections.Concurrent;
using System.Text.Json;
using SwarmAssistant.Contracts.Hierarchy;
using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Hierarchy;

internal sealed class AgentSpanCollector
{
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, AgentSpan> _spans = new();
    private readonly object _idLock = new();
    private int _nextSpanId = 1;

    public AgentSpanCollector(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public AgentSpan StartSpan(string taskId, string? runId, SwarmRole? role,
        AgentSpanKind kind, string? parentSpanId, string? adapterId)
    {
        int level = 0;
        if (parentSpanId is not null)
        {
            if (!_spans.TryGetValue(parentSpanId, out var parent))
            {
                throw new ArgumentException($"Parent span {parentSpanId} not found", nameof(parentSpanId));
            }

            level = parent.Level + 1;
        }

        var spanId = GenerateSpanId();
        var now = _timeProvider.GetUtcNow();

        var span = new AgentSpan
        {
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            Level = level,
            Kind = kind,
            TaskId = taskId,
            RunId = runId,
            AgentId = adapterId ?? "unknown",
            AdapterId = adapterId,
            Role = role,
            StartedAt = now,
            Status = AgentSpanStatus.Running,
            Flavor = kind == AgentSpanKind.SubAgent ? SubAgentFlavor.Normal : SubAgentFlavor.None
        };

        _spans[spanId] = span;
        return span;
    }

    public AgentSpan CompleteSpan(string spanId, AgentSpanStatus status,
        TokenUsage? usage = null, decimal? costUsd = null, string? adapterId = null)
    {
        if (!_spans.TryGetValue(spanId, out var span))
        {
            throw new ArgumentException($"Span {spanId} not found", nameof(spanId));
        }

        var completedSpan = span with
        {
            CompletedAt = _timeProvider.GetUtcNow(),
            Status = status,
            Usage = usage,
            CostUsd = costUsd,
            AdapterId = adapterId ?? span.AdapterId,
            AgentId = string.IsNullOrWhiteSpace(adapterId) ? span.AgentId : adapterId
        };

        _spans[spanId] = completedSpan;
        return completedSpan;
    }

    public IReadOnlyList<AgentSpan> GetFlat(string taskId)
    {
        return _spans.Values
            .Where(s => s.TaskId == taskId)
            .OrderBy(s => s.StartedAt)
            .ToList()
            .AsReadOnly();
    }

    public IReadOnlyList<AgentSpan> GetByRun(string runId)
    {
        return _spans.Values
            .Where(s => s.RunId == runId)
            .OrderBy(s => s.StartedAt)
            .ToList()
            .AsReadOnly();
    }

    public AgentSpanTree? GetTree(string taskId)
    {
        var taskSpans = _spans.Values
            .Where(s => s.TaskId == taskId)
            .ToDictionary(s => s.SpanId);

        if (taskSpans.Count == 0)
        {
            return null;
        }

        var rootSpans = taskSpans.Values
            .Where(s => s.ParentSpanId == null)
            .OrderBy(s => s.StartedAt)
            .ToList();
        if (rootSpans.Count == 0)
        {
            return null;
        }
        if (rootSpans.Count > 1)
        {
            throw new InvalidOperationException(
                $"Expected one root span for task '{taskId}', found {rootSpans.Count}.");
        }

        return new AgentSpanTree
        {
            Span = rootSpans[0],
            Children = BuildChildren(rootSpans[0].SpanId, taskSpans)
        };
    }

    private static IReadOnlyList<AgentSpanTree> BuildChildren(string parentSpanId, Dictionary<string, AgentSpan> allSpans)
    {
        var children = allSpans.Values
            .Where(s => s.ParentSpanId == parentSpanId)
            .OrderBy(s => s.StartedAt)
            .Select(span => new AgentSpanTree
            {
                Span = span,
                Children = BuildChildren(span.SpanId, allSpans)
            })
            .ToList();

        return children.AsReadOnly();
    }

    public static SubAgentFlavor DetectFlavor(string toolName, JsonElement? toolArgs)
    {
        if (toolName == "Task" && toolArgs != null && toolArgs.Value.TryGetProperty("resume", out _))
        {
            return SubAgentFlavor.CoWork;
        }

        return SubAgentFlavor.Normal;
    }

    private string GenerateSpanId()
    {
        lock (_idLock)
        {
            return $"span-{_nextSpanId++}";
        }
    }
}
