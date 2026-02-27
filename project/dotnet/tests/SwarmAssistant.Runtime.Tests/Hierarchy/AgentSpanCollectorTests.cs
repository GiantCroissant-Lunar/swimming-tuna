using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using SwarmAssistant.Contracts.Hierarchy;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Hierarchy;
using Xunit;

namespace SwarmAssistant.Runtime.Tests.Hierarchy;

public sealed class AgentSpanCollectorTests
{
    [Fact]
    public void StartSpan_CreatesRootSpanWithLevelZero()
    {
        var collector = new AgentSpanCollector();

        var span = collector.StartSpan("task-1", "run-1", SwarmRole.Planner,
            AgentSpanKind.Coordinator, null, "kilo");

        span.SpanId.Should().Be("span-1");
        span.ParentSpanId.Should().BeNull();
        span.Level.Should().Be(0);
        span.Kind.Should().Be(AgentSpanKind.Coordinator);
        span.TaskId.Should().Be("task-1");
        span.RunId.Should().Be("run-1");
        span.AgentId.Should().Be("kilo");
        span.AdapterId.Should().Be("kilo");
        span.Role.Should().Be(SwarmRole.Planner);
        span.Status.Should().Be(AgentSpanStatus.Running);
        span.StartedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
        span.CompletedAt.Should().BeNull();
        span.Flavor.Should().Be(SubAgentFlavor.None);
    }

    [Fact]
    public void StartSpan_CreatesChildSpanWithComputedLevel()
    {
        var collector = new AgentSpanCollector();

        var parent = collector.StartSpan("task-1", "run-1", SwarmRole.Planner,
            AgentSpanKind.Coordinator, null, "kilo");

        var child = collector.StartSpan("task-1", "run-1", SwarmRole.Builder,
            AgentSpanKind.CliAgent, parent.SpanId, "claude");

        child.SpanId.Should().Be("span-2");
        child.ParentSpanId.Should().Be(parent.SpanId);
        child.Level.Should().Be(1);
        child.Kind.Should().Be(AgentSpanKind.CliAgent);
    }

    [Fact]
    public void StartSpan_WithNonExistentParent_Throws()
    {
        var collector = new AgentSpanCollector();

        Action act = () => collector.StartSpan("task-1", "run-1", SwarmRole.Builder,
            AgentSpanKind.CliAgent, "non-existent-parent", "claude");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Parent span non-existent-parent not found*");
    }

    [Fact]
    public void StartSpan_GeneratesUniqueSpanIds()
    {
        var collector = new AgentSpanCollector();

        var span1 = collector.StartSpan("task-1", "run-1", SwarmRole.Planner,
            AgentSpanKind.Coordinator, null, "kilo");
        var span2 = collector.StartSpan("task-1", "run-1", SwarmRole.Builder,
            AgentSpanKind.CliAgent, null, "claude");

        span1.SpanId.Should().Be("span-1");
        span2.SpanId.Should().Be("span-2");
    }

    [Fact]
    public void CompleteSpan_UpdatesSpanStatusAndMetrics()
    {
        var collector = new AgentSpanCollector();

        var started = collector.StartSpan("task-1", "run-1", SwarmRole.Planner,
            AgentSpanKind.Coordinator, null, "kilo");

        var usage = new TokenUsage
        {
            InputTokens = 1000,
            OutputTokens = 500,
            ReasoningTokens = 200
        };

        var completed = collector.CompleteSpan(started.SpanId, AgentSpanStatus.Completed, usage, 0.015m);

        completed.SpanId.Should().Be(started.SpanId);
        completed.Status.Should().Be(AgentSpanStatus.Completed);
        completed.CompletedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
        completed.Usage.Should().Be(usage);
        completed.CostUsd.Should().Be(0.015m);
        completed.StartedAt.Should().Be(started.StartedAt);
    }

    [Fact]
    public void CompleteSpan_WithAdapterId_UpdatesAgentAndAdapter()
    {
        var collector = new AgentSpanCollector();
        var started = collector.StartSpan("task-1", "run-1", SwarmRole.Builder,
            AgentSpanKind.CliAgent, null, null);

        var completed = collector.CompleteSpan(
            started.SpanId,
            AgentSpanStatus.Completed,
            adapterId: "kilo");

        completed.AdapterId.Should().Be("kilo");
        completed.AgentId.Should().Be("kilo");
    }

