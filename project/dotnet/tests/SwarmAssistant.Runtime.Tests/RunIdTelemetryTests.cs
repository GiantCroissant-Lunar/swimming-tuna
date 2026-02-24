using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime.Tests;

/// <summary>
/// Tests verifying that runId is correctly promoted to the Langfuse session key
/// and that swarm.run.id / swarm.task.id tags are set on telemetry spans.
/// Phase 6, Issue 15: Correlate Run/Task IDs with Langfuse Session and Trace Tags.
/// </summary>
public sealed class RunIdTelemetryTests : IDisposable
{
    private readonly RuntimeTelemetry _telemetry;
    private readonly List<Activity> _capturedActivities = new();
    private readonly ActivityListener _listener;

    public RunIdTelemetryTests()
    {
        var options = new RuntimeOptions { Profile = "CI" };
        _telemetry = new RuntimeTelemetry(options, NullLoggerFactory.Instance);

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RuntimeTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _capturedActivities.Add(activity),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    // ── langfuse.session.id fallback ─────────────────────────────────────────

    [Fact]
    public void StartActivity_WithTaskIdOnly_SetsLangfuseSessionIdToTaskId()
    {
        var taskId = $"task-{Guid.NewGuid():N}";

        using var activity = _telemetry.StartActivity("test.span", taskId: taskId);

        Assert.NotNull(activity);
        Assert.Equal(taskId, activity!.GetTagItem("langfuse.session.id"));
        Assert.Equal(taskId, activity.GetTagItem("swarm.task.id"));
        Assert.Null(activity.GetTagItem("swarm.run.id"));
    }

    // ── langfuse.session.id promoted to runId ────────────────────────────────

    [Fact]
    public void StartActivity_WithRunId_SetsLangfuseSessionIdToRunId()
    {
        var taskId = $"task-{Guid.NewGuid():N}";
        var runId = $"run-{Guid.NewGuid():N}";

        using var activity = _telemetry.StartActivity("test.span", taskId: taskId, runId: runId);

        Assert.NotNull(activity);
        Assert.Equal(runId, activity!.GetTagItem("langfuse.session.id"));
    }

    // ── swarm.run.id tag ──────────────────────────────────────────────────────

    [Fact]
    public void StartActivity_WithRunId_SetsSwarmRunIdTag()
    {
        var taskId = $"task-{Guid.NewGuid():N}";
        var runId = $"run-{Guid.NewGuid():N}";

        using var activity = _telemetry.StartActivity("test.span", taskId: taskId, runId: runId);

        Assert.NotNull(activity);
        Assert.Equal(runId, activity!.GetTagItem("swarm.run.id"));
    }

    // ── swarm.task.id always set ──────────────────────────────────────────────

    [Fact]
    public void StartActivity_WithRunId_AlsoSetsSwarmTaskIdTag()
    {
        var taskId = $"task-{Guid.NewGuid():N}";
        var runId = $"run-{Guid.NewGuid():N}";

        using var activity = _telemetry.StartActivity("test.span", taskId: taskId, runId: runId);

        Assert.NotNull(activity);
        Assert.Equal(taskId, activity!.GetTagItem("swarm.task.id"));
    }

    // ── swarm.role tag ────────────────────────────────────────────────────────

    [Fact]
    public void StartActivity_WithRole_SetsSwarmRoleTag()
    {
        using var activity = _telemetry.StartActivity("test.span", role: "planner");

        Assert.NotNull(activity);
        Assert.Equal("planner", activity!.GetTagItem("swarm.role"));
    }

    // ── no runId → no swarm.run.id tag ───────────────────────────────────────

    [Fact]
    public void StartActivity_WithoutRunId_DoesNotSetSwarmRunIdTag()
    {
        using var activity = _telemetry.StartActivity("test.span", taskId: $"task-{Guid.NewGuid():N}");

        Assert.NotNull(activity);
        Assert.Null(activity!.GetTagItem("swarm.run.id"));
    }

    // ── no taskId or runId → no langfuse.session.id ──────────────────────────

    [Fact]
    public void StartActivity_WithoutTaskIdOrRunId_DoesNotSetLangfuseSessionId()
    {
        using var activity = _telemetry.StartActivity("test.span");

        Assert.NotNull(activity);
        Assert.Null(activity!.GetTagItem("langfuse.session.id"));
    }

    // ── ExecuteRoleTask carries RunId ─────────────────────────────────────────

    [Fact]
    public void ExecuteRoleTask_RunIdFieldDefaultsToNull()
    {
        var command = new ExecuteRoleTask("t-1", SwarmRole.Planner, "T", "D", null, null);

        Assert.Null(command.RunId);
    }

    [Fact]
    public void ExecuteRoleTask_RunIdFieldCanBeSet()
    {
        var runId = $"run-{Guid.NewGuid():N}";
        var command = new ExecuteRoleTask("t-1", SwarmRole.Planner, "T", "D", null, null, RunId: runId);

        Assert.Equal(runId, command.RunId);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _telemetry.Dispose();
    }
}
