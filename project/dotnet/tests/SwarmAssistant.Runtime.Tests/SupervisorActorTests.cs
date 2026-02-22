using Akka.Actor;
using Akka.TestKit.Xunit2;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Telemetry;
using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Tests;

public sealed class SupervisorActorTests : TestKit
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });

    private RuntimeTelemetry CreateTelemetry()
    {
        return new RuntimeTelemetry(new RuntimeOptions(), _loggerFactory);
    }

    [Fact]
    public void Snapshot_ReturnsInitialZeros()
    {
        var telemetry = CreateTelemetry();
        var supervisor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, telemetry, null, null)));

        supervisor.Tell(new GetSupervisorSnapshot());
        var snapshot = ExpectMsg<SupervisorSnapshot>();

        Assert.Equal(0, snapshot.Started);
        Assert.Equal(0, snapshot.Completed);
        Assert.Equal(0, snapshot.Failed);
        Assert.Equal(0, snapshot.Escalations);
    }

    [Fact]
    public void Snapshot_TracksStartedAndCompleted()
    {
        var telemetry = CreateTelemetry();
        var supervisor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, telemetry, null, null)));

        supervisor.Tell(new TaskStarted("task-1", TaskState.Planning, DateTimeOffset.UtcNow, "coordinator"));
        supervisor.Tell(new TaskResult("task-1", TaskState.Done, "output", DateTimeOffset.UtcNow, "coordinator"));

        supervisor.Tell(new GetSupervisorSnapshot());
        var snapshot = ExpectMsg<SupervisorSnapshot>();

        Assert.Equal(1, snapshot.Started);
        Assert.Equal(1, snapshot.Completed);
    }

    [Fact]
    public void Snapshot_TracksFailedAndEscalations()
    {
        var telemetry = CreateTelemetry();
        var supervisor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, telemetry, null, null)));

        supervisor.Tell(new TaskFailed("task-1", TaskState.Blocked, "error", DateTimeOffset.UtcNow, "coordinator"));
        supervisor.Tell(new EscalationRaised("task-1", "reason", 1, DateTimeOffset.UtcNow, "coordinator"));

        supervisor.Tell(new GetSupervisorSnapshot());
        var snapshot = ExpectMsg<SupervisorSnapshot>();

        Assert.Equal(1, snapshot.Failed);
        Assert.Equal(1, snapshot.Escalations);
    }

    [Fact]
    public void RoleFailureReport_RetriableError_SendsRetryRole()
    {
        var telemetry = CreateTelemetry();
        var supervisor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, telemetry, null, null)));

        // Register a coordinator ref by sending TaskStarted from TestActor
        supervisor.Tell(new TaskStarted("task-1", TaskState.Building, DateTimeOffset.UtcNow, "coordinator"));

        // Send a retriable failure report
        supervisor.Tell(new RoleFailureReport(
            "task-1",
            SwarmRole.Builder,
            "copilot: execution timeout",
            0,
            DateTimeOffset.UtcNow));

        // Supervisor should send RetryRole back to the coordinator (TestActor = Sender of TaskStarted)
        var retry = ExpectMsg<RetryRole>();
        Assert.Equal("task-1", retry.TaskId);
        Assert.Equal(SwarmRole.Builder, retry.Role);
        Assert.Contains("retry #1", retry.Reason);
    }

    [Fact]
    public void RoleFailureReport_SimulatedError_DoesNotRetry()
    {
        var telemetry = CreateTelemetry();
        var supervisor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, telemetry, null, null)));

        supervisor.Tell(new TaskStarted("task-1", TaskState.Building, DateTimeOffset.UtcNow, "coordinator"));

        // Simulated errors are not retriable
        supervisor.Tell(new RoleFailureReport(
            "task-1",
            SwarmRole.Builder,
            "Simulated builder failure for phase testing.",
            0,
            DateTimeOffset.UtcNow));

        ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void RoleFailureReport_ExceedsMaxRetries_StopsRetrying()
    {
        var telemetry = CreateTelemetry();
        var supervisor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, telemetry, null, null)));

        supervisor.Tell(new TaskStarted("task-1", TaskState.Building, DateTimeOffset.UtcNow, "coordinator"));

        // Send 3 failures (MaxRetriesPerTask = 3), should get 3 retries
        for (var i = 0; i < 3; i++)
        {
            supervisor.Tell(new RoleFailureReport(
                "task-1", SwarmRole.Builder, "adapter failure", i, DateTimeOffset.UtcNow));
            ExpectMsg<RetryRole>();
        }

        // 4th failure should NOT trigger retry
        supervisor.Tell(new RoleFailureReport(
            "task-1", SwarmRole.Builder, "adapter failure", 3, DateTimeOffset.UtcNow));
        ExpectNoMsg(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void AdapterCircuitBreaker_OpensAfterThreshold()
    {
        var telemetry = CreateTelemetry();
        var supervisor = Sys.ActorOf(
            Props.Create(() => new SupervisorActor(_loggerFactory, telemetry, null, null)));

        // Subscribe to EventStream for circuit open events
        Sys.EventStream.Subscribe(TestActor, typeof(AdapterCircuitOpen));

        // Register coordinators for each task (TestActor is the Sender)
        for (var i = 0; i < 3; i++)
        {
            supervisor.Tell(new TaskStarted($"task-{i}", TaskState.Building, DateTimeOffset.UtcNow, "coordinator"));
        }

        // Send 3 failures mentioning "copilot" (AdapterCircuitThreshold = 3)
        for (var i = 0; i < 3; i++)
        {
            supervisor.Tell(new RoleFailureReport(
                $"task-{i}", SwarmRole.Builder, "copilot: execution timeout", 0, DateTimeOffset.UtcNow));
        }

        // Consume all messages (RetryRole + AdapterCircuitOpen) and find the circuit open event
        var messages = ReceiveN(4, TimeSpan.FromSeconds(2));
        var circuitOpen = messages.OfType<AdapterCircuitOpen>().FirstOrDefault();
        Assert.NotNull(circuitOpen);
        Assert.Equal("copilot", circuitOpen.AdapterId);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loggerFactory.Dispose();
        }

        base.Dispose(disposing);
    }
}