    [Fact]
    public void CompleteSpan_WithNonExistentSpan_Throws()
    {
        var collector = new AgentSpanCollector();

        Action act = () => collector.CompleteSpan("non-existent", AgentSpanStatus.Completed);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Span non-existent not found*");
    }

    [Fact]
    public void GetFlat_ReturnsSpansForTask()
    {
        var collector = new AgentSpanCollector();

        var span1 = collector.StartSpan("task-1", "run-1", SwarmRole.Planner,
            AgentSpanKind.Coordinator, null, "kilo");
        var span2 = collector.StartSpan("task-1", "run-1", SwarmRole.Builder,
            AgentSpanKind.CliAgent, span1.SpanId, "claude");
        collector.StartSpan("task-2", "run-1", SwarmRole.Builder,
            AgentSpanKind.CliAgent, null, "claude");

        var flat = collector.GetFlat("task-1");

        flat.Should().HaveCount(2);
        flat.Should().Contain(s => s.SpanId == span1.SpanId);
        flat.Should().Contain(s => s.SpanId == span2.SpanId);
    }

    [Fact]
    public void GetFlat_ReturnsEmptyListForNonExistentTask()
    {
        var collector = new AgentSpanCollector();

        var flat = collector.GetFlat("non-existent");

        flat.Should().BeEmpty();
    }

    [Fact]
    public void GetByRun_ReturnsSpansForRun()
    {
        var collector = new AgentSpanCollector();

        collector.StartSpan("task-1", "run-1", SwarmRole.Planner,
            AgentSpanKind.Coordinator, null, "kilo");
        collector.StartSpan("task-2", "run-1", SwarmRole.Builder,
            AgentSpanKind.CliAgent, null, "claude");
        collector.StartSpan("task-3", "run-2", SwarmRole.Reviewer,
            AgentSpanKind.CliAgent, null, "copilot");

        var runSpans = collector.GetByRun("run-1");

        runSpans.Should().HaveCount(2);
        runSpans.Should().OnlyContain(s => s.RunId == "run-1");
    }

    [Fact]
    public void GetByRun_ReturnsEmptyListForNonExistentRun()
    {
        var collector = new AgentSpanCollector();

        var runSpans = collector.GetByRun("non-existent");

        runSpans.Should().BeEmpty();
    }

    [Fact]
    public void GetTree_BuildsHierarchyFromParentSpanId()
    {
        var collector = new AgentSpanCollector();

        var root = collector.StartSpan("task-1", "run-1", SwarmRole.Planner,
            AgentSpanKind.Coordinator, null, "kilo");
        var child1 = collector.StartSpan("task-1", "run-1", SwarmRole.Builder,
            AgentSpanKind.CliAgent, root.SpanId, "claude");
        var child2 = collector.StartSpan("task-1", "run-1", SwarmRole.Builder,
            AgentSpanKind.CliAgent, root.SpanId, "copilot");
        var grandchild = collector.StartSpan("task-1", "run-1", null,
            AgentSpanKind.SubAgent, child1.SpanId, "claude");

        var tree = collector.GetTree("task-1");

        tree.Should().NotBeNull();
        tree!.Span.SpanId.Should().Be(root.SpanId);
        tree.Children.Should().HaveCount(2);

        var treeChild1 = tree.Children.FirstOrDefault(c => c.Span.SpanId == child1.SpanId);
        treeChild1.Should().NotBeNull();
        treeChild1!.Children.Should().HaveCount(1);
        treeChild1.Children[0].Span.SpanId.Should().Be(grandchild.SpanId);

        var treeChild2 = tree.Children.FirstOrDefault(c => c.Span.SpanId == child2.SpanId);
        treeChild2.Should().NotBeNull();
        treeChild2!.Children.Should().BeEmpty();
    }

    [Fact]
    public void GetTree_WithNoSpans_ReturnsNull()
    {
        var collector = new AgentSpanCollector();

        var tree = collector.GetTree("non-existent");

        tree.Should().BeNull();
    }

