using Akka.Actor;
using Akka.TestKit.Xunit2;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Runtime.Actors;

namespace SwarmAssistant.Runtime.Tests;

public sealed class ConsensusActorTests : TestKit
{
    private readonly IActorRef _consensusActor;

    public ConsensusActorTests()
    {
        _consensusActor = Sys.ActorOf(Props.Create(() => new ConsensusActor(NullLogger<ConsensusActor>.Instance)));
    }

    [Fact]
    public void Consensus_Majority_Approved()
    {
        var taskId = $"task-{Guid.NewGuid():N}";
        _consensusActor.Tell(new ConsensusRequest(taskId, "artifact", 3, "majority"));

        _consensusActor.Tell(new ConsensusVote(taskId, "voter1", true, 1.0, "LGTM"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter2", false, 1.0, "Reject"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter3", true, 1.0, "Looks good"));

        var result = ExpectMsg<ConsensusResult>();
        Assert.Equal(taskId, result.TaskId);
        Assert.True(result.Approved);
        Assert.Equal(3, result.Votes.Count);
    }

    [Fact]
    public void Consensus_Majority_Rejected()
    {
        var taskId = $"task-{Guid.NewGuid():N}";
        _consensusActor.Tell(new ConsensusRequest(taskId, "artifact", 3, "majority"));

        _consensusActor.Tell(new ConsensusVote(taskId, "voter1", false, 1.0, "Reject"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter2", false, 1.0, "Reject again"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter3", true, 1.0, "LGTM"));

        var result = ExpectMsg<ConsensusResult>();
        Assert.Equal(taskId, result.TaskId);
        Assert.False(result.Approved);
        Assert.Equal(3, result.Votes.Count);
    }

    [Fact]
    public void Consensus_Unanimous_RejectedWhenOneFails()
    {
        var taskId = $"task-{Guid.NewGuid():N}";
        _consensusActor.Tell(new ConsensusRequest(taskId, "artifact", 3, "unanimous"));

        _consensusActor.Tell(new ConsensusVote(taskId, "voter1", true, 1.0, "LGTM"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter2", true, 1.0, "LGTM"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter3", false, 1.0, "Reject"));

        var result = ExpectMsg<ConsensusResult>();
        Assert.Equal(taskId, result.TaskId);
        Assert.False(result.Approved);
    }

    [Fact]
    public void Consensus_Unanimous_Approved()
    {
        var taskId = $"task-{Guid.NewGuid():N}";
        _consensusActor.Tell(new ConsensusRequest(taskId, "artifact", 2, "unanimous"));

        _consensusActor.Tell(new ConsensusVote(taskId, "voter1", true, 1.0, "LGTM"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter2", true, 1.0, "LGTM"));

        var result = ExpectMsg<ConsensusResult>();
        Assert.Equal(taskId, result.TaskId);
        Assert.True(result.Approved);
    }

    [Fact]
    public void Consensus_Weighted_Approved()
    {
        var taskId = $"task-{Guid.NewGuid():N}";
        _consensusActor.Tell(new ConsensusRequest(taskId, "artifact", 3, "weighted"));

        _consensusActor.Tell(new ConsensusVote(taskId, "voter1", true, 2.0, "High confidence LGTM"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter2", false, 1.0, "Reject"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter3", false, 0.5, "Reject"));

        var result = ExpectMsg<ConsensusResult>();
        Assert.Equal(taskId, result.TaskId);
        Assert.True(result.Approved); // 2.0 vs 1.5
    }

    [Fact]
    public void Consensus_Weighted_Rejected()
    {
        var taskId = $"task-{Guid.NewGuid():N}";
        _consensusActor.Tell(new ConsensusRequest(taskId, "artifact", 3, "weighted"));

        _consensusActor.Tell(new ConsensusVote(taskId, "voter1", true, 1.0, "LGTM"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter2", true, 1.0, "LGTM"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter3", false, 3.0, "High confidence reject"));

        var result = ExpectMsg<ConsensusResult>();
        Assert.Equal(taskId, result.TaskId);
        Assert.False(result.Approved); // 2.0 vs 3.0
    }

    [Fact]
    public void Consensus_SingleVote_BackwardCompatibility()
    {
        var taskId = $"task-{Guid.NewGuid():N}";
        _consensusActor.Tell(new ConsensusRequest(taskId, "artifact", 1, "majority"));

        _consensusActor.Tell(new ConsensusVote(taskId, "voter1", true, 1.0, "LGTM"));

        var result = ExpectMsg<ConsensusResult>();
        Assert.Equal(taskId, result.TaskId);
        Assert.True(result.Approved);
        Assert.Single(result.Votes);
    }
}
