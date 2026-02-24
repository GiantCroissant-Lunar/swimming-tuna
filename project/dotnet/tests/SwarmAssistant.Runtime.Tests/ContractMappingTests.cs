using System.Text.Json;
using SwarmAssistant.Runtime.Dto;
using SwarmAssistant.Runtime.Tasks;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tests;

/// <summary>
/// Verifies mapping correctness for RunEntry → RunDto and
/// TaskExecutionEvent → TaskExecutionEventDto.
/// These tests intentionally enumerate every field so that a breaking
/// change (field rename, addition, or removal) causes a test failure.
/// </summary>
public sealed class ContractMappingTests
{
    // ---------------------------------------------------------------
    // RunEntry → RunDto
    // ---------------------------------------------------------------

    private static RunEntry BuildRunEntry(string runId = "run-1") =>
        new RunEntry(
            RunId: runId,
            Title: "My Run",
            CreatedAt: new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void RunDto_MapsAllFields()
    {
        var entry = BuildRunEntry();
        RunDto dto = TaskSnapshotMapper.ToDto(entry);

        Assert.Equal("run-1", dto.RunId);
        Assert.Equal("My Run", dto.Title);
        Assert.Equal(entry.CreatedAt, dto.CreatedAt);
    }

    [Fact]
    public void RunDto_WithNullTitle_MapsNullTitle()
    {
        var entry = new RunEntry("run-notitle", null, DateTimeOffset.UtcNow);
        RunDto dto = TaskSnapshotMapper.ToDto(entry);

        Assert.Equal("run-notitle", dto.RunId);
        Assert.Null(dto.Title);
    }

    // ---------------------------------------------------------------
    // TaskExecutionEvent → TaskExecutionEventDto
    // ---------------------------------------------------------------

    private static TaskExecutionEvent BuildEvent(string eventId = "evt-1") =>
        new TaskExecutionEvent(
            EventId: eventId,
            RunId: "run-42",
            TaskId: "task-99",
            EventType: "role.execution.completed",
            Payload: "{\"role\":\"builder\"}",
            OccurredAt: new DateTimeOffset(2025, 6, 2, 9, 30, 0, TimeSpan.Zero),
            TaskSequence: 3L,
            RunSequence: 7L);

    [Fact]
    public void TaskExecutionEventDto_MapsAllFields()
    {
        var evt = BuildEvent();
        TaskExecutionEventDto dto = TaskSnapshotMapper.ToDto(evt);

        Assert.Equal("evt-1", dto.EventId);
        Assert.Equal("run-42", dto.RunId);
        Assert.Equal("task-99", dto.TaskId);
        Assert.Equal("role.execution.completed", dto.EventType);
        Assert.Equal("{\"role\":\"builder\"}", dto.Payload);
        Assert.Equal(evt.OccurredAt, dto.OccurredAt);
        Assert.Equal(3L, dto.TaskSequence);
        Assert.Equal(7L, dto.RunSequence);
    }

    [Fact]
    public void TaskExecutionEventDto_WithNullPayload_MapsNullPayload()
    {
        var evt = BuildEvent() with { Payload = null };
        TaskExecutionEventDto dto = TaskSnapshotMapper.ToDto(evt);

        Assert.Null(dto.Payload);
        Assert.Equal("evt-1", dto.EventId);
    }

    // ---------------------------------------------------------------
    // JSON contract snapshot tests — fail on any field rename or removal
    // ---------------------------------------------------------------

    private static readonly JsonSerializerOptions _jsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void TaskSnapshotDto_JsonShape_MatchesBaseline()
    {
        var snapshot = new TaskSnapshot(
            TaskId: "task-snap",
            Title: "Snap Task",
            Description: "desc",
            Status: TaskState.Done,
            CreatedAt: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero),
            PlanningOutput: "plan",
            BuildOutput: "build",
            ReviewOutput: "review",
            Summary: "ok",
            Error: null,
            ParentTaskId: "parent-snap",
            ChildTaskIds: new[] { "child-snap" },
            RunId: "run-snap");

        var dto = TaskSnapshotMapper.ToDto(snapshot);
        var json = JsonSerializer.Serialize(dto, _jsonOptions);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("taskId", out _), "Missing: taskId");
        Assert.True(root.TryGetProperty("title", out _), "Missing: title");
        Assert.True(root.TryGetProperty("description", out _), "Missing: description");
        Assert.True(root.TryGetProperty("status", out _), "Missing: status");
        Assert.True(root.TryGetProperty("createdAt", out _), "Missing: createdAt");
        Assert.True(root.TryGetProperty("updatedAt", out _), "Missing: updatedAt");
        Assert.True(root.TryGetProperty("planningOutput", out _), "Missing: planningOutput");
        Assert.True(root.TryGetProperty("buildOutput", out _), "Missing: buildOutput");
        Assert.True(root.TryGetProperty("reviewOutput", out _), "Missing: reviewOutput");
        Assert.True(root.TryGetProperty("summary", out _), "Missing: summary");
        Assert.True(root.TryGetProperty("error", out _), "Missing: error");
        Assert.True(root.TryGetProperty("parentTaskId", out _), "Missing: parentTaskId");
        Assert.True(root.TryGetProperty("childTaskIds", out _), "Missing: childTaskIds");
        Assert.True(root.TryGetProperty("runId", out _), "Missing: runId");
        Assert.Equal(14, root.EnumerateObject().Count());
    }

    [Fact]
    public void RunDto_JsonShape_MatchesBaseline()
    {
        var entry = BuildRunEntry();
        var dto = TaskSnapshotMapper.ToDto(entry);
        var json = JsonSerializer.Serialize(dto, _jsonOptions);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("runId", out _), "Missing: runId");
        Assert.True(root.TryGetProperty("title", out _), "Missing: title");
        Assert.True(root.TryGetProperty("createdAt", out _), "Missing: createdAt");
        Assert.Equal(3, root.EnumerateObject().Count());
    }

    [Fact]
    public void TaskExecutionEventDto_JsonShape_MatchesBaseline()
    {
        var evt = BuildEvent();
        var dto = TaskSnapshotMapper.ToDto(evt);
        var json = JsonSerializer.Serialize(dto, _jsonOptions);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("eventId", out _), "Missing: eventId");
        Assert.True(root.TryGetProperty("runId", out _), "Missing: runId");
        Assert.True(root.TryGetProperty("taskId", out _), "Missing: taskId");
        Assert.True(root.TryGetProperty("eventType", out _), "Missing: eventType");
        Assert.True(root.TryGetProperty("payload", out _), "Missing: payload");
        Assert.True(root.TryGetProperty("occurredAt", out _), "Missing: occurredAt");
        Assert.True(root.TryGetProperty("taskSequence", out _), "Missing: taskSequence");
        Assert.True(root.TryGetProperty("runSequence", out _), "Missing: runSequence");
        Assert.Equal(8, root.EnumerateObject().Count());
    }
}
