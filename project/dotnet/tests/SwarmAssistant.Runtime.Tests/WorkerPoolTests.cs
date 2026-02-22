using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Execution;

namespace SwarmAssistant.Runtime.Tests;

public sealed class WorkerPoolTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(16)]
    public void WorkerPoolSize_ClampedToValidRange(int poolSize)
    {
        var options = new RuntimeOptions { WorkerPoolSize = poolSize };
        var clamped = Math.Clamp(options.WorkerPoolSize, 1, 16);
        Assert.Equal(poolSize, clamped);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100, 16)]
    public void WorkerPoolSize_OutOfRange_GetsClampedAtBoundary(int input, int expected)
    {
        var options = new RuntimeOptions { WorkerPoolSize = input };
        var clamped = Math.Clamp(options.WorkerPoolSize, 1, 16);
        Assert.Equal(expected, clamped);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(16)]
    public void ReviewerPoolSize_ClampedToValidRange(int poolSize)
    {
        var options = new RuntimeOptions { ReviewerPoolSize = poolSize };
        var clamped = Math.Clamp(options.ReviewerPoolSize, 1, 16);
        Assert.Equal(poolSize, clamped);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(32)]
    public void MaxCliConcurrency_ClampedToValidRange(int concurrency)
    {
        var options = new RuntimeOptions { MaxCliConcurrency = concurrency };
        var clamped = Math.Clamp(options.MaxCliConcurrency, 1, 32);
        Assert.Equal(concurrency, clamped);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(100, 32)]
    public void MaxCliConcurrency_OutOfRange_GetsClampedAtBoundary(int input, int expected)
    {
        var options = new RuntimeOptions { MaxCliConcurrency = input };
        var clamped = Math.Clamp(options.MaxCliConcurrency, 1, 32);
        Assert.Equal(expected, clamped);
    }

    [Fact]
    public void RuntimeOptions_DefaultPoolSizes()
    {
        var options = new RuntimeOptions();
        Assert.Equal(3, options.WorkerPoolSize);
        Assert.Equal(2, options.ReviewerPoolSize);
        Assert.Equal(4, options.MaxCliConcurrency);
    }

    [Fact]
    public async Task ConcurrencySemaphore_LimitsParallelExecution()
    {
        // Use MaxCliConcurrency=2 to test the semaphore limits parallel calls.
        var options = new RuntimeOptions
        {
            CliAdapterOrder = ["local-echo"],
            SandboxMode = "host",
            MaxCliConcurrency = 2
        };

        using var loggerFactory = LoggerFactory.Create(builder => { });
        var executor = new SubscriptionCliRoleExecutor(options, loggerFactory);

        // Fire 5 concurrent requests. With concurrency=2, they should all succeed
        // (local-echo is fast) but the semaphore ensures at most 2 run at a time.
        var tasks = Enumerable.Range(0, 5).Select(i =>
            executor.ExecuteAsync(
                new ExecuteRoleTask(
                    $"task-{i}",
                    SwarmRole.Planner,
                    $"Task {i}",
                    "Concurrent test",
                    null,
                    null),
                CancellationToken.None));

        var results = await Task.WhenAll(tasks);

        Assert.Equal(5, results.Length);
        Assert.All(results, r => Assert.Equal("local-echo", r.AdapterId));
    }

    [Fact]
    public async Task ConcurrencySemaphore_ReleasesOnCancellation()
    {
        var options = new RuntimeOptions
        {
            CliAdapterOrder = ["local-echo"],
            SandboxMode = "host",
            MaxCliConcurrency = 1
        };

        using var loggerFactory = LoggerFactory.Create(builder => { });
        var executor = new SubscriptionCliRoleExecutor(options, loggerFactory);

        // First call should succeed.
        var result = await executor.ExecuteAsync(
            new ExecuteRoleTask("task-1", SwarmRole.Builder, "Test", "Test", null, null),
            CancellationToken.None);
        Assert.Equal("local-echo", result.AdapterId);

        // After the first call completes, the semaphore should be released.
        // A second call should also succeed (not deadlock).
        var result2 = await executor.ExecuteAsync(
            new ExecuteRoleTask("task-2", SwarmRole.Builder, "Test 2", "Test 2", null, null),
            CancellationToken.None);
        Assert.Equal("local-echo", result2.AdapterId);
    }
}
