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

        _consensusActor.Tell(new ConsensusVote(taskId, "voter1", true, 0.8, "High confidence LGTM"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter2", false, 0.4, "Reject"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter3", false, 0.2, "Reject"));

        var result = ExpectMsg<ConsensusResult>();
        Assert.Equal(taskId, result.TaskId);
        Assert.True(result.Approved); // 0.8 vs 0.6
    }

    [Fact]
    public void Consensus_Weighted_Rejected()
    {
        var taskId = $"task-{Guid.NewGuid():N}";
        _consensusActor.Tell(new ConsensusRequest(taskId, "artifact", 3, "weighted"));

        _consensusActor.Tell(new ConsensusVote(taskId, "voter1", true, 0.3, "LGTM"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter2", true, 0.3, "LGTM"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter3", false, 0.9, "High confidence reject"));

        var result = ExpectMsg<ConsensusResult>();
        Assert.Equal(taskId, result.TaskId);
        Assert.False(result.Approved); // 0.6 vs 0.9
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

    [Fact]
    public void Consensus_Timeout_When_Votes_Missing()
    {
        var taskId = $"task-{Guid.NewGuid():N}";
        _consensusActor.Tell(new ConsensusRequest(taskId, "artifact", 3, "majority"));

        // Submit only 2 of the 3 required votes
        _consensusActor.Tell(new ConsensusVote(taskId, "voter1", true, 1.0, "LGTM"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter2", true, 1.0, "LGTM"));

        // Directly inject a timeout message to simulate the session timer firing
        // without waiting for the 5-minute wall-clock timeout
        _consensusActor.Tell(new ConsensusSessionTimeout(taskId));

        var result = ExpectMsg<ConsensusResult>(TimeSpan.FromSeconds(10));
        Assert.Equal(taskId, result.TaskId);
        // With 2 approvals out of 2 received votes, majority is met — result reflects
        // the votes that arrived rather than a blanket failure
        Assert.Equal(2, result.Votes.Count);
    }

    [Fact]
    public void Consensus_Timeout_With_No_Votes_Returns_NotApproved()
    {
        var taskId = $"task-{Guid.NewGuid():N}";
        _consensusActor.Tell(new ConsensusRequest(taskId, "artifact", 3, "majority"));

        // No votes submitted — inject the timeout directly
        _consensusActor.Tell(new ConsensusSessionTimeout(taskId));

        var result = ExpectMsg<ConsensusResult>(TimeSpan.FromSeconds(10));
        Assert.Equal(taskId, result.TaskId);
        Assert.False(result.Approved);
        Assert.Empty(result.Votes);
    }

    [Fact]
    public void Consensus_EarlyVotes_BufferedAndProcessed()
    {
        var taskId = $"task-{Guid.NewGuid():N}";

        // Votes arrive before the ConsensusRequest (race condition scenario)
        _consensusActor.Tell(new ConsensusVote(taskId, "voter1", true, 1.0, "LGTM"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter2", true, 1.0, "LGTM"));

        // Session is created afterwards
        _consensusActor.Tell(new ConsensusRequest(taskId, "artifact", 2, "majority"));

        var result = ExpectMsg<ConsensusResult>(TimeSpan.FromSeconds(10));
        Assert.Equal(taskId, result.TaskId);
        Assert.True(result.Approved);
        Assert.Equal(2, result.Votes.Count);
    }

    [Fact]
    public void Consensus_NegativeConfidence_ClampedToZero()
    {
        var taskId = $"task-{Guid.NewGuid():N}";
        _consensusActor.Tell(new ConsensusRequest(taskId, "artifact", 2, "weighted"));

        // A negative confidence value on an approval vote should not invert results
        _consensusActor.Tell(new ConsensusVote(taskId, "voter1", true, -1.0, "Approve with bad confidence"));
        _consensusActor.Tell(new ConsensusVote(taskId, "voter2", false, 0.5, "Reject"));

        var result = ExpectMsg<ConsensusResult>(TimeSpan.FromSeconds(10));
        Assert.Equal(taskId, result.TaskId);
        // Clamped: approval weight=0.0, rejection weight=0.5 → rejected
        Assert.False(result.Approved);
    }
}
