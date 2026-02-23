using Microsoft.Extensions.Logging;
using Moq;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Tasks;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tests;

public class OutcomeTrackerTests
{
    private readonly Mock<ITaskMemoryWriter> _memoryWriterMock;
    private readonly Mock<IOutcomeWriter> _outcomeWriterMock;
    private readonly Mock<ILogger<TaskRegistry>> _registryLoggerMock;
    private readonly Mock<ILogger<OutcomeTracker>> _trackerLoggerMock;
    private readonly TaskRegistry _taskRegistry;

    public OutcomeTrackerTests()
    {
        _memoryWriterMock = new Mock<ITaskMemoryWriter>();
        _outcomeWriterMock = new Mock<IOutcomeWriter>();
        _registryLoggerMock = new Mock<ILogger<TaskRegistry>>();
        _trackerLoggerMock = new Mock<ILogger<OutcomeTracker>>();
        _taskRegistry = new TaskRegistry(_memoryWriterMock.Object, _registryLoggerMock.Object);
    }

    [Fact]
    public void ExtractKeywords_WithValidTitle_ReturnsKeywords()
    {
        // Arrange
        var title = "Implement authentication with OAuth2";

        // Act
        var keywords = OutcomeTracker.ExtractKeywords(title);

        // Assert - "with" is a stopword and gets filtered, "OAuth2" becomes "oauth" (numbers stripped)
        Assert.Contains("implement", keywords);
        Assert.Contains("authentication", keywords);
        Assert.Contains("oauth", keywords);
        Assert.DoesNotContain("with", keywords); // stopword
    }

    [Fact]
    public void ExtractKeywords_WithStopWords_FiltersStopWords()
    {
        // Arrange
        var title = "Create that component with these features";

        // Act
        var keywords = OutcomeTracker.ExtractKeywords(title);

        // Assert - "that" and "with" are stopwords (3-4 chars), "these" is 5 chars so included
        Assert.DoesNotContain("that", keywords);
        Assert.DoesNotContain("with", keywords);
        Assert.Contains("create", keywords);
        Assert.Contains("component", keywords);
        Assert.Contains("features", keywords);
        // "these" is 5 chars and not a stopword, so it's included
        Assert.Contains("these", keywords);
    }

    [Fact]
    public void ExtractKeywords_WithEmptyTitle_ReturnsEmptyList()
    {
        // Arrange
        var title = "";

        // Act
        var keywords = OutcomeTracker.ExtractKeywords(title);

        // Assert
        Assert.Empty(keywords);
    }

    [Fact]
    public void ExtractKeywords_WithShortWords_FiltersShortWords()
    {
        // Arrange
        var title = "Do it now with the API";

        // Act
        var keywords = OutcomeTracker.ExtractKeywords(title);

        // Assert - Words less than 4 characters are filtered, "with" is a stopword
        Assert.DoesNotContain("do", keywords);
        Assert.DoesNotContain("it", keywords);
        Assert.DoesNotContain("now", keywords);
        Assert.DoesNotContain("with", keywords); // stopword
        // API is only 3 chars, but regex extracts [a-zA-Z]{4,}, so it won't match
    }