    [Fact]
    public void GetTree_WithMultipleRoots_Throws()
    {
        var collector = new AgentSpanCollector();

        collector.StartSpan("task-1", "run-1", SwarmRole.Planner,
            AgentSpanKind.Coordinator, null, "kilo");
        collector.StartSpan("task-1", "run-1", SwarmRole.Builder,
            AgentSpanKind.CliAgent, null, "claude");

        Action act = () => collector.GetTree("task-1");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Expected one root span for task 'task-1', found 2.*");
    }

    [Fact]
    public void DetectFlavor_TaskWithResume_ReturnsCoWork()
    {
        var toolArgs = JsonSerializer.Deserialize<JsonElement>(@"{""resume"": ""task-abc123""}");

        var flavor = AgentSpanCollector.DetectFlavor("Task", toolArgs);

        flavor.Should().Be(SubAgentFlavor.CoWork);
    }

    [Fact]
    public void DetectFlavor_TaskWithoutResume_ReturnsNormal()
    {
        var toolArgs = JsonSerializer.Deserialize<JsonElement>(@"{""prompt"": ""do something""}");

        var flavor = AgentSpanCollector.DetectFlavor("Task", toolArgs);

        flavor.Should().Be(SubAgentFlavor.Normal);
    }

    [Fact]
    public void DetectFlavor_NonTaskTool_ReturnsNormal()
    {
        var toolArgs = JsonSerializer.Deserialize<JsonElement>(@"{""command"": ""ls""}");

        var flavor = AgentSpanCollector.DetectFlavor("Bash", toolArgs);

        flavor.Should().Be(SubAgentFlavor.Normal);
    }

    [Fact]
    public void DetectFlavor_NullToolArgs_ReturnsNormal()
    {
        var flavor = AgentSpanCollector.DetectFlavor("Task", null);

        flavor.Should().Be(SubAgentFlavor.Normal);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentStartOperations()
    {
        var collector = new AgentSpanCollector();
        var tasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            int index = i;
            tasks.Add(Task.Run(() =>
            {
                collector.StartSpan($"task-{index % 10}", "run-1", SwarmRole.Builder,
                    AgentSpanKind.CliAgent, null, "claude");
            }));
        }

        await Task.WhenAll(tasks);

        collector.GetByRun("run-1").Should().HaveCount(100);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentStartAndCompleteOperations()
    {
        var collector = new AgentSpanCollector();
        var spanIds = new ConcurrentBag<string>();
        var tasks = new List<Task>();

        for (int i = 0; i < 50; i++)
        {
            int index = i;
            tasks.Add(Task.Run(() =>
            {
                var span = collector.StartSpan("task-1", "run-1", SwarmRole.Builder,
                    AgentSpanKind.CliAgent, null, "claude");
                spanIds.Add(span.SpanId);
            }));
        }

        await Task.WhenAll(tasks);

        foreach (var spanId in spanIds)
        {
            collector.CompleteSpan(spanId, AgentSpanStatus.Completed);
        }

        var flat = collector.GetFlat("task-1");
        flat.Should().HaveCount(50);
        flat.Should().OnlyContain(s => s.Status == AgentSpanStatus.Completed);
    }

    [Fact]
    public void GetFlat_OrdersByStartTime()
    {
        var timeProvider = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var collector = new AgentSpanCollector(timeProvider);

        var span1 = collector.StartSpan("task-1", "run-1", SwarmRole.Planner,
            AgentSpanKind.Coordinator, null, "kilo");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var span2 = collector.StartSpan("task-1", "run-1", SwarmRole.Builder,
            AgentSpanKind.CliAgent, span1.SpanId, "claude");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var span3 = collector.StartSpan("task-1", "run-1", SwarmRole.Reviewer,
            AgentSpanKind.CliAgent, span1.SpanId, "copilot");

        var flat = collector.GetFlat("task-1");

        flat.Should().HaveCount(3);
        flat[0].SpanId.Should().Be(span1.SpanId);
        flat[1].SpanId.Should().Be(span2.SpanId);
        flat[2].SpanId.Should().Be(span3.SpanId);
    }

    private sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
