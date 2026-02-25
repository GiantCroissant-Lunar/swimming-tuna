using SwarmAssistant.Runtime.Dto;
using SwarmAssistant.Runtime.Tasks;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tests;

public sealed class TaskSnapshotMapperTests
{
    private static TaskSnapshot BuildSnapshot(string taskId = "task-1") =>
        new TaskSnapshot(
            TaskId: taskId,
            Title: "Test Task",
            Description: "A description",
            Status: TaskState.Done,
            CreatedAt: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero),
            PlanningOutput: "plan output",
            BuildOutput: "build output",
            ReviewOutput: "review output",
            Summary: "all good",
            Error: null,
            ParentTaskId: "parent-1",
            ChildTaskIds: new[] { "child-1", "child-2" },
            RunId: "run-42",
            Artifacts:
            [
                new TaskArtifact(
                    ArtifactId: "art-abc123",
                    RunId: "run-42",
                    TaskId: taskId,
                    AgentId: "builder-01",
                    Type: TaskArtifactTypes.File,
                    Path: "src/Foo.cs",
                    ContentHash: "sha256:abc123",
                    CreatedAt: new DateTimeOffset(2025, 1, 2, 1, 0, 0, TimeSpan.Zero),
                    Metadata: new Dictionary<string, string> { ["language"] = "csharp" })
            ]
        );

    [Fact]
    public void ToDto_MapsAllFields()
    {
        var snapshot = BuildSnapshot();
        TaskSnapshotDto dto = TaskSnapshotMapper.ToDto(snapshot);

        Assert.Equal("task-1", dto.TaskId);
        Assert.Equal("Test Task", dto.Title);
        Assert.Equal("A description", dto.Description);
        Assert.Equal("done", dto.Status);
        Assert.Equal(snapshot.CreatedAt, dto.CreatedAt);
        Assert.Equal(snapshot.UpdatedAt, dto.UpdatedAt);
        Assert.Equal("plan output", dto.PlanningOutput);
        Assert.Equal("build output", dto.BuildOutput);
        Assert.Equal("review output", dto.ReviewOutput);
        Assert.Equal("all good", dto.Summary);
        Assert.Null(dto.Error);
        Assert.Equal("parent-1", dto.ParentTaskId);
        Assert.Equal(new[] { "child-1", "child-2" }, dto.ChildTaskIds);
        Assert.Equal("run-42", dto.RunId);
        Assert.NotNull(dto.Artifacts);
        Assert.Single(dto.Artifacts!);
        Assert.Equal("art-abc123", dto.Artifacts[0].ArtifactId);
        Assert.Equal("src/Foo.cs", dto.Artifacts[0].Path);
    }

    [Theory]
    [InlineData(TaskState.Queued, "queued")]
    [InlineData(TaskState.Planning, "planning")]
    [InlineData(TaskState.Building, "building")]
    [InlineData(TaskState.Reviewing, "reviewing")]
    [InlineData(TaskState.Done, "done")]
    [InlineData(TaskState.Blocked, "blocked")]
    public void ToDto_Status_IsLowerCaseString(TaskState status, string expected)
    {
        var snapshot = BuildSnapshot() with { Status = status };
        var dto = TaskSnapshotMapper.ToDto(snapshot);

        Assert.Equal(expected, dto.Status);
    }

    [Fact]
    public void ToSummaryDto_MapsRequiredFields()
    {
        var snapshot = BuildSnapshot();
        TaskSummaryDto dto = TaskSnapshotMapper.ToSummaryDto(snapshot);

        Assert.Equal("task-1", dto.TaskId);
        Assert.Equal("Test Task", dto.Title);
        Assert.Equal("done", dto.Status);
        Assert.Equal(snapshot.UpdatedAt, dto.UpdatedAt);
        Assert.Null(dto.Error);
    }

    [Fact]
    public void ToSummaryDto_WithError_MapsErrorField()
    {
        var snapshot = BuildSnapshot() with { Status = TaskState.Blocked, Error = "something went wrong" };
        var dto = TaskSnapshotMapper.ToSummaryDto(snapshot);

        Assert.Equal("blocked", dto.Status);
        Assert.Equal("something went wrong", dto.Error);
    }

    [Fact]
    public void ToDto_WithNullOptionalFields_ProducesNullsInDto()
    {
        var snapshot = new TaskSnapshot(
            TaskId: "task-null",
            Title: "Minimal",
            Description: "Desc",
            Status: TaskState.Queued,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow
        );

        var dto = TaskSnapshotMapper.ToDto(snapshot);

        Assert.Null(dto.PlanningOutput);
        Assert.Null(dto.BuildOutput);
        Assert.Null(dto.ReviewOutput);
        Assert.Null(dto.Summary);
        Assert.Null(dto.Error);
        Assert.Null(dto.ParentTaskId);
        Assert.Null(dto.ChildTaskIds);
        Assert.Null(dto.RunId);
        Assert.Null(dto.Artifacts);
    }
}