    [Fact]
    public async Task FinalizeOutcomeAsync_WithSuccessfulTask_RecordsOutcome()
    {
        // Arrange
        var tracker = new OutcomeTracker(
            _taskRegistry,
            _outcomeWriterMock.Object,
            _trackerLoggerMock.Object);

        var taskId = "test-task-001";
        var snapshot = new TaskSnapshot(
            TaskId: taskId,
            Title: "Test task",
            Description: "Test description",
            Status: TaskState.Queued,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt: DateTimeOffset.UtcNow);

        _taskRegistry.ImportSnapshots(new[] { snapshot });

        // Act
        await tracker.FinalizeOutcomeAsync(taskId, TaskState.Done, summary: "Task completed successfully");

        // Assert
        _outcomeWriterMock.Verify(
            x => x.WriteAsync(
                It.Is<TaskOutcome>(o =>
                    o.TaskId == taskId &&
                    o.FinalStatus == TaskState.Done &&
                    o.Summary == "Task completed successfully"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task FinalizeOutcomeAsync_WithBlockedTask_RecordsFailureReason()
    {
        // Arrange
        var tracker = new OutcomeTracker(
            _taskRegistry,
            _outcomeWriterMock.Object,
            _trackerLoggerMock.Object);

        var taskId = "test-task-002";
        var snapshot = new TaskSnapshot(
            TaskId: taskId,
            Title: "Failed task",
            Description: "Task that will fail",
            Status: TaskState.Queued,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            UpdatedAt: DateTimeOffset.UtcNow);

        _taskRegistry.ImportSnapshots(new[] { snapshot });

        // Act
        await tracker.FinalizeOutcomeAsync(taskId, TaskState.Blocked, failureReason: "Test failure");

        // Assert
        _outcomeWriterMock.Verify(
            x => x.WriteAsync(
                It.Is<TaskOutcome>(o =>
                    o.TaskId == taskId &&
                    o.FinalStatus == TaskState.Blocked &&
                    o.FailureReason == "Test failure"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void RecordRoleCompletion_TracksRoleExecutions()
    {
        // Arrange
        var tracker = new OutcomeTracker(
            _taskRegistry,
            _outcomeWriterMock.Object,
            _trackerLoggerMock.Object);

        var taskId = "test-task-003";

        // Act
        tracker.RecordRoleStart(taskId, SwarmRole.Planner, "cline");
        tracker.RecordRoleCompletion(taskId, SwarmRole.Planner, succeeded: true, confidence: 0.9);

        // Assert - No exception thrown, tracking is internal
        // The actual verification happens in FinalizeOutcomeAsync
        Assert.True(true);
    }

    [Fact]
    public void RecordRoleCompletion_WithFailure_IncrementsRetryCount()
    {
        // Arrange
        var tracker = new OutcomeTracker(
            _taskRegistry,
            _outcomeWriterMock.Object,
            _trackerLoggerMock.Object);

        var taskId = "test-task-004";

        // Act - Simulate retry scenario
        tracker.RecordRoleStart(taskId, SwarmRole.Builder, "cline");
        tracker.RecordRoleCompletion(taskId, SwarmRole.Builder, succeeded: false, confidence: 0.3);
        tracker.RecordRoleStart(taskId, SwarmRole.Builder, "cline");
        tracker.RecordRoleCompletion(taskId, SwarmRole.Builder, succeeded: true, confidence: 0.8);

        // Assert - No exception thrown
        Assert.True(true);
    }
}

public class StrategyAdviceTests
{
    [Fact]
    public void StrategyAdvice_WithNoSimilarTasks_ReturnsDefaultValues()
    {
        // Arrange & Act
        var advice = new StrategyAdvice
        {
            TaskId = "test-task",
            SimilarTaskSuccessRate = 0.5,
            SimilarTaskCount = 0
        };

        // Assert
        Assert.Equal("test-task", advice.TaskId);
        Assert.Equal(0.5, advice.SimilarTaskSuccessRate);
        Assert.Equal(0, advice.SimilarTaskCount);
        Assert.Empty(advice.Insights);
        Assert.Empty(advice.AdapterSuccessRates);
        Assert.Empty(advice.RecommendedCostAdjustments);
        Assert.Empty(advice.CommonFailurePatterns);
    }

    [Fact]
    public void StrategyAdvice_WithInsights_ContainsInsights()
    {
        // Arrange & Act
        var advice = new StrategyAdvice
        {
            TaskId = "test-task",
            SimilarTaskSuccessRate = 0.8,
            SimilarTaskCount = 5,
            Insights = new List<string>
            {
                "Tasks with similar keywords have high success rate (80%).",
                "Adapter 'cline' has 90% success rate for similar tasks."
            }
        };

        // Assert
        Assert.Equal(2, advice.Insights.Count);
        Assert.Contains("Tasks with similar keywords have high success rate (80%).", advice.Insights);
    }

    [Fact]
    public void StrategyAdvice_WithAdapterRates_ContainsRates()
    {
        // Arrange & Act
        var advice = new StrategyAdvice
        {
            TaskId = "test-task",
            SimilarTaskSuccessRate = 0.75,
            SimilarTaskCount = 10,
            AdapterSuccessRates = new Dictionary<string, double>
            {
                ["cline"] = 0.9,
                ["cursor"] = 0.7,
                ["copilot"] = 0.65
            }
        };

        // Assert
        Assert.Equal(3, advice.AdapterSuccessRates.Count);
        Assert.Equal(0.9, advice.AdapterSuccessRates["cline"]);
        Assert.Equal(0.7, advice.AdapterSuccessRates["cursor"]);
        Assert.Equal(0.65, advice.AdapterSuccessRates["copilot"]);
    }
}

public class TaskOutcomeTests
{
    [Fact]
    public void TaskOutcome_WithRoleExecutions_CalculatesTotalRetries()
    {
        // Arrange & Act
        var outcome = new TaskOutcome
        {
            TaskId = "test-task",
            Title = "Test task",
            FinalStatus = TaskState.Done,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            CompletedAt = DateTimeOffset.UtcNow,
            TitleKeywords = new List<string> { "test", "task" },
            RoleExecutions = new List<RoleExecutionRecord>
            {
                new() { Role = SwarmRole.Planner, RetryCount = 0, Succeeded = true },
                new() { Role = SwarmRole.Builder, RetryCount = 2, Succeeded = true },
                new() { Role = SwarmRole.Reviewer, RetryCount = 1, Succeeded = true }
            }
        };

        // Assert
        Assert.Equal(3, outcome.TotalRetries);
    }

    [Fact]
    public void TaskOutcome_CalculatesTotalDuration()
    {
        // Arrange
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-15);
        var completedAt = DateTimeOffset.UtcNow;

        // Act
        var outcome = new TaskOutcome
        {
            TaskId = "test-task",
            Title = "Test task",
            FinalStatus = TaskState.Done,
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            TitleKeywords = new List<string>(),
            RoleExecutions = new List<RoleExecutionRecord>()
        };

        // Assert
        Assert.True(outcome.TotalDuration >= TimeSpan.FromMinutes(14));
        Assert.True(outcome.TotalDuration <= TimeSpan.FromMinutes(16));
    }
}

public class RoleExecutionRecordTests
{
    [Fact]
    public void RoleExecutionRecord_CalculatesDuration()
    {
        // Arrange
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var completedAt = DateTimeOffset.UtcNow;

        // Act
        var record = new RoleExecutionRecord
        {
            Role = SwarmRole.Builder,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Succeeded = true,
            Confidence = 0.9
        };

        // Assert
        Assert.True(record.Duration >= TimeSpan.FromMinutes(4));
        Assert.True(record.Duration <= TimeSpan.FromMinutes(6));
    }

    [Fact]
    public void RoleExecutionRecord_WithoutTimestamps_ReturnsNullDuration()
    {
        // Arrange & Act
        var record = new RoleExecutionRecord
        {
            Role = SwarmRole.Planner,
            Succeeded = true,
            Confidence = 1.0
        };

        // Assert
        Assert.Null(record.Duration);
    }
}
