using Akka.Actor;
using Akka.TestKit.Xunit2;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Runtime.Actors;

namespace SwarmAssistant.Runtime.Tests;

public sealed class BlackboardActorTests : TestKit
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });

    private IActorRef CreateBlackboardActor()
    {
        return Sys.ActorOf(Props.Create(() => new BlackboardActor(_loggerFactory)));
    }

    [Fact]
    public void UpdateGlobalBlackboard_GetGlobalContext_ReturnsValue()
    {
        var blackboard = CreateBlackboardActor();

        blackboard.Tell(new UpdateGlobalBlackboard("adapter_success_rate", "0.95"));
        blackboard.Tell(new GetGlobalContext());

        var context = ExpectMsg<GlobalBlackboardContext>();
        Assert.Single(context.Entries);
        Assert.Equal("0.95", context.Entries["adapter_success_rate"]);
    }

    [Fact]
    public void UpdateGlobalBlackboard_UpdatesExistingKey()
    {
        var blackboard = CreateBlackboardActor();

        blackboard.Tell(new UpdateGlobalBlackboard("adapter_success_rate", "0.95"));
        blackboard.Tell(new UpdateGlobalBlackboard("adapter_success_rate", "0.87"));
        blackboard.Tell(new GetGlobalContext());

        var context = ExpectMsg<GlobalBlackboardContext>();
        Assert.Single(context.Entries);
        Assert.Equal("0.87", context.Entries["adapter_success_rate"]);
    }

    [Fact]
    public void UpdateGlobalBlackboard_PublishesToEventStream()
    {
        var blackboard = CreateBlackboardActor();

        // Subscribe to EventStream for global blackboard changes
        Sys.EventStream.Subscribe(TestActor, typeof(GlobalBlackboardChanged));

        blackboard.Tell(new UpdateGlobalBlackboard("task_pattern", "build_retry"));

        var changed = ExpectMsg<GlobalBlackboardChanged>();
        Assert.Equal("task_pattern", changed.Key);
        Assert.Equal("build_retry", changed.Value);
    }

    [Fact]
    public void GetGlobalContext_ReturnsEmpty_WhenNoEntries()
    {
        var blackboard = CreateBlackboardActor();

        blackboard.Tell(new GetGlobalContext());

        var context = ExpectMsg<GlobalBlackboardContext>();
        Assert.Empty(context.Entries);
    }

    [Fact]
    public void PerTaskBlackboard_IsolatedFromGlobalBlackboard()
    {
        var blackboard = CreateBlackboardActor();

        // Add task-specific entry
        blackboard.Tell(new UpdateBlackboard("task-1", "status", "building"));

        // Add global entry
        blackboard.Tell(new UpdateGlobalBlackboard("adapter_status", "healthy"));

        // Get task context - should only have task entry
        blackboard.Tell(new GetBlackboardContext("task-1"));
        var taskContext = ExpectMsg<BlackboardContext>();
        Assert.Single(taskContext.Entries);
        Assert.Equal("building", taskContext.Entries["status"]);

        // Get global context - should only have global entry
        blackboard.Tell(new GetGlobalContext());
        var globalContext = ExpectMsg<GlobalBlackboardContext>();
        Assert.Single(globalContext.Entries);
        Assert.Equal("healthy", globalContext.Entries["adapter_status"]);
    }

    [Fact]
    public void MultipleTasks_IsolatedFromEachOther()
    {
        var blackboard = CreateBlackboardActor();

        blackboard.Tell(new UpdateBlackboard("task-1", "step", "plan"));
        blackboard.Tell(new UpdateBlackboard("task-2", "step", "build"));

        blackboard.Tell(new GetBlackboardContext("task-1"));
        var context1 = ExpectMsg<BlackboardContext>();
        Assert.Equal("plan", context1.Entries["step"]);

        blackboard.Tell(new GetBlackboardContext("task-2"));
        var context2 = ExpectMsg<BlackboardContext>();
        Assert.Equal("build", context2.Entries["step"]);
    }

    [Fact]
    public void GlobalBlackboard_SharedAcrossAllTasks()
    {
        var blackboard = CreateBlackboardActor();

        // Multiple updates to global blackboard
        blackboard.Tell(new UpdateGlobalBlackboard("metric_1", "value_1"));
        blackboard.Tell(new UpdateGlobalBlackboard("metric_2", "value_2"));
        blackboard.Tell(new UpdateGlobalBlackboard("metric_3", "value_3"));

        blackboard.Tell(new GetGlobalContext());
        var context = ExpectMsg<GlobalBlackboardContext>();
        Assert.Equal(3, context.Entries.Count);
        Assert.Equal("value_1", context.Entries["metric_1"]);
        Assert.Equal("value_2", context.Entries["metric_2"]);
        Assert.Equal("value_3", context.Entries["metric_3"]);
    }

    [Fact]
    public void RemoveBlackboard_DoesNotAffectGlobalBoard()
    {
        var blackboard = CreateBlackboardActor();

        // Add task and global entries
        blackboard.Tell(new UpdateBlackboard("task-1", "data", "value"));
        blackboard.Tell(new UpdateGlobalBlackboard("global_data", "global_value"));

        // Remove task blackboard
        blackboard.Tell(new RemoveBlackboard("task-1"));

        // Global should still exist
        blackboard.Tell(new GetGlobalContext());
        var globalContext = ExpectMsg<GlobalBlackboardContext>();
        Assert.Single(globalContext.Entries);
        Assert.Equal("global_value", globalContext.Entries["global_data"]);

        // Task should be empty
        blackboard.Tell(new GetBlackboardContext("task-1"));
        var taskContext = ExpectMsg<BlackboardContext>();
        Assert.Empty(taskContext.Entries);
    }

    [Fact]
    public void RemoveBlackboard_DoesNotAffectOtherTasks()
    {
        var blackboard = CreateBlackboardActor();

        // Add entries for two tasks
        blackboard.Tell(new UpdateBlackboard("task-1", "key", "value1"));
        blackboard.Tell(new UpdateBlackboard("task-2", "key", "value2"));

        // Remove task-1
        blackboard.Tell(new RemoveBlackboard("task-1"));

        // task-2 should still have its entry
        blackboard.Tell(new GetBlackboardContext("task-2"));
        var context = ExpectMsg<BlackboardContext>();
        Assert.Single(context.Entries);
        Assert.Equal("value2", context.Entries["key"]);
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
